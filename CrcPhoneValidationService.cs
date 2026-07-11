using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

namespace KlaviyoCRC;

public interface ICrcPhoneValidationService
{
    Task ValidatePhonesAsync(BrandOptions brand, CancellationToken cancellationToken);
}

public class CrcPhoneValidationService : ICrcPhoneValidationService
{
    private readonly IConfiguration _configuration;
    private readonly ICrcApiTokenService _crcApiTokenService;
    private readonly ILogger<CrcPhoneValidationService> _logger;

    public CrcPhoneValidationService(IConfiguration configuration, ICrcApiTokenService crcApiTokenService, ILogger<CrcPhoneValidationService> logger)
    {
        _configuration = configuration;
        _crcApiTokenService = crcApiTokenService;
        _logger = logger;
    }

    private class CrcPayloadChunk
    {
        public string Payload { get; set; } = string.Empty;
        public List<string> Keys { get; set; } = new();
    }

    public async Task ValidatePhonesAsync(BrandOptions brand, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[{Brand}] Iniciando validación de teléfonos contra servicio CRC...", brand.Code);

        // Reutiliza la misma sección de configuración que el servicio de emails
        var settings = _configuration.GetSection("CrcApiSettings");

        string connectionString = _configuration.GetConnectionString("KlaviyoDatabase")
            ?? _configuration["ConnectionStrings:KlaviyoDatabase"]
            ?? throw new InvalidOperationException("No se encontró la cadena de conexión 'KlaviyoDatabase'.");

        string baseUrl  = settings["BaseUrl"]  ?? throw new InvalidOperationException("CrcApiSettings:BaseUrl no configurado.");
        string endpoint = settings["Endpoint"] ?? throw new InvalidOperationException("CrcApiSettings:Endpoint no configurado.");

        // --- 1. Obtener la lista de payloads del SP ---
        var chunks = await GetCrcPayloadsFromDatabaseAsync(connectionString, brand.GetCustomerPhonesForCrcSp, cancellationToken);

        if (chunks.Count == 0)
        {
            _logger.LogWarning("[{Brand}] El SP {Sp} no retornó payloads. No se enviará ninguna solicitud al CRC.", brand.Code, brand.GetCustomerPhonesForCrcSp);
            return;
        }

        _logger.LogInformation("[{Brand}] Se obtuvieron {ChunkCount} bloques de teléfonos desde el SP para validar en total.", brand.Code, chunks.Count);

        using var client = new RestClient(baseUrl);

        int chunkIndex = 0;
        foreach (var chunk in chunks)
        {
            chunkIndex++;
            _logger.LogInformation("[{Brand}] Procesando bloque {ChunkIndex}/{TotalChunks} con {Count} teléfonos...", brand.Code, chunkIndex, chunks.Count, chunk.Keys.Count);

            // --- 2. Construir y ejecutar el request HTTP ---
            var request = new RestRequest(endpoint, Method.Post);

            // Agregar headers configurados dinámicamente desde appsettings (compartidos con email)
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

            // Authorization: token vigente gestionado por CrcApiTokenService (se renueva automáticamente
            // antes de que expire, ver ProcessExecutor.RunInternalProcessAsync), no viene de appsettings.
            var crcToken = await _crcApiTokenService.GetCurrentTokenAsync(cancellationToken);
            request.AddHeader("Authorization", $"Bearer {crcToken}");

            // Enviar el JSON del SP directamente como body
            request.AddStringBody(chunk.Payload, ContentType.Json);

            _logger.LogInformation("[{Brand}] Enviando bloque {ChunkIndex} al endpoint CRC: {BaseUrl}{Endpoint}", brand.Code, chunkIndex, baseUrl, endpoint);

            var response = await client.ExecuteAsync(request, cancellationToken);

            if (!response.IsSuccessful)
            {
                _logger.LogError(
                    "[{Brand}] Error al llamar al servicio CRC (teléfonos) para el bloque {ChunkIndex}. StatusCode: {StatusCode} - Respuesta: {Content}",
                    brand.Code,
                    chunkIndex,
                    response.StatusCode,
                    response.Content);
                throw new Exception($"[{brand.Code}] CRC Phone API Error (Bloque {chunkIndex}): {response.StatusCode} - {response.Content}");
            }

            _logger.LogInformation("[{Brand}] Respuesta recibida para el bloque {ChunkIndex} del servicio CRC (teléfonos). Procesando resultados...", brand.Code, chunkIndex);

            // --- 3. Procesar la respuesta CRC y actualizar la base de datos ---
            await ProcessCrcResponseAndUpdateDatabaseAsync(
                brand,
                connectionString,
                response.Content ?? "[]",
                chunk.Keys,
                cancellationToken);
        }

        _logger.LogInformation("[{Brand}] Validación CRC de teléfonos completada exitosamente para todos los bloques.", brand.Code);
    }

    /// <summary>
    /// Ejecuta el SP GetCustomerPhonesForCRC y retorna la lista de bloques JSON a procesar.
    /// </summary>
    private async Task<List<CrcPayloadChunk>> GetCrcPayloadsFromDatabaseAsync(
        string connectionString,
        string getCustomerPhonesForCrcSp,
        CancellationToken cancellationToken)
    {
        var chunks = new List<CrcPayloadChunk>();

        using var conn = new SqlConnection(connectionString);
        using var cmd  = new SqlCommand(getCustomerPhonesForCrcSp, conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        await conn.OpenAsync(cancellationToken);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = reader["CrcPhonePayload"]?.ToString();
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
    /// Procesa el array de respuesta CRC y sincroniza IsSmsExcludedCRC e IsCallExcludedCRC en la
    /// tabla Customers para TODOS los teléfonos enviados (no solo los que deben excluirse), ya que
    /// el cliente puede activar/desactivar estos canales en CRC en cualquier momento:
    ///
    ///   sms = true      en la respuesta → IsSmsExcludedCRC  = 0 (quiere ser contactado)
    ///   sms = false     en la respuesta → IsSmsExcludedCRC  = 1 (no quiere ser contactado)
    ///   llamada = true  en la respuesta → IsCallExcludedCRC = 0 (quiere ser contactado)
    ///   llamada = false en la respuesta → IsCallExcludedCRC = 1 (no quiere ser contactado)
    ///   el teléfono no aparece en la respuesta (o array vacío []) → ambos campos = 0 (sin rastro, se asume disponible)
    /// </summary>
    private async Task ProcessCrcResponseAndUpdateDatabaseAsync(
        BrandOptions brand,
        string connectionString,
        string responseContent,
        List<string> phonesSent,
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
            _logger.LogError("[{Brand}] La respuesta CRC (teléfonos) no es un JSON array válido: {Error}. Contenido: {Content}", brand.Code, ex.Message, responseContent);
            throw;
        }

        // Mapa llave (teléfono) -> (sms, llamada) reportado por CRC
        var crcByPhone = new Dictionary<string, (bool Sms, bool Llamada)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in crcResults)
        {
            var llave = item["llave"]?.ToString();
            if (string.IsNullOrWhiteSpace(llave)) continue;

            var sms     = item["opcionesContacto"]?["sms"]    ?.ToObject<bool>() ?? true;
            var llamada = item["opcionesContacto"]?["llamada"]?.ToObject<bool>() ?? true;
            crcByPhone[llave] = (sms, llamada);
        }

        // Construir DataTable con TODOS los teléfonos enviados y su estado final (TVP: dbo.PhoneExclusionType)
        // Columnas: Phone, IsSmsExcluded, IsCallExcluded
        var table = new DataTable();
        table.Columns.Add("Phone",          typeof(string));
        table.Columns.Add("IsSmsExcluded",  typeof(bool));
        table.Columns.Add("IsCallExcluded", typeof(bool));

        int totalExclusions = 0;

        foreach (var phone in phonesSent)
        {
            // Si CRC no reporta el teléfono, se asume sms = true y llamada = true (disponible)
            var (sms, llamada) = crcByPhone.TryGetValue(phone, out var value) ? value : (true, true);

            bool isSmsExcluded  = !sms;
            bool isCallExcluded = !llamada;
            if (isSmsExcluded || isCallExcluded) totalExclusions++;

            table.Rows.Add(phone, isSmsExcluded, isCallExcluded);
            _logger.LogDebug(
                "Teléfono {Phone}: IsSmsExcluded={Sms}, IsCallExcluded={Call}",
                phone, isSmsExcluded, isCallExcluded);
        }

        _logger.LogInformation(
            "[{Brand}] Procesamiento CRC: {Total} teléfonos en respuesta, {ToUpdate} de {Sent} teléfonos enviados quedarán con alguna exclusión.",
            brand.Code, crcResults.Count, totalExclusions, phonesSent.Count);

        // Ejecutar el SP que sincroniza IsSmsExcludedCRC, IsCallExcludedCRC y CrcLastCheckedAt
        using var conn = new SqlConnection(connectionString);
        using var cmd  = new SqlCommand(brand.UpdatePhoneExclusionCrcSp, conn)
        {
            CommandType    = CommandType.StoredProcedure,
            CommandTimeout = 120
        };

        var param = cmd.Parameters.AddWithValue("@Phones", table);
        param.SqlDbType = SqlDbType.Structured;
        param.TypeName  = brand.PhoneExclusionType;

        await conn.OpenAsync(cancellationToken);
        var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation(
            "[{Brand}] SP {Sp} ejecutado. Registros actualizados en Customers: {Rows}.",
            brand.Code, brand.UpdatePhoneExclusionCrcSp, rowsAffected);
    }
}
