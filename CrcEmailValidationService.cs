using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using RestSharp;
using System.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

namespace KlaviyoCRC;

public interface ICrcEmailValidationService
{
    Task ValidateEmailsAsync(BrandOptions brand, CancellationToken cancellationToken);
}

public class CrcEmailValidationService : ICrcEmailValidationService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CrcEmailValidationService> _logger;

    public CrcEmailValidationService(IConfiguration configuration, ILogger<CrcEmailValidationService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private class CrcPayloadChunk
    {
        public string Payload { get; set; } = string.Empty;
        public List<string> Keys { get; set; } = new();
    }

    public async Task ValidateEmailsAsync(BrandOptions brand, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[{Brand}] Iniciando validación de emails contra servicio CRC...", brand.Code);

        var settings = _configuration.GetSection("CrcApiSettings");

        string connectionString = _configuration.GetConnectionString("KlaviyoDatabase")
            ?? _configuration["ConnectionStrings:KlaviyoDatabase"]
            ?? throw new InvalidOperationException("No se encontró la cadena de conexión 'KlaviyoDatabase'.");

        string baseUrl  = settings["BaseUrl"]  ?? throw new InvalidOperationException("CrcApiSettings:BaseUrl no configurado.");
        string endpoint = settings["Endpoint"] ?? throw new InvalidOperationException("CrcApiSettings:Endpoint no configurado.");

        // --- 1. Obtener la lista de payloads del SP ---
        var chunks = await GetCrcPayloadsFromDatabaseAsync(connectionString, brand.GetCustomerEmailsForCrcSp, cancellationToken);

        if (chunks.Count == 0)
        {
            _logger.LogWarning("[{Brand}] El SP {Sp} no retornó payloads. No se enviará ninguna solicitud al CRC.", brand.Code, brand.GetCustomerEmailsForCrcSp);
            return;
        }

        _logger.LogInformation("[{Brand}] Se obtuvieron {ChunkCount} bloques de emails desde el SP para validar en total.", brand.Code, chunks.Count);

        using var client = new RestClient(baseUrl);

        int chunkIndex = 0;
        foreach (var chunk in chunks)
        {
            chunkIndex++;
            _logger.LogInformation("[{Brand}] Procesando bloque {ChunkIndex}/{TotalChunks} con {Count} emails...", brand.Code, chunkIndex, chunks.Count, chunk.Keys.Count);

            // --- 2. Construir y ejecutar el request HTTP ---
            var request = new RestRequest(endpoint, Method.Post);

            // Agregar headers configurados dinámicamente desde appsettings
            var headersSection = settings.GetSection("Headers");
            foreach (var header in headersSection.GetChildren())
            {
                var headerName  = header["Key"];
                var headerValue = header["Value"];
                if (!string.IsNullOrWhiteSpace(headerName) && headerValue != null)
                {
                    request.AddHeader(headerName, headerValue);
                    _logger.LogDebug("Header agregado: {Key}", headerName);
                }
            }

            // Enviar el JSON del SP directamente como body
            request.AddStringBody(chunk.Payload, ContentType.Json);

            _logger.LogInformation("[{Brand}] Enviando bloque {ChunkIndex} al endpoint CRC: {BaseUrl}{Endpoint}", brand.Code, chunkIndex, baseUrl, endpoint);

            var response = await client.ExecuteAsync(request, cancellationToken);

            if (!response.IsSuccessful)
            {
                _logger.LogError(
                    "[{Brand}] Error al llamar al servicio CRC para el bloque {ChunkIndex}. StatusCode: {StatusCode} - Respuesta: {Content}",
                    brand.Code,
                    chunkIndex,
                    response.StatusCode,
                    response.Content);
                throw new Exception($"[{brand.Code}] CRC API Error (Bloque {chunkIndex}): {response.StatusCode} - {response.Content}");
            }

            _logger.LogInformation("[{Brand}] Respuesta recibida para el bloque {ChunkIndex} del servicio CRC. Procesando resultados...", brand.Code, chunkIndex);

            // --- 3. Procesar la respuesta CRC y actualizar la base de datos ---
            await ProcessCrcResponseAndUpdateDatabaseAsync(
                brand,
                connectionString,
                response.Content ?? "[]",
                chunk.Keys,
                cancellationToken);
        }

        _logger.LogInformation("[{Brand}] Validación CRC de emails completada exitosamente para todos los bloques.", brand.Code);
    }

    /// <summary>
    /// Ejecuta el SP GetCustomerEmailsForCRC y retorna la lista de bloques JSON a procesar.
    /// </summary>
    private async Task<List<CrcPayloadChunk>> GetCrcPayloadsFromDatabaseAsync(
        string connectionString,
        string getCustomerEmailsForCrcSp,
        CancellationToken cancellationToken)
    {
        var chunks = new List<CrcPayloadChunk>();

        using var conn = new SqlConnection(connectionString);
        using var cmd  = new SqlCommand(getCustomerEmailsForCrcSp, conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        await conn.OpenAsync(cancellationToken);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = reader["CrcEmailPayload"]?.ToString();
            if (string.IsNullOrWhiteSpace(payload))
                continue;

            var keys = new List<string>();
            try
            {
                var payloadObj = JObject.Parse(payload);
                var keysArray = payloadObj["keys"]?.ToObject<string[]>();
                if (keysArray != null)
                    keys.AddRange(keysArray);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("No se pudo extraer la lista de keys del payload: {Error}", ex.Message);
            }

            if (keys.Count > 0)
            {
                chunks.Add(new CrcPayloadChunk
                {
                    Payload = payload,
                    Keys = keys
                });
            }
        }

        return chunks;
    }

    /// <summary>
    /// Procesa el array de respuesta CRC y sincroniza IsEmailExcludedCRC en la tabla Customers
    /// para TODOS los emails enviados (no solo los que deben excluirse), ya que el cliente puede
    /// activar/desactivar el canal en CRC en cualquier momento:
    ///
    ///   - correo_electronico = true  en la respuesta → IsEmailExcludedCRC = 0 (quiere ser contactado)
    ///   - correo_electronico = false en la respuesta → IsEmailExcludedCRC = 1 (no quiere ser contactado)
    ///   - el email no aparece en la respuesta         → IsEmailExcludedCRC = 0 (sin rastro, se asume disponible)
    /// </summary>
    private async Task ProcessCrcResponseAndUpdateDatabaseAsync(
        BrandOptions brand,
        string connectionString,
        string responseContent,
        List<string> emailsSent,
        CancellationToken cancellationToken)
    {
        // Parsear respuesta: puede ser [] o [{...}, ...]
        JArray crcResults;
        try
        {
            crcResults = JArray.Parse(responseContent);
        }
        catch (Exception ex)
        {
            _logger.LogError("[{Brand}] La respuesta CRC no es un JSON array válido: {Error}. Contenido: {Content}", brand.Code, ex.Message, responseContent);
            throw;
        }

        // Mapa llave (email) -> correo_electronico reportado por CRC
        var crcByEmail = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in crcResults)
        {
            var llave = item["llave"]?.ToString();
            if (string.IsNullOrWhiteSpace(llave)) continue;

            var correoElectronico = item["opcionesContacto"]?["correo_electronico"]?.ToObject<bool>() ?? true;
            crcByEmail[llave] = correoElectronico;
        }

        // Construir DataTable con TODOS los emails enviados y su estado final (TVP: EmailExclusionType de la marca)
        var table = new DataTable();
        table.Columns.Add("Email", typeof(string));
        table.Columns.Add("IsExcluded", typeof(bool));

        int excludedCount = 0;
        foreach (var email in emailsSent)
        {
            // Si CRC no reporta el email, se asume correo_electronico = true (disponible)
            bool wantsContact = crcByEmail.TryGetValue(email, out var value) ? value : true;
            bool isExcluded = !wantsContact;
            if (isExcluded) excludedCount++;

            table.Rows.Add(email, isExcluded);
            _logger.LogDebug("Email {Email}: IsEmailExcludedCRC={Excluded}", email, isExcluded);
        }

        _logger.LogInformation(
            "[{Brand}] Procesamiento CRC: {Total} emails en respuesta, {ToExclude} de {Sent} emails enviados quedarán excluidos.",
            brand.Code, crcResults.Count, excludedCount, emailsSent.Count);

        // Ejecutar el SP que sincroniza IsEmailExcludedCRC y CrcLastCheckedAt para todos los emails enviados
        using var conn = new SqlConnection(connectionString);
        using var cmd  = new SqlCommand(brand.UpdateEmailExclusionCrcSp, conn)
        {
            CommandType    = CommandType.StoredProcedure,
            CommandTimeout = 120
        };

        var param = cmd.Parameters.AddWithValue("@Emails", table);
        param.SqlDbType = SqlDbType.Structured;
        param.TypeName  = brand.EmailExclusionType;

        await conn.OpenAsync(cancellationToken);
        var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation(
            "[{Brand}] SP {Sp} ejecutado. Registros actualizados en Customers: {Rows}.",
            brand.Code, brand.UpdateEmailExclusionCrcSp,
            rowsAffected);
    }
}
