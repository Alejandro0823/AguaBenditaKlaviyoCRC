using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KlaviyoCRC;

public interface IKlaviyoEmailExclusionSyncService
{
    Task SyncEmailExclusionsAsync(BrandOptions brand, CancellationToken cancellationToken);
}

/// <summary>
/// Proceso independiente: toma los clientes marcados como IsEmailExcludedCRC = 1
/// (Registro de Números Excluidos / RNE de Colombia) que aún no han sido enviados a
/// Klaviyo, y los desuscribe de marketing por email vía
/// profile-subscription-bulk-delete-jobs, en bloques de máximo 100 perfiles.
/// El JSON del body ya viene armado desde el SP (mismo patrón que
/// CrcEmailValidationService/CrcPhoneValidationService). No modifica el flujo
/// existente de lectura/validación CRC.
/// </summary>
public class KlaviyoEmailExclusionSyncService : IKlaviyoEmailExclusionSyncService
{
    // Espaciado entre lotes para respetar el rate limit de Klaviyo (Burst: 75/s, Steady: 750/min).
    private const int DelayBetweenBatchesMs = 150;

    private readonly IConfiguration _configuration;
    private readonly ILogger<KlaviyoEmailExclusionSyncService> _logger;

    public KlaviyoEmailExclusionSyncService(IConfiguration configuration, ILogger<KlaviyoEmailExclusionSyncService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private class KlaviyoDeleteChunk
    {
        public string Payload { get; set; } = string.Empty;
        public List<string> ProfileIds { get; set; } = new();
    }

    public async Task SyncEmailExclusionsAsync(BrandOptions brand, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[{Brand}] Iniciando sincronización de exclusión de email CRC hacia Klaviyo (bulk unsubscribe)...", brand.Code);

        string connectionString = _configuration.GetConnectionString("KlaviyoDatabase")
            ?? _configuration["ConnectionStrings:KlaviyoDatabase"]
            ?? throw new InvalidOperationException("No se encontró la cadena de conexión 'KlaviyoDatabase'.");

        // --- 1. Obtener los lotes (JSON ya armado en el SP) pendientes de sincronizar ---
        var chunks = await GetPendingChunksFromDatabaseAsync(connectionString, brand.GetPendingEmailExclusionSyncKlaviyoSp, cancellationToken);

        if (chunks.Count == 0)
        {
            _logger.LogInformation("[{Brand}] No hay clientes pendientes de sincronizar exclusión de email hacia Klaviyo.", brand.Code);
            return;
        }

        var totalProfiles = chunks.Sum(c => c.ProfileIds.Count);
        _logger.LogInformation("[{Brand}] Se obtuvieron {ChunkCount} lotes con {Total} perfiles pendientes de desuscripción en Klaviyo.", brand.Code, chunks.Count, totalProfiles);

        using var client = new RestClient(brand.BaseUrl);

        int chunkIndex = 0;
        int totalSynced = 0;

        foreach (var chunk in chunks)
        {
            chunkIndex++;
            _logger.LogInformation("[{Brand}] Enviando lote {ChunkIndex}/{TotalChunks} con {Count} perfiles a Klaviyo...", brand.Code, chunkIndex, chunks.Count, chunk.ProfileIds.Count);

            // --- 2. Enviar el JSON del SP directamente como body ---
            var request = new RestRequest(brand.ProfileSubscriptionBulkDeleteJobsEndpoint, Method.Post);
            request.AddHeader("Authorization", $"Klaviyo-API-Key {brand.ApiKey}");
            request.AddHeader("accept", brand.Accept);
            request.AddHeader("content-type", brand.Accept);
            request.AddHeader("revision", brand.Revision);
            request.AddStringBody(chunk.Payload, ContentType.Json);

            var response = await client.ExecuteAsync(request, cancellationToken);

            if (!response.IsSuccessful)
            {
                var errorMsg = $"[{brand.Code}] Error al enviar el lote {chunkIndex} a Klaviyo (bulk unsubscribe). StatusCode: {response.StatusCode} - {response.Content}";
                _logger.LogError(errorMsg);
                throw new Exception(errorMsg);
            }

            string? jobId = null;
            try
            {
                var responseJson = string.IsNullOrEmpty(response.Content) ? null : JObject.Parse(response.Content);
                jobId = responseJson?["data"]?["id"]?.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[{Brand}] No se pudo extraer el job_id de la respuesta de Klaviyo: {Error}", brand.Code, ex.Message);
            }

            _logger.LogInformation(
                "[{Brand}] Lote {ChunkIndex} aceptado por Klaviyo (StatusCode: {StatusCode}, job_id: {JobId}). Perfiles enviados: {Count}.",
                brand.Code, chunkIndex, response.StatusCode, jobId ?? "N/A", chunk.ProfileIds.Count);

            // --- 3. Marcar en base de datos únicamente los registros de este lote ya confirmado ---
            await MarkExclusionsAsSyncedAsync(connectionString, brand, chunk.ProfileIds, cancellationToken);
            totalSynced += chunk.ProfileIds.Count;

            if (chunkIndex < chunks.Count)
                await Task.Delay(DelayBetweenBatchesMs, cancellationToken);
        }

        _logger.LogInformation(
            "[{Brand}] Sincronización de exclusión de email hacia Klaviyo completada. Total de perfiles sincronizados: {Total}.",
            brand.Code, totalSynced);
    }

    /// <summary>
    /// Ejecuta el SP GetPendingEmailExclusionSyncKlaviyo y retorna la lista de lotes a enviar:
    /// el JSON ya armado (KlaviyoBulkDeletePayload) y los ProfileId incluidos en cada uno
    /// (columna ProfileIds), necesarios para el marcado post-envío.
    /// </summary>
    private async Task<List<KlaviyoDeleteChunk>> GetPendingChunksFromDatabaseAsync(
        string connectionString,
        string getPendingEmailExclusionSyncKlaviyoSp,
        CancellationToken cancellationToken)
    {
        var chunks = new List<KlaviyoDeleteChunk>();

        using var conn = new SqlConnection(connectionString);
        using var cmd = new SqlCommand(getPendingEmailExclusionSyncKlaviyoSp, conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        await conn.OpenAsync(cancellationToken);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = reader["KlaviyoBulkDeletePayload"]?.ToString();
            var profileIdsJson = reader["ProfileIds"]?.ToString();

            if (string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(profileIdsJson))
                continue;

            List<string> profileIds;
            try
            {
                profileIds = JArray.Parse(profileIdsJson).ToObject<List<string>>() ?? new List<string>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("No se pudo extraer la lista de ProfileId del resultado del SP: {Error}", ex.Message);
                continue;
            }

            if (profileIds.Count > 0)
            {
                chunks.Add(new KlaviyoDeleteChunk { Payload = payload, ProfileIds = profileIds });
            }
        }

        return chunks;
    }

    /// <summary>
    /// Ejecuta el SP MarkEmailExclusionSyncedKlaviyo únicamente para los ProfileId del lote
    /// que Klaviyo ya confirmó (202), para que la siguiente corrida los excluya.
    /// </summary>
    private async Task MarkExclusionsAsSyncedAsync(
        string connectionString,
        BrandOptions brand,
        List<string> profileIds,
        CancellationToken cancellationToken)
    {
        var table = new DataTable();
        table.Columns.Add("ProfileId", typeof(string));
        foreach (var profileId in profileIds)
            table.Rows.Add(profileId);

        using var conn = new SqlConnection(connectionString);
        using var cmd = new SqlCommand(brand.MarkEmailExclusionSyncedKlaviyoSp, conn)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 120
        };

        var param = cmd.Parameters.AddWithValue("@ProfileIds", table);
        param.SqlDbType = SqlDbType.Structured;
        param.TypeName = brand.EmailExclusionSyncType;

        await conn.OpenAsync(cancellationToken);
        var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation(
            "[{Brand}] SP {Sp} ejecutado. Registros marcados como sincronizados con Klaviyo: {Rows}.",
            brand.Code, brand.MarkEmailExclusionSyncedKlaviyoSp, rowsAffected);
    }
}