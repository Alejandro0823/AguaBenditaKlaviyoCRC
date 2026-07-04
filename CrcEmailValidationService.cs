using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.Data;
using System.Data.SqlClient;
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

    public async Task ValidateEmailsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Iniciando validación de emails contra servicio CRC...");

        var settings = _configuration.GetSection("CrcEmailValidationSettings");

        string connectionString = _configuration.GetConnectionString("KlaviyoDatabase")
            ?? _configuration["ConnectionStrings:KlaviyoDatabase"]
            ?? throw new InvalidOperationException("No se encontró la cadena de conexión 'KlaviyoDatabase'.");

        string baseUrl  = settings["BaseUrl"]  ?? throw new InvalidOperationException("CrcEmailValidationSettings:BaseUrl no configurado.");
        string endpoint = settings["Endpoint"] ?? throw new InvalidOperationException("CrcEmailValidationSettings:Endpoint no configurado.");

        // --- 1. Obtener el payload JSON directamente desde el SP ---
        var crcEmailPayload = await GetCrcPayloadFromDatabaseAsync(connectionString, cancellationToken);

        if (string.IsNullOrWhiteSpace(crcEmailPayload))
        {
            _logger.LogWarning("El SP GetCustomerEmailsForCRC no retornó payload. No se enviará la solicitud al CRC.");
            return;
        }

        // Parsear para loguear cuántos emails contiene el payload
        try
        {
            var payloadObj = JObject.Parse(crcEmailPayload);
            var keyCount   = payloadObj["keys"]?.ToObject<string[]>()?.Length ?? 0;
            _logger.LogInformation("Payload obtenido del SP con {Count} emails para validar.", keyCount);
        }
        catch
        {
            _logger.LogWarning("No se pudo parsear el payload para conteo. Se enviará tal como viene del SP.");
        }

        // --- 2. Construir y ejecutar el request HTTP ---
        using var client = new RestClient(baseUrl);
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

        // Enviar el JSON del SP directamente como body (sin re-envolver)
        request.AddStringBody(crcEmailPayload, ContentType.Json);

        _logger.LogInformation("Enviando payload al endpoint CRC: {BaseUrl}{Endpoint}", baseUrl, endpoint);

        var response = await client.ExecuteAsync(request, cancellationToken);

        if (!response.IsSuccessful)
        {
            _logger.LogError(
                "Error al llamar al servicio CRC. StatusCode: {StatusCode} - Respuesta: {Content}",
                response.StatusCode,
                response.Content);
            throw new Exception($"CRC API Error: {response.StatusCode} - {response.Content}");
        }

        _logger.LogInformation("Validación CRC completada exitosamente. Respuesta: {Content}", response.Content);
    }

    /// <summary>
    /// Ejecuta el SP GetCustomerEmailsForCRC y retorna el payload JSON completo
    /// tal como lo entrega la columna CrcEmailPayload (ej: {"type":"COR","keys":[...]}).
    /// </summary>
    private async Task<string?> GetCrcPayloadFromDatabaseAsync(string connectionString, CancellationToken cancellationToken)
    {
        using var conn = new SqlConnection(connectionString);
        using var cmd  = new SqlCommand("dbo.GetCustomerEmailsForCRC", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        await conn.OpenAsync(cancellationToken);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        // El SP retorna una sola fila con el payload JSON completo en la columna CrcEmailPayload
        if (await reader.ReadAsync(cancellationToken))
        {
            return reader["CrcEmailPayload"]?.ToString();
        }

        return null;
    }
}
