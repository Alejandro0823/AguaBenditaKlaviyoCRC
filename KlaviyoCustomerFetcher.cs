using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KlaviyoCRC;

public interface IKlaviyoCustomerFetcher
{
    Task FetchCustomersAsync(CancellationToken cancellationToken);
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

    public async Task FetchCustomersAsync(CancellationToken cancellationToken)
    {
        string apiKey = _configuration["KlaviyoSettings:ApiKey"] ?? "pk_xxxxxxxxxxxxx";
        string baseUrl = _configuration["KlaviyoSettings:BaseUrl"] ?? "https://a.klaviyo.com/api";
        string endpoint = _configuration["KlaviyoSettings:ProfilesEndpoint"] ?? "/profiles";
        string accept = _configuration["KlaviyoSettings:Accept"] ?? "application/vnd.api+json";
        string revision = _configuration["KlaviyoSettings:Revision"] ?? "2026-04-15";
        string connectionString = _configuration.GetConnectionString("KlaviyoDatabase") 
            ?? _configuration["ConnectionStrings:KlaviyoDatabase"] 
            ?? "Server=TU_SERVER;Database=TU_DB;Trusted_Connection=True;";

        string? nextCursor = null;
        using var client = new RestClient(baseUrl);

        _logger.LogInformation("Iniciando lectura y descarga de clientes de Klaviyo usando KlaviyoCustomerFetcher...");

        int pageCount = 0;
        int totalProcessed = 0;
        int maxPages = _configuration.GetValue<int>("KlaviyoSettings:MaxPagesToFetch", 0);

        do
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("La lectura de clientes de Klaviyo fue cancelada.");
                break;
            }

            pageCount++;
            var request = new RestRequest(endpoint, Method.Get);
            request.AddHeader("Authorization", $"Klaviyo-API-Key {apiKey}");
            request.AddHeader("accept", accept);
            request.AddHeader("revision", revision);

            request.AddQueryParameter("page[size]", "100");
            if (!string.IsNullOrEmpty(nextCursor))
                request.AddQueryParameter("page[cursor]", nextCursor);

            _logger.LogInformation("Consumiendo API de Klaviyo. Endpoint: {Endpoint}, Página: {PageCount}, Clientes procesados hasta ahora: {TotalProcessed}, Cursor actual: {Cursor}", 
                endpoint, pageCount, totalProcessed, nextCursor ?? "Inicio");
            var response = await client.ExecuteAsync(request, cancellationToken);
            if (!response.IsSuccessful)
            {
                var errorMsg = $"Error API: {response.StatusCode} - {response.Content}";
                _logger.LogError(errorMsg);
                throw new Exception(errorMsg);
            }

            if (string.IsNullOrEmpty(response.Content))
            {
                _logger.LogWarning("La respuesta del API de Klaviyo está vacía.");
                break;
            }

            var json = JObject.Parse(response.Content);
            var dataArray = json["data"];
            if (dataArray == null || !dataArray.Any())
            {
                _logger.LogInformation("No se encontraron clientes en la respuesta actual.");
                break;
            }

            // Construir DataTable para Table Type
            var table = new DataTable();
            table.Columns.Add("ProfileId", typeof(string));
            table.Columns.Add("FirstName", typeof(string));
            table.Columns.Add("LastName", typeof(string));
            table.Columns.Add("PhoneNumber", typeof(string));
            table.Columns.Add("Email", typeof(string));
            table.Columns.Add("IdentificationNumber", typeof(string));
            table.Columns.Add("IdentificationType", typeof(string));

            foreach (var item in dataArray)
            {
                var id = item["id"]?.ToString();
                var attr = item["attributes"];

                string? firstName = attr?["first_name"]?.ToString();
                string? lastName = attr?["last_name"]?.ToString();
                string? phone = attr?["phone_number"]?.ToString();
                string? email = attr?["email"]?.ToString();

                // Extraer campos opcionales desde properties (puede ser {} o no contener las claves)
                var properties = attr?["properties"] as JObject;
                string? identificationNumber = properties?.ContainsKey("identification_number") == true
                    ? properties["identification_number"]?.ToString()
                    : null;
                string? identificationType = properties?.ContainsKey("identification_type") == true
                    ? properties["identification_type"]?.ToString()
                    : null;

                table.Rows.Add(id, firstName, lastName, phone, email, identificationNumber, identificationType);
            }

            totalProcessed += table.Rows.Count;
            _logger.LogInformation("Insertando {RowCount} clientes en la base de datos (Total acumulado: {TotalProcessed})...", table.Rows.Count, totalProcessed);

            // Enviar DataTable al SP
            using (var conn = new SqlConnection(connectionString))
            using (var cmd = new SqlCommand("dbo.InsertCustomers", conn))
            {
                cmd.CommandType = CommandType.StoredProcedure;
                var param = cmd.Parameters.AddWithValue("@Customers", table);
                param.SqlDbType = SqlDbType.Structured;
                param.TypeName = "dbo.CustomerType";

                await conn.OpenAsync(cancellationToken);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Obtener cursor siguiente: extraer solo el valor de page[cursor] de la URL
            var nextLink = json["links"]?["next"]?.ToString();
            nextCursor = null;
            if (!string.IsNullOrEmpty(nextLink) && Uri.TryCreate(nextLink, UriKind.Absolute, out var nextUri))
            {
                // Parsear query string manualmente (no se requiere System.Web en .NET moderno)
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
                _logger.LogInformation("Se alcanzó el límite de páginas configurado ({MaxPages}). Deteniendo la descarga.", maxPages);
                break;
            }

        } while (!string.IsNullOrEmpty(nextCursor));

        _logger.LogInformation("Lectura de clientes de Klaviyo finalizada correctamente.");
    }
}
