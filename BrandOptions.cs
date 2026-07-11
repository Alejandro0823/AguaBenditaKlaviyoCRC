namespace KlaviyoCRC;

public class BrandOptions
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    // Klaviyo
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://a.klaviyo.com/api";
    public string ProfilesEndpoint { get; set; } = "/profiles";
    public string Accept { get; set; } = "application/vnd.api+json";
    public string Revision { get; set; } = string.Empty;
    public int MaxPagesToFetch { get; set; } = 0;

    // Optimización de descarga/guardado de perfiles:
    // - PrefetchQueueCapacity: cuántas páginas ya descargadas pueden esperar en cola a ser insertadas
    //   antes de que el fetch se detenga a esperar (backpressure natural, evita acumular memoria sin control).
    // - DbBatchPages: cuántas páginas se acumulan antes de hacer un solo INSERT (menos round-trips a la BD).
    // - MaxRetryAttempts / RetryBaseDelayMs: reintentos con backoff exponencial ante 429/5xx de Klaviyo.
    public int PrefetchQueueCapacity { get; set; } = 3;
    public int DbBatchPages { get; set; } = 5;
    public int MaxRetryAttempts { get; set; } = 5;
    public int RetryBaseDelayMs { get; set; } = 1000;

    // Número de streams concurrentes al descargar perfiles, particionados por rango de fecha
    // de creación (cada uno con su propio cursor). Validado contra el rate-limit real de Klaviyo
    // (steady 750 req/min): con 1 stream se usa ~11% de ese límite, así que 5 streams concurrentes
    // dejan margen de sobra. 1 = comportamiento secuencial original, sin particionar.
    public int FetchConcurrency { get; set; } = 5;

    // Base de datos: tabla y stored procedures propios de la marca
    public string CustomerType { get; set; } = string.Empty;
    public string InsertCustomersSp { get; set; } = string.Empty;
    public string GetCustomerEmailsForCrcSp { get; set; } = string.Empty;
    public string UpdateEmailExclusionCrcSp { get; set; } = string.Empty;
    public string EmailExclusionType { get; set; } = string.Empty;
    public string GetCustomerPhonesForCrcSp { get; set; } = string.Empty;
    public string UpdatePhoneExclusionCrcSp { get; set; } = string.Empty;
    public string PhoneExclusionType { get; set; } = string.Empty;

    // Klaviyo: sincronización de exclusión de email (CRC/RNE) vía bulk unsubscribe
    public string ProfileSubscriptionBulkDeleteJobsEndpoint { get; set; } = "/profile-subscription-bulk-delete-jobs";

    // Base de datos: stored procedures y Table Type propios de la marca para este proceso
    public string GetPendingEmailExclusionSyncKlaviyoSp { get; set; } = string.Empty;
    public string MarkEmailExclusionSyncedKlaviyoSp { get; set; } = string.Empty;
    public string EmailExclusionSyncType { get; set; } = string.Empty;
}