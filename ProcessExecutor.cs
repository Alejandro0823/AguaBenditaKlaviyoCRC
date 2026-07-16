using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KlaviyoCRC;

public interface IProcessExecutor
{
    Task ExecuteAsync(CancellationToken cancellationToken);
}

public class ProcessExecutor : IProcessExecutor
{
    private readonly IConfiguration _configuration;
    private readonly IKlaviyoCustomerFetcher _klaviyoCustomerFetcher;
    private readonly ICrcApiTokenService _crcApiTokenService;
    private readonly ICrcEmailValidationService _crcEmailValidationService;
    private readonly ICrcPhoneValidationService _crcPhoneValidationService;
    private readonly IKlaviyoEmailExclusionSyncService _klaviyoEmailExclusionSyncService;
    private readonly IKlaviyoSmsExclusionSyncService _klaviyoSmsExclusionSyncService;
    private readonly ILogger<ProcessExecutor> _logger;

    public ProcessExecutor(
        IConfiguration configuration,
        IKlaviyoCustomerFetcher klaviyoCustomerFetcher,
        ICrcApiTokenService crcApiTokenService,
        ICrcEmailValidationService crcEmailValidationService,
        ICrcPhoneValidationService crcPhoneValidationService,
        IKlaviyoEmailExclusionSyncService klaviyoEmailExclusionSyncService,
        IKlaviyoSmsExclusionSyncService klaviyoSmsExclusionSyncService,
        ILogger<ProcessExecutor> logger)
    {
        _configuration = configuration;
        _klaviyoCustomerFetcher = klaviyoCustomerFetcher;
        _crcApiTokenService = crcApiTokenService;
        _crcEmailValidationService = crcEmailValidationService;
        _crcPhoneValidationService = crcPhoneValidationService;
        _klaviyoEmailExclusionSyncService = klaviyoEmailExclusionSyncService;
        _klaviyoSmsExclusionSyncService = klaviyoSmsExclusionSyncService;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var mode = _configuration["ProcessSettings:Mode"] ?? "Internal";
        _logger.LogInformation("Starting execution in mode: {Mode}", mode);

        try
        {
            if (string.Equals(mode, "External", StringComparison.OrdinalIgnoreCase))
            {
                await RunExternalProcessAsync(cancellationToken);
            }
            else
            {
                await RunInternalProcessAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            if (ex.WasCancelledByRequest(cancellationToken))
                _logger.LogInformation("Ejecución detenida manualmente desde la interfaz.");
            else
                _logger.LogError(ex, "Error occurred during process execution");
        }
    }

    private async Task RunInternalProcessAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Executing internal custom process logic");

        var brands = _configuration.GetSection("Brands").Get<List<BrandOptions>>() ?? new List<BrandOptions>();
        if (brands.Count == 0)
        {
            _logger.LogWarning("No hay marcas configuradas en la sección 'Brands'. No se ejecutará ningún proceso.");
            return;
        }

        // Renovación del token de CRC (una sola vez por corrida, compartido por todas las marcas).
        // Si falla, se registra el error y se continúa con el token vigente: no debe tumbar la corrida.
        try
        {
            await _crcApiTokenService.EnsureFreshTokenAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            if (ex.WasCancelledByRequest(cancellationToken))
                _logger.LogInformation("Verificación/renovación del token de la API CRC detenida manualmente.");
            else
                _logger.LogError(ex, "Error al verificar/renovar el token de la API CRC. Se continuará con el token vigente.");
        }

        // Cada marca corre su propio pipeline (Klaviyo -> Email CRC -> Phone CRC ->
        // Klaviyo Email Exclusion Sync) de forma independiente y en simultáneo;
        // una marca fallando no detiene a las demás.
        await Task.WhenAll(brands.Select(brand => RunBrandPipelineAsync(brand, cancellationToken)));

        _logger.LogInformation("Internal process: Completed task logic for all brands at {Time}", DateTime.Now);
    }

    private async Task RunBrandPipelineAsync(BrandOptions brand, CancellationToken cancellationToken)
    {
        // El scope "Brand" se propaga (AsyncLocal) a todos los servicios llamados dentro
        // de este pipeline, permitiendo que BrandConsoleFormatter coloree sus logs también.
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["Brand"] = brand.Code });

        try
        {
            _logger.LogInformation("[{Brand}] Iniciando pipeline (Klaviyo -> Email CRC -> Phone CRC -> Klaviyo Email Exclusion Sync -> Klaviyo SMS Exclusion Sync)...", brand.Code);

            // Paso 1: Descargar y persistir clientes desde Klaviyo
            await _klaviyoCustomerFetcher.FetchCustomersAsync(brand, cancellationToken);

            // Paso 2: Validar emails contra el servicio CRC
            await _crcEmailValidationService.ValidateEmailsAsync(brand, cancellationToken);

            // Paso 3: Validar teléfonos contra el servicio CRC
            await _crcPhoneValidationService.ValidatePhonesAsync(brand, cancellationToken);

            // Paso 4: Sincronizar hacia Klaviyo (bulk unsubscribe) los clientes excluidos por CRC
            await _klaviyoEmailExclusionSyncService.SyncEmailExclusionsAsync(brand, cancellationToken);

            // Paso 5: Sincronizar hacia Klaviyo (custom property "Consentimiento SMS CRC") los
            // clientes excluidos de SMS por CRC. Último paso del pipeline.
            await _klaviyoSmsExclusionSyncService.SyncSmsExclusionsAsync(brand, cancellationToken);

            _logger.LogInformation("[{Brand}] Pipeline completado.", brand.Code);
        }
        catch (Exception ex)
        {
            if (ex.WasCancelledByRequest(cancellationToken))
                _logger.LogInformation("[{Brand}] Pipeline detenido manualmente.", brand.Code);
            else
                _logger.LogError(ex, "[{Brand}] Error en el pipeline de la marca.", brand.Code);
        }
    }

    private async Task RunExternalProcessAsync(CancellationToken cancellationToken)
    {
        var executable = _configuration["ProcessSettings:ExecutablePath"];
        var arguments = _configuration["ProcessSettings:Arguments"] ?? "";
        var workingDir = _configuration["ProcessSettings:WorkingDirectory"] ?? "";

        if (string.IsNullOrEmpty(executable))
        {
            _logger.LogWarning("ProcessSettings:ExecutablePath is not configured. Skipping external process run.");
            return;
        }

        _logger.LogInformation("Launching external process: {Executable} {Arguments}", executable, arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        
        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                _logger.LogInformation("[Process Output] {Data}", e.Data);
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                _logger.LogError("[Process Error] {Data}", e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);
        _logger.LogInformation("External process exited with code {ExitCode}", process.ExitCode);
    }
}
