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
    Task ValidatePhonesAsync(CancellationToken cancellationToken);
}

public class CrcPhoneValidationService : ICrcPhoneValidationService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CrcPhoneValidationService> _logger;

    public CrcPhoneValidationService(IConfiguration configuration, ILogger<CrcPhoneValidationService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task ValidatePhonesAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Iniciando validación de teléfonos contra servicio CRC...");

        // Reutiliza la misma sección de configuración que el servicio de emails
        var settings = _configuration.GetSection("CrcApiSettings");

        string connectionString = _configuration.GetConnectionString("KlaviyoDatabase")
            ?? _configuration["ConnectionStrings:KlaviyoDatabase"]
            ?? throw new InvalidOperationException("No se encontró la cadena de conexión 'KlaviyoDatabase'.");

        string baseUrl  = settings["BaseUrl"]  ?? throw new InvalidOperationException("CrcApiSettings:BaseUrl no configurado.");
        string endpoint = settings["Endpoint"] ?? throw new InvalidOperationException("CrcApiSettings:Endpoint no configurado.");

        // --- 1. Obtener el payload JSON directamente desde el SP ---
        var (crcPhonePayload, phonesSent) = await GetCrcPayloadFromDatabaseAsync(connectionString, cancellationToken);

        if (string.IsNullOrWhiteSpace(crcPhonePayload) || phonesSent.Count == 0)
        {
            _logger.LogWarning("El SP GetCustomerPhonesForCRC no retornó payload. No se enviará la solicitud al CRC.");
            return;
        }

        _logger.LogInformation("Payload obtenido del SP con {Count} teléfonos para validar.", phonesSent.Count);

        // --- 2. Construir y ejecutar el request HTTP ---
        using var client = new RestClient(baseUrl);
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

        // Enviar el JSON del SP directamente como body (sin re-envolver)
        request.AddStringBody(crcPhonePayload, ContentType.Json);

        _logger.LogInformation("Enviando payload al endpoint CRC: {BaseUrl}{Endpoint}", baseUrl, endpoint);

        var response = await client.ExecuteAsync(request, cancellationToken);

        if (!response.IsSuccessful)
        {
            _logger.LogError(
                "Error al llamar al servicio CRC (teléfonos). StatusCode: {StatusCode} - Respuesta: {Content}",
                response.StatusCode,
                response.Content);
            throw new Exception($"CRC Phone API Error: {response.StatusCode} - {response.Content}");
        }

        _logger.LogInformation("Respuesta recibida del servicio CRC (teléfonos). Procesando resultados...");

        // --- 3. Procesar la respuesta CRC y actualizar la base de datos ---
        await ProcessCrcResponseAndUpdateDatabaseAsync(
            connectionString,
            response.Content ?? "[]",
            phonesSent,
            cancellationToken);

        _logger.LogInformation("Validación CRC de teléfonos completada exitosamente.");
    }

    /// <summary>
    /// Ejecuta el SP GetCustomerPhonesForCRC y retorna:
    ///   - El payload JSON completo tal como lo entrega la columna CrcPhonePayload.
    ///   - La lista de teléfonos (keys) incluidos en dicho payload.
    /// Los teléfonos en la DB ya están limpios (sin indicativo de país),
    /// por lo que se usan directamente como llave.
    /// </summary>
    private async Task<(string? Payload, List<string> PhonesSent)> GetCrcPayloadFromDatabaseAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using var conn = new SqlConnection(connectionString);
        using var cmd  = new SqlCommand("dbo.GetCustomerPhonesForCRC", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        await conn.OpenAsync(cancellationToken);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            var payload = reader["CrcPhonePayload"]?.ToString();

            // Extraer la lista de teléfonos del campo "keys" del payload
            var phonesSent = new List<string>();
            if (!string.IsNullOrWhiteSpace(payload))
            {
                try
                {
                    var payloadObj = JObject.Parse(payload);
                    var keys = payloadObj["keys"]?.ToObject<string[]>();
                    if (keys != null)
                        phonesSent.AddRange(keys);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("No se pudo extraer la lista de keys del payload: {Error}", ex.Message);
                }
            }

            return (payload, phonesSent);
        }

        return (null, new List<string>());
    }

    /// <summary>
    /// Procesa el array de respuesta CRC y actualiza los campos IsSmsExcludedCRC e IsCallExcludedCRC
    /// en la tabla Customers según las siguientes reglas:
    ///
    ///   sms = false en la respuesta    → IsSmsExcludedCRC  = 1 (excluido)
    ///   sms = true  en la respuesta    → IsSmsExcludedCRC  permanece en 0 (sin cambio)
    ///   llamada = false en la respuesta → IsCallExcludedCRC = 1 (excluido)
    ///   llamada = true  en la respuesta → IsCallExcludedCRC permanece en 0 (sin cambio)
    ///   no aparece en la respuesta o array vacío [] → ambos campos permanecen en 0
    ///   Siempre que haya actualización → CrcLastCheckedAt = GETDATE()
    /// </summary>
    private async Task ProcessCrcResponseAndUpdateDatabaseAsync(
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
            _logger.LogError("La respuesta CRC (teléfonos) no es un JSON array válido: {Error}. Contenido: {Content}", ex.Message, responseContent);
            throw;
        }

        if (crcResults.Count == 0)
        {
            _logger.LogInformation(
                "La respuesta CRC está vacía ([]). Ningún teléfono será marcado como excluido. " +
                "IsSmsExcludedCRC e IsCallExcludedCRC permanecen en 0 para los {Count} teléfonos enviados.",
                phonesSent.Count);
            return;
        }

        // Construir DataTable con los registros a actualizar (TVP: dbo.PhoneExclusionType)
        // Columnas: Phone, IsSmsExcluded, IsCallExcluded
        var table = new DataTable();
        table.Columns.Add("Phone",          typeof(string));
        table.Columns.Add("IsSmsExcluded",  typeof(bool));
        table.Columns.Add("IsCallExcluded", typeof(bool));

        int totalExclusions = 0;

        foreach (var item in crcResults)
        {
            var llave = item["llave"]?.ToString();
            if (string.IsNullOrWhiteSpace(llave)) continue;

            // sms = false → excluir SMS (IsSmsExcludedCRC = 1)
            var sms     = item["opcionesContacto"]?["sms"]    ?.ToObject<bool>() ?? true;
            // llamada = false → excluir llamada (IsCallExcludedCRC = 1)
            var llamada = item["opcionesContacto"]?["llamada"]?.ToObject<bool>() ?? true;

            bool isSmsExcluded  = !sms;
            bool isCallExcluded = !llamada;

            // Solo se actualiza en DB si al menos uno de los dos campos debe ser excluido
            if (isSmsExcluded || isCallExcluded)
            {
                table.Rows.Add(llave, isSmsExcluded, isCallExcluded);
                totalExclusions++;

                _logger.LogDebug(
                    "Teléfono {Phone}: IsSmsExcluded={Sms}, IsCallExcluded={Call}",
                    llave, isSmsExcluded, isCallExcluded);
            }
            else
            {
                _logger.LogDebug("Teléfono {Phone}: sms=true y llamada=true, permanece sin cambio.", llave);
            }
        }

        _logger.LogInformation(
            "Procesamiento CRC: {Total} teléfonos en respuesta, {ToUpdate} requieren actualización de exclusión.",
            crcResults.Count, totalExclusions);

        if (table.Rows.Count == 0)
        {
            _logger.LogInformation("Ningún teléfono requiere actualización de exclusión CRC.");
            return;
        }

        // Ejecutar el SP que actualiza IsSmsExcludedCRC, IsCallExcludedCRC y CrcLastCheckedAt
        using var conn = new SqlConnection(connectionString);
        using var cmd  = new SqlCommand("dbo.UpdatePhoneExclusionCRC", conn)
        {
            CommandType    = CommandType.StoredProcedure,
            CommandTimeout = 120
        };

        var param = cmd.Parameters.AddWithValue("@ExcludedPhones", table);
        param.SqlDbType = SqlDbType.Structured;
        param.TypeName  = "dbo.PhoneExclusionType";

        await conn.OpenAsync(cancellationToken);
        var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation(
            "SP UpdatePhoneExclusionCRC ejecutado. Registros actualizados en Customers: {Rows}.",
            rowsAffected);
    }
}
