using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace KlaviyoCRC;

public interface IKlaviyoSmsExclusionSyncService
{
    Task SyncSmsExclusionsAsync(BrandOptions brand, CancellationToken cancellationToken);
}

/// <summary>
/// Proceso independiente: toma los clientes marcados como IsSmsExcludedCRC = 1
/// (Registro de Números Excluidos / RNE de Colombia) que aún no han sido enviados a
/// Klaviyo, y registra ese estado como la custom property "Consentimiento SMS CRC"
/// (booleana, siempre false para estos clientes) en cada profile.
///
/// A diferencia de KlaviyoEmailExclusionSyncService, aquí NO se usa
/// profile-subscription-bulk-delete-jobs: Klaviyo no soporta el canal nativo de SMS
/// marketing para números de Colombia, así que este proceso solo dimensiona la
/// custom property vía PATCH /api/profiles/{profile_id}/, una llamada por perfil
/// (no existe bulk endpoint para custom properties). El ProfileId se toma
/// directamente de la columna ya existente en Customers_AGB/Customers_ABB, sin
/// lookups adicionales contra Klaviyo. El JSON del body ya viene armado desde el SP
/// (mismo patrón que KlaviyoEmailExclusionSyncService/CrcEmailValidationService).
///
/// Dado el mayor volumen de llamadas individuales (vs. lotes de hasta 100 en el
/// proceso de email), cada request pasa por un retry con backoff exponencial y
/// respeto del header Retry-After ante 429/5xx (mismo algoritmo que ya usa
/// KlaviyoCustomerFetcher para la descarga masiva de perfiles, reimplementado aquí
/// de forma autocontenida para no modificar ese archivo). Como cada perfil es
/// independiente, el fallo de uno no aborta a los demás: se registra el error, se
/// continúa con el resto, y ese perfil queda pendiente para la siguiente corrida.
/// No modifica el flujo existente de lectura/validación CRC ni el de email.
/// </summary>
public class KlaviyoSmsExclusionSyncService : IKlaviyoSmsExclusionSyncService
{
    // Espaciado entre llamadas individuales para repartir el volumen de requests
    // (aquí 1 por perfil, a diferencia de los lotes de hasta 100 del proceso de email).
    private const int DelayBetweenCallsMs = 150;

    private readonly IConfiguration _configuration;
    private readonly ILogger<KlaviyoSmsExclusionSyncService> _logger;

    public KlaviyoSmsExclusionSyncService(IConfiguration configuration, ILogger<KlaviyoSmsExclusionSyncService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private class PendingSmsProfile
    {
        public string ProfileId { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
    }

    public async Task SyncSmsExclusionsAsync(BrandOptions brand, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[{Brand}] Iniciando sincronización de exclusión de SMS CRC hacia Klaviyo (custom property 'Consentimiento SMS CRC', PATCH individual por perfil)...",
            brand.Code);

        string connectionString = _configuration.GetConnectionString("KlaviyoDatabase")
            ?? _configuration["ConnectionStrings:KlaviyoDatabase"]
            ?? throw new InvalidOperationException("No se encontró la cadena de conexión 'KlaviyoDatabase'.");

        // --- 1. Obtener los perfiles (JSON ya armado en el SP) pendientes de sincronizar ---
        var pendingProfiles = await GetPendingProfilesFromDatabaseAsync(connectionString, brand.GetPendingSmsExclusionSyncKlaviyoSp, cancellationToken);

        if (pendingProfiles.Count == 0)
        {
            _logger.LogInformation("[{Brand}] No hay clientes pendientes de sincronizar exclusión de SMS hacia Klaviyo.", brand.Code);
            return;
        }

        _logger.LogInformation("[{Brand}] Se obtuvieron {Count} perfiles pendientes de actualizar en Klaviyo (SMS).", brand.Code, pendingProfiles.Count);

        using var client = new RestClient(brand.BaseUrl);

        int index = 0;
        int totalSynced = 0;
        int totalFailed = 0;

        foreach (var profile in pendingProfiles)
        {
            index++;
            cancellationToken.ThrowIfCancellationRequested();

            // --- 2. Enviar el JSON del SP directamente como body, uno por perfil ---
            var request = new RestRequest($"{brand.ProfilesEndpoint}/{profile.ProfileId}/", Method.Patch);
            request.AddHeader("Authorization", $"Klaviyo-API-Key {brand.ApiKey}");
            request.AddHeader("accept", brand.Accept);
            request.AddHeader("content-type", brand.Accept);
            request.AddHeader("revision", brand.Revision);
            request.AddStringBody(profile.Payload, ContentType.Json);

            try
            {
                var response = await ExecuteWithRetryAsync(client, request, brand, cancellationToken);

                _logger.LogInformation(
                    "[{Brand}] Perfil {Index}/{Total} ({ProfileId}) actualizado en Klaviyo (StatusCode: {StatusCode}).",
                    brand.Code, index, pendingProfiles.Count, profile.ProfileId, response.StatusCode);

                // --- 3. Marcar en base de datos únicamente este perfil, ya confirmado ---
                await MarkProfileAsSyncedAsync(connectionString, brand, profile.ProfileId, cancellationToken);
                totalSynced++;
            }
            catch (Exception ex)
            {
                if (ex.WasCancelledByRequest(cancellationToken))
                {
                    _logger.LogInformation("[{Brand}] Sincronización de exclusión de SMS detenida manualmente.", brand.Code);
                    throw;
                }

                totalFailed++;
                _logger.LogError(ex,
                    "[{Brand}] Error al sincronizar exclusión de SMS del perfil {ProfileId} ({Index}/{Total}). Se continúa con el resto; este perfil se reintentará en la próxima corrida.",
                    brand.Code, profile.ProfileId, index, pendingProfiles.Count);
            }

            if (index < pendingProfiles.Count)
                await Task.Delay(DelayBetweenCallsMs, cancellationToken);
        }

        _logger.LogInformation(
            "[{Brand}] Sincronización de exclusión de SMS hacia Klaviyo completada. Sincronizados: {Synced}, fallidos: {Failed} de {Total}.",
            brand.Code, totalSynced, totalFailed, pendingProfiles.Count);
    }

    /// <summary>
    /// Ejecuta el SP GetPendingSmsExclusionSyncKlaviyo y retorna la lista de perfiles a enviar:
    /// el ProfileId (columna ProfileId, usado también para armar la URL del PATCH) y el JSON
    /// ya armado (columna KlaviyoSmsUpdatePayload) para cada uno.
    /// </summary>
    private async Task<List<PendingSmsProfile>> GetPendingProfilesFromDatabaseAsync(
        string connectionString,
        string getPendingSmsExclusionSyncKlaviyoSp,
        CancellationToken cancellationToken)
    {
        var pendingProfiles = new List<PendingSmsProfile>();

        using var conn = new SqlConnection(connectionString);
        using var cmd = new SqlCommand(getPendingSmsExclusionSyncKlaviyoSp, conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        await conn.OpenAsync(cancellationToken);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var profileId = reader["ProfileId"]?.ToString();
            var payload = reader["KlaviyoSmsUpdatePayload"]?.ToString();

            if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(payload))
                continue;

            pendingProfiles.Add(new PendingSmsProfile { ProfileId = profileId, Payload = payload });
        }

        return pendingProfiles;
    }

    /// <summary>
    /// Ejecuta el SP MarkSmsExclusionSyncedKlaviyo para un único ProfileId, inmediatamente después
    /// de que Klaviyo confirme el PATCH de ese perfil puntual (a diferencia del proceso de email,
    /// que marca un lote completo tras una sola respuesta 202, aquí cada llamada se confirma sola).
    /// </summary>
    private async Task MarkProfileAsSyncedAsync(
        string connectionString,
        BrandOptions brand,
        string profileId,
        CancellationToken cancellationToken)
    {
        using var conn = new SqlConnection(connectionString);
        using var cmd = new SqlCommand(brand.MarkSmsExclusionSyncedKlaviyoSp, conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        cmd.Parameters.AddWithValue("@ProfileId", profileId);

        await conn.OpenAsync(cancellationToken);
        var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation(
            "[{Brand}] SP {Sp} ejecutado para {ProfileId}. Registros marcados como sincronizados con Klaviyo: {Rows}.",
            brand.Code, brand.MarkSmsExclusionSyncedKlaviyoSp, profileId, rowsAffected);
    }

    /// <summary>
    /// Reintenta con backoff exponencial (respetando el header Retry-After si Klaviyo lo envía)
    /// ante 429/5xx, usando brand.MaxRetryAttempts/RetryBaseDelayMs. Mismo algoritmo que
    /// KlaviyoCustomerFetcher.ExecuteWithRetryAsync, reimplementado aquí de forma autocontenida
    /// (no se comparte código con ese archivo para no modificarlo).
    /// </summary>
    private async Task<RestResponse> ExecuteWithRetryAsync(RestClient client, RestRequest request, BrandOptions brand, CancellationToken cancellationToken)
    {
        int maxAttempts = Math.Max(1, brand.MaxRetryAttempts);
        int baseDelayMs = Math.Max(1, brand.RetryBaseDelayMs);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var response = await client.ExecuteAsync(request, cancellationToken);

            // RestSharp no lanza excepción si el request se aborta por cancelación: devuelve una
            // respuesta "fallida" con StatusCode 0, que sin este chequeo se trataría como un error
            // real de la API en vez de un efecto esperado de "Detener procesos".
            cancellationToken.ThrowIfCancellationRequested();

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
