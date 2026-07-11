using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using RestSharp;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Net;
using System.Threading.Channels;

namespace KlaviyoCRC;

public interface IKlaviyoCustomerFetcher
{
    Task FetchCustomersAsync(BrandOptions brand, CancellationToken cancellationToken);
}

public class KlaviyoCustomerFetcher : IKlaviyoCustomerFetcher
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<KlaviyoCustomerFetcher> _logger;

    public KlaviyoCustomerFetcher(IConfiguration configuration, ILogger<KlaviyoCustomerFetcher> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task FetchCustomersAsync(BrandOptions brand, CancellationToken cancellationToken)
    {
        string connectionString = _configuration.GetConnectionString("KlaviyoDatabase")
            ?? _configuration["ConnectionStrings:KlaviyoDatabase"]
            ?? "Server=TU_SERVER;Database=TU_DB;Trusted_Connection=True;";

        _logger.LogInformation("[{Brand}] Iniciando lectura y descarga de clientes de Klaviyo usando KlaviyoCustomerFetcher...", brand.Code);

        using var client = new RestClient(brand.BaseUrl);
        var partitions = await BuildPartitionsAsync(client, brand, cancellationToken);

        if (partitions.Count == 0)
        {
            _logger.LogInformation("[{Brand}] No hay clientes para descargar.", brand.Code);
            return;
        }

        // Pipeline productor/consumidor: mientras se inserta una página en la BD, ya se están
        // descargando las siguientes (una o varias en paralelo, según FetchConcurrency). El canal
        // acotado (PrefetchQueueCapacity) actúa como control de presión: si la BD se atrasa, la
        // descarga se frena sola, sin arriesgar acumular memoria sin límite.
        var channel = Channel.CreateBounded<DataTable>(new BoundedChannelOptions(Math.Max(1, brand.PrefetchQueueCapacity))
        {
            SingleReader = true,
            SingleWriter = partitions.Count == 1,
            FullMode = BoundedChannelFullMode.Wait
        });

        var consumerTask = ConsumeAndPersistPagesAsync(brand, connectionString, channel.Reader, cancellationToken);

        Exception? failure = null;
        try
        {
            await Task.WhenAll(partitions.Select(p =>
                ProduceProfilePagesAsync(brand, client, p.Label, p.Filter, channel.Writer, cancellationToken)));
        }
        catch (Exception ex)
        {
            failure = ex;
            _logger.LogError(ex, "[{Brand}] Error descargando clientes desde Klaviyo.", brand.Code);
        }
        finally
        {
            // Completar el canal (con o sin error) para que el consumidor sepa cuándo parar
            // de esperar más páginas y, si hubo falla, la propague tras drenar lo ya encolado.
            channel.Writer.Complete(failure);
        }

        await consumerTask;

        _logger.LogInformation("[{Brand}] Lectura de clientes de Klaviyo finalizada correctamente.", brand.Code);
    }

    // Divide el rango de fechas de creación de los perfiles en tantas particiones como indique
    // FetchConcurrency, cada una con su propio filtro y su propio cursor, para poder descargarlas
    // en paralelo. Klaviyo solo permite 'greater-than'/'less-than' (no '-or-equal') sobre 'created',
    // así que las particiones son contiguas y sin traslape (el filo exacto entre dos particiones
    // es la única franja teóricamente en riesgo, y de perderse un perfil ahí por coincidencia exacta
    // de timestamp, la siguiente corrida periódica lo vuelve a traer).
    private async Task<List<(string Label, string? Filter)>> BuildPartitionsAsync(RestClient client, BrandOptions brand, CancellationToken cancellationToken)
    {
        int concurrency = Math.Max(1, brand.FetchConcurrency);
        if (concurrency == 1)
        {
            return [("1/1", null)];
        }

        var range = await DetermineCreatedRangeAsync(client, brand, cancellationToken);
        if (range == null)
        {
            return [];
        }

        var (min, max) = range.Value;
        if (max <= min)
        {
            return [("1/1", null)];
        }

        var totalTicks = (max - min).Ticks;
        var boundaries = new DateTimeOffset[concurrency - 1];
        for (int i = 0; i < boundaries.Length; i++)
        {
            boundaries[i] = min + TimeSpan.FromTicks(totalTicks * (i + 1) / concurrency);
        }

        var partitions = new List<(string, string?)>();
        for (int i = 0; i < concurrency; i++)
        {
            string label = $"{i + 1}/{concurrency}";
            string filter = i switch
            {
                0 => $"less-than(created,{FormatKlaviyoDate(boundaries[0])})",
                _ when i == concurrency - 1 => $"greater-than(created,{FormatKlaviyoDate(boundaries[i - 1])})",
                _ => $"and(greater-than(created,{FormatKlaviyoDate(boundaries[i - 1])}),less-than(created,{FormatKlaviyoDate(boundaries[i])}))"
            };
            partitions.Add((label, filter));
        }

        _logger.LogInformation("[{Brand}] Descarga paralela en {Concurrency} partición(es) por fecha de creación (rango detectado: {Min:o} a {Max:o}).",
            brand.Code, concurrency, min, max);

        return partitions;
    }

    private static string FormatKlaviyoDate(DateTimeOffset dt) => dt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private async Task<(DateTimeOffset Min, DateTimeOffset Max)?> DetermineCreatedRangeAsync(RestClient client, BrandOptions brand, CancellationToken cancellationToken)
    {
        var oldest = await GetSingleProfileCreatedAsync(client, brand, "created", cancellationToken);
        if (oldest == null)
        {
            return null;
        }

        var newest = await GetSingleProfileCreatedAsync(client, brand, "-created", cancellationToken);
        if (newest == null)
        {
            return null;
        }

        return (oldest.Value, newest.Value);
    }

    private async Task<DateTimeOffset?> GetSingleProfileCreatedAsync(RestClient client, BrandOptions brand, string sort, CancellationToken cancellationToken)
    {
        var request = new RestRequest(brand.ProfilesEndpoint, Method.Get);
        request.AddHeader("Authorization", $"Klaviyo-API-Key {brand.ApiKey}");
        request.AddHeader("accept", brand.Accept);
        request.AddHeader("revision", brand.Revision);
        request.AddQueryParameter("page[size]", "1");
        request.AddQueryParameter("sort", sort);

        var response = await ExecuteWithRetryAsync(client, request, brand, cancellationToken);
        if (string.IsNullOrEmpty(response.Content))
        {
            return null;
        }

        var json = JObject.Parse(response.Content);
        var first = json["data"]?.FirstOrDefault();
        var createdToken = first?["attributes"]?["created"];
        if (createdToken == null || createdToken.Type == JTokenType.Null)
        {
            return null;
        }

        // Newtonsoft detecta el string ISO-8601 y lo parsea a DateTime (no DateTimeOffset) al leer
        // el JSON; leerlo tipado evita que un .ToString() posterior lo reformatee con la cultura
        // regional actual. Klaviyo siempre devuelve el 'created' en UTC.
        var createdUtc = DateTime.SpecifyKind(createdToken.Value<DateTime>(), DateTimeKind.Utc);
        return new DateTimeOffset(createdUtc);
    }

    private async Task ProduceProfilePagesAsync(BrandOptions brand, RestClient client, string partitionLabel, string? filter, ChannelWriter<DataTable> writer, CancellationToken cancellationToken)
    {
        string? nextCursor = null;
        int pageCount = 0;
        int maxPages = brand.MaxPagesToFetch;

        do
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("[{Brand}] [{Partition}] La lectura de clientes de Klaviyo fue cancelada.", brand.Code, partitionLabel);
                break;
            }

            pageCount++;
            var request = new RestRequest(brand.ProfilesEndpoint, Method.Get);
            request.AddHeader("Authorization", $"Klaviyo-API-Key {brand.ApiKey}");
            request.AddHeader("accept", brand.Accept);
            request.AddHeader("revision", brand.Revision);
            request.AddQueryParameter("page[size]", "100");
            if (!string.IsNullOrEmpty(filter))
                request.AddQueryParameter("filter", filter);
            if (!string.IsNullOrEmpty(nextCursor))
                request.AddQueryParameter("page[cursor]", nextCursor);

            _logger.LogInformation("[{Brand}] [{Partition}] Consumiendo API de Klaviyo. Página: {PageCount}, Cursor actual: {Cursor}",
                brand.Code, partitionLabel, pageCount, nextCursor ?? "Inicio");

            var response = await ExecuteWithRetryAsync(client, request, brand, cancellationToken);

            if (string.IsNullOrEmpty(response.Content))
            {
                _logger.LogWarning("[{Brand}] [{Partition}] La respuesta del API de Klaviyo está vacía.", brand.Code, partitionLabel);
                break;
            }

            var json = JObject.Parse(response.Content);
            var dataArray = json["data"];
            if (dataArray == null || !dataArray.Any())
            {
                _logger.LogInformation("[{Brand}] [{Partition}] No se encontraron más clientes en esta partición.", brand.Code, partitionLabel);
                break;
            }

            var table = BuildProfileTable();
            foreach (var item in dataArray)
            {
                var id = item["id"]?.ToString();
                var attr = item["attributes"];

                string? firstName = attr?["first_name"]?.ToString();
                string? lastName = attr?["last_name"]?.ToString();
                string? phone = attr?["phone_number"]?.ToString();
                string? email = attr?["email"]?.ToString();

                var properties = attr?["properties"] as JObject;
                string? identificationNumber = properties?.ContainsKey("identification_number") == true
                    ? properties["identification_number"]?.ToString()
                    : null;
                string? identificationType = properties?.ContainsKey("identification_type") == true
                    ? properties["identification_type"]?.ToString()
                    : null;

                table.Rows.Add(id, firstName, lastName, phone, email, identificationNumber, identificationType);
            }

            // Si el consumidor va atrasado, esta espera es la que frena naturalmente la descarga.
            await writer.WriteAsync(table, cancellationToken);

            var nextLink = json["links"]?["next"]?.ToString();
            nextCursor = null;
            if (!string.IsNullOrEmpty(nextLink) && Uri.TryCreate(nextLink, UriKind.Absolute, out var nextUri))
            {
                nextCursor = nextUri.Query
                    .TrimStart('?')
                    .Split('&')
                    .Select(p => p.Split('='))
                    .Where(p => p.Length == 2 && Uri.UnescapeDataString(p[0]) == "page[cursor]")
                    .Select(p => Uri.UnescapeDataString(p[1]))
                    .FirstOrDefault();
            }

            if (maxPages > 0 && pageCount >= maxPages)
            {
                _logger.LogInformation("[{Brand}] [{Partition}] Se alcanzó el límite de páginas configurado ({MaxPages}). Deteniendo esta partición.",
                    brand.Code, partitionLabel, maxPages);
                break;
            }

        } while (!string.IsNullOrEmpty(nextCursor));
    }

    private async Task ConsumeAndPersistPagesAsync(BrandOptions brand, string connectionString, ChannelReader<DataTable> reader, CancellationToken cancellationToken)
    {
        int batchPageLimit = Math.Max(1, brand.DbBatchPages);
        var batch = BuildProfileTable();
        int pagesInBatch = 0;
        int totalPersisted = 0;

        await foreach (var page in reader.ReadAllAsync(cancellationToken))
        {
            foreach (DataRow row in page.Rows)
            {
                batch.ImportRow(row);
            }
            pagesInBatch++;

            if (pagesInBatch >= batchPageLimit)
            {
                totalPersisted += batch.Rows.Count;
                await InsertBatchAsync(brand, connectionString, batch, totalPersisted, cancellationToken);
                batch = BuildProfileTable();
                pagesInBatch = 0;
            }
        }

        if (batch.Rows.Count > 0)
        {
            totalPersisted += batch.Rows.Count;
            await InsertBatchAsync(brand, connectionString, batch, totalPersisted, cancellationToken);
        }
    }

    private async Task InsertBatchAsync(BrandOptions brand, string connectionString, DataTable batch, int totalPersisted, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[{Brand}] Insertando {RowCount} clientes en la base de datos (Total acumulado: {TotalPersisted})...",
            brand.Code, batch.Rows.Count, totalPersisted);

        using var conn = new SqlConnection(connectionString);
        using var cmd = new SqlCommand(brand.InsertCustomersSp, conn);
        cmd.CommandType = CommandType.StoredProcedure;
        var param = cmd.Parameters.AddWithValue("@Customers", batch);
        param.SqlDbType = SqlDbType.Structured;
        param.TypeName = brand.CustomerType;

        await conn.OpenAsync(cancellationToken);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DataTable BuildProfileTable()
    {
        var table = new DataTable();
        table.Columns.Add("ProfileId", typeof(string));
        table.Columns.Add("FirstName", typeof(string));
        table.Columns.Add("LastName", typeof(string));
        table.Columns.Add("PhoneNumber", typeof(string));
        table.Columns.Add("Email", typeof(string));
        table.Columns.Add("IdentificationNumber", typeof(string));
        table.Columns.Add("IdentificationType", typeof(string));
        return table;
    }

    private async Task<RestResponse> ExecuteWithRetryAsync(RestClient client, RestRequest request, BrandOptions brand, CancellationToken cancellationToken)
    {
        int maxAttempts = Math.Max(1, brand.MaxRetryAttempts);
        int baseDelayMs = Math.Max(1, brand.RetryBaseDelayMs);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var response = await client.ExecuteAsync(request, cancellationToken);

            if (response.IsSuccessful)
            {
                return response;
            }

            bool isRateLimited = response.StatusCode == HttpStatusCode.TooManyRequests;
            bool isTransientServerError = (int)response.StatusCode >= 500;

            if ((isRateLimited || isTransientServerError) && attempt < maxAttempts)
            {
                var delay = GetRetryDelay(response, attempt, baseDelayMs);
                _logger.LogWarning("[{Brand}] Klaviyo respondió {StatusCode} (intento {Attempt}/{MaxAttempts}). Reintentando en {Delay}ms...",
                    brand.Code, response.StatusCode, attempt, maxAttempts, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            _logger.LogError("[{Brand}] Error API: {StatusCode} - {Content}", brand.Code, response.StatusCode, response.Content);
            throw new Exception($"[{brand.Code}] Error API: {response.StatusCode} - {response.Content}");
        }

        throw new Exception($"[{brand.Code}] Se agotaron los reintentos ({maxAttempts}) contra la API de Klaviyo.");
    }

    private static TimeSpan GetRetryDelay(RestResponse response, int attempt, int baseDelayMs)
    {
        var retryAfterHeader = response.Headers?
            .FirstOrDefault(h => string.Equals(h.Name, "Retry-After", StringComparison.OrdinalIgnoreCase));

        if (retryAfterHeader?.Value != null && double.TryParse(retryAfterHeader.Value.ToString(), out var retryAfterSeconds))
        {
            return TimeSpan.FromSeconds(retryAfterSeconds);
        }

        // Backoff exponencial si Klaviyo no indicó cuánto esperar.
        var exponentialMs = baseDelayMs * Math.Pow(2, attempt - 1);
        return TimeSpan.FromMilliseconds(exponentialMs);
    }
}
