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
    Task ValidateEmailsAsync(CancellationToken cancellationToken);
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

    public async Task ValidateEmailsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Iniciando validación de emails contra servicio CRC...");

        var settings = _configuration.GetSection("CrcApiSettings");

        string connectionString = _configuration.GetConnectionString("KlaviyoDatabase")
            ?? _configuration["ConnectionStrings:KlaviyoDatabase"]
            ?? throw new InvalidOperationException("No se encontró la cadena de conexión 'KlaviyoDatabase'.");

        string baseUrl  = settings["BaseUrl"]  ?? throw new InvalidOperationException("CrcApiSettings:BaseUrl no configurado.");
        string endpoint = settings["Endpoint"] ?? throw new InvalidOperationException("CrcApiSettings:Endpoint no configurado.");

        // --- 1. Obtener la lista de payloads del SP ---
        var chunks = await GetCrcPayloadsFromDatabaseAsync(connectionString, cancellationToken);

        if (chunks.Count == 0)
        {
            _logger.LogWarning("El SP GetCustomerEmailsForCRC no retornó payloads. No se enviará ninguna solicitud al CRC.");
            return;
        }

        _logger.LogInformation("Se obtuvieron {ChunkCount} bloques de emails desde el SP para validar en total.", chunks.Count);

        using var client = new RestClient(baseUrl);

        int chunkIndex = 0;
        foreach (var chunk in chunks)
        {
            chunkIndex++;
            _logger.LogInformation("Procesando bloque {ChunkIndex}/{TotalChunks} con {Count} emails...", chunkIndex, chunks.Count, chunk.Keys.Count);

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

            _logger.LogInformation("Enviando bloque {ChunkIndex} al endpoint CRC: {BaseUrl}{Endpoint}", chunkIndex, baseUrl, endpoint);

            var response = await client.ExecuteAsync(request, cancellationToken);

            if (!response.IsSuccessful)
            {
                _logger.LogError(
                    "Error al llamar al servicio CRC para el bloque {ChunkIndex}. StatusCode: {StatusCode} - Respuesta: {Content}",
                    chunkIndex,
                    response.StatusCode,
                    response.Content);
                throw new Exception($"CRC API Error (Bloque {chunkIndex}): {response.StatusCode} - {response.Content}");
            }

            _logger.LogInformation("Respuesta recibida para el bloque {ChunkIndex} del servicio CRC. Procesando resultados...", chunkIndex);

            // --- 3. Procesar la respuesta CRC y actualizar la base de datos ---
            await ProcessCrcResponseAndUpdateDatabaseAsync(
                connectionString,
                response.Content ?? "[]",
                chunk.Keys,
                cancellationToken);
        }

        _logger.LogInformation("Validación CRC de emails completada exitosamente para todos los bloques.");
    }

    /// <summary>
    /// Ejecuta el SP GetCustomerEmailsForCRC y retorna la lista de bloques JSON a procesar.
    /// </summary>
    private async Task<List<CrcPayloadChunk>> GetCrcPayloadsFromDatabaseAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var chunks = new List<CrcPayloadChunk>();

        using var conn = new SqlConnection(connectionString);
        using var cmd  = new SqlCommand("dbo.GetCustomerEmailsForCRC", conn)
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
    /// Procesa el array de respuesta CRC y actualiza el campo IsEmailExcludedCRC
    /// en la tabla Customers según las siguientes reglas:
    ///
    ///   - correo_electronico = false en la respuesta  → IsEmailExcludedCRC = 1  (excluido) + actualiza CrcLastCheckedAt
    ///   - correo_electronico = true  en la respuesta  → IsEmailExcludedCRC permanece en 0  (sin cambio)
    ///   - no aparece en la respuesta                  → IsEmailExcludedCRC permanece en 0  (sin cambio)
    /// </summary>
    private async Task ProcessCrcResponseAndUpdateDatabaseAsync(
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
            _logger.LogError("La respuesta CRC no es un JSON array válido: {Error}. Contenido: {Content}", ex.Message, responseContent);
            throw;
        }

        if (crcResults.Count == 0)
        {
            _logger.LogInformation(
                "La respuesta CRC está vacía ([]). Ningún email será marcado como excluido. " +
                "IsEmailExcludedCRC permanece en 0 para los {Count} emails enviados.",
                emailsSent.Count);
            return;
        }

        // Identificar emails con correo_electronico = false → deben excluirse (IsEmailExcludedCRC = 1)
        var emailsToExclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in crcResults)
        {
            var llave = item["llave"]?.ToString();
            if (string.IsNullOrWhiteSpace(llave)) continue;

            var correoElectronico = item["opcionesContacto"]?["correo_electronico"]?.ToObject<bool>() ?? true;

            if (!correoElectronico)
            {
                emailsToExclude.Add(llave);
                _logger.LogDebug("Email marcado para exclusión (correo_electronico=false): {Email}", llave);
            }
            else
            {
                _logger.LogDebug("Email con correo_electronico=true, permanece sin cambio: {Email}", llave);
            }
        }

        _logger.LogInformation(
            "Procesamiento CRC: {Total} emails en respuesta, {ToExclude} con correo_electronico=false (serán excluidos).",
            crcResults.Count, emailsToExclude.Count);

        if (emailsToExclude.Count == 0)
        {
            _logger.LogInformation("Ningún email requiere actualización de IsEmailExcludedCRC.");
            return;
        }

        // Construir DataTable con los emails que deben ser excluidos (TVP: dbo.EmailListType)
        var table = new DataTable();
        table.Columns.Add("Email", typeof(string));

        foreach (var email in emailsToExclude)
            table.Rows.Add(email);

        // Ejecutar el SP que actualiza IsEmailExcludedCRC = 1 y CrcLastCheckedAt = GETDATE()
        using var conn = new SqlConnection(connectionString);
        using var cmd  = new SqlCommand("dbo.UpdateEmailExclusionCRC", conn)
        {
            CommandType    = CommandType.StoredProcedure,
            CommandTimeout = 120
        };

        var param = cmd.Parameters.AddWithValue("@ExcludedEmails", table);
        param.SqlDbType = SqlDbType.Structured;
        param.TypeName  = "dbo.EmailExclusionType";

        await conn.OpenAsync(cancellationToken);
        var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation(
            "SP UpdateCrcEmailExclusion ejecutado. Registros actualizados en Customers: {Rows}.",
            rowsAffected);
    }
}
