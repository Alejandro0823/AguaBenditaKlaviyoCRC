using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

namespace KlaviyoCRC;

public interface ICrcApiTokenService
{
    /// <summary>
    /// Revisa el token vigente (sembrándolo desde CrcApiSettings:BootstrapToken si aún no existe
    /// en base de datos) y lo renueva contra el CRC si falta poco para expirar. Pensado para
    /// llamarse una sola vez al inicio de cada corrida del scheduler.
    /// </summary>
    Task EnsureFreshTokenAsync(CancellationToken cancellationToken);

    /// <summary>Retorna el token vigente guardado en base de datos (sin el prefijo "Bearer ").</summary>
    Task<string> GetCurrentTokenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// El token de la API de CRC (RNE) es un JWT que expira cada ~6 meses. Este servicio lo
/// mantiene vigente: lo guarda en dbo.CrcApiToken y, cuando falta poco para su expiración
/// (decodificada del claim "exp" del propio JWT), llama a CrcApiSettings:TokenRenewalEndpoint
/// usando el token actual como Bearer para obtener uno nuevo, tal como lo requiere CRC.
/// </summary>
public class CrcApiTokenService : ICrcApiTokenService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CrcApiTokenService> _logger;

    public CrcApiTokenService(IConfiguration configuration, ILogger<CrcApiTokenService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task EnsureFreshTokenAsync(CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        var settings = _configuration.GetSection("CrcApiSettings");

        var (token, expiresAtUtc) = await GetStoredTokenAsync(connectionString, cancellationToken);

        if (token == null)
        {
            var bootstrapToken = settings["BootstrapToken"]
                ?? throw new InvalidOperationException("No hay token de CRC guardado en base de datos y CrcApiSettings:BootstrapToken no está configurado para sembrarlo.");

            token = bootstrapToken;
            expiresAtUtc = DecodeJwtExpirationUtc(token);
            await SaveTokenAsync(connectionString, token, expiresAtUtc, cancellationToken);

            _logger.LogInformation("Token de CRC sembrado en base de datos desde configuración. Expira: {ExpiresAtUtc} UTC.", expiresAtUtc);
        }

        var thresholdDays = settings.GetValue<int?>("TokenRenewalThresholdDays") ?? 15;
        var remaining = expiresAtUtc - DateTime.UtcNow;

        if (remaining > TimeSpan.FromDays(thresholdDays))
        {
            _logger.LogInformation("Token de CRC vigente. Expira: {ExpiresAtUtc} UTC (en {Days} días).", expiresAtUtc, (int)remaining.TotalDays);
            return;
        }

        _logger.LogInformation("Token de CRC expira en {Days} días (umbral: {Threshold}). Renovando...", (int)remaining.TotalDays, thresholdDays);

        var newToken = await RenewTokenAsync(settings, token, cancellationToken);
        var newExpiresAtUtc = DecodeJwtExpirationUtc(newToken);

        await SaveTokenAsync(connectionString, newToken, newExpiresAtUtc, cancellationToken);

        _logger.LogInformation("Token de CRC renovado exitosamente. Nueva expiración: {ExpiresAtUtc} UTC.", newExpiresAtUtc);
    }

    public async Task<string> GetCurrentTokenAsync(CancellationToken cancellationToken)
    {
        var (token, _) = await GetStoredTokenAsync(GetConnectionString(), cancellationToken);
        return token ?? throw new InvalidOperationException("No hay token de CRC disponible en base de datos. Ejecuta EnsureFreshTokenAsync primero.");
    }

    private async Task<string> RenewTokenAsync(IConfigurationSection settings, string currentToken, CancellationToken cancellationToken)
    {
        var baseUrl = settings["BaseUrl"] ?? throw new InvalidOperationException("CrcApiSettings:BaseUrl no configurado.");
        var renewalEndpoint = settings["TokenRenewalEndpoint"] ?? throw new InvalidOperationException("CrcApiSettings:TokenRenewalEndpoint no configurado.");

        using var client = new RestClient(baseUrl);
        var request = new RestRequest(renewalEndpoint, Method.Get);
        request.AddHeader("Authorization", $"Bearer {currentToken}");

        var response = await client.ExecuteAsync(request, cancellationToken);

        if (!response.IsSuccessful || string.IsNullOrWhiteSpace(response.Content))
        {
            throw new Exception($"Error al renovar el token de CRC. StatusCode: {response.StatusCode} - {response.Content}");
        }

        var json = JObject.Parse(response.Content);
        var newToken = json["data"]?.ToString();

        if (string.IsNullOrWhiteSpace(newToken))
        {
            throw new Exception($"La respuesta de renovación de token de CRC no trae 'data'. Contenido: {response.Content}");
        }

        return newToken;
    }

    private static DateTime DecodeJwtExpirationUtc(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
            throw new FormatException("El token de CRC no tiene el formato JWT esperado (header.payload.signature).");

        var payloadJson = DecodeBase64Url(parts[1]);
        var payload = JObject.Parse(payloadJson);
        var exp = payload["exp"]?.ToObject<long?>()
            ?? throw new FormatException("El JWT del token de CRC no contiene el claim 'exp'.");

        return DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
    }

    private static string DecodeBase64Url(string input)
    {
        var value = input.Replace('-', '+').Replace('_', '/');
        switch (value.Length % 4)
        {
            case 2: value += "=="; break;
            case 3: value += "="; break;
        }

        var bytes = Convert.FromBase64String(value);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private async Task<(string? Token, DateTime ExpiresAtUtc)> GetStoredTokenAsync(string connectionString, CancellationToken cancellationToken)
    {
        using var conn = new SqlConnection(connectionString);
        using var cmd = new SqlCommand("dbo.GetCrcApiToken", conn) { CommandType = CommandType.StoredProcedure };

        await conn.OpenAsync(cancellationToken);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            var token = reader.GetString(reader.GetOrdinal("Token"));
            var expiresAtUtc = reader.GetDateTime(reader.GetOrdinal("ExpiresAtUtc"));
            return (token, expiresAtUtc);
        }

        return (null, default);
    }

    private async Task SaveTokenAsync(string connectionString, string token, DateTime expiresAtUtc, CancellationToken cancellationToken)
    {
        using var conn = new SqlConnection(connectionString);
        using var cmd = new SqlCommand("dbo.UpsertCrcApiToken", conn) { CommandType = CommandType.StoredProcedure };

        cmd.Parameters.Add("@Token", SqlDbType.NVarChar, -1).Value = token;
        cmd.Parameters.Add("@ExpiresAtUtc", SqlDbType.DateTime2).Value = expiresAtUtc;

        await conn.OpenAsync(cancellationToken);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private string GetConnectionString()
    {
        return _configuration.GetConnectionString("KlaviyoDatabase")
            ?? _configuration["ConnectionStrings:KlaviyoDatabase"]
            ?? throw new InvalidOperationException("No se encontró la cadena de conexión 'KlaviyoDatabase'.");
    }
}
