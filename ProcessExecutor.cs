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
    private readonly ICrcEmailValidationService _crcEmailValidationService;
    private readonly ILogger<ProcessExecutor> _logger;

    public ProcessExecutor(
        IConfiguration configuration,
        IKlaviyoCustomerFetcher klaviyoCustomerFetcher,
        ICrcEmailValidationService crcEmailValidationService,
        ILogger<ProcessExecutor> logger)
    {
        _configuration = configuration;
        _klaviyoCustomerFetcher = klaviyoCustomerFetcher;
        _crcEmailValidationService = crcEmailValidationService;
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
            _logger.LogError(ex, "Error occurred during process execution");
        }
    }

    private async Task RunInternalProcessAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Executing internal custom process logic");

        // Paso 1: Descargar y persistir clientes desde Klaviyo
        await _klaviyoCustomerFetcher.FetchCustomersAsync(cancellationToken);

        // Paso 2: Validar emails contra el servicio CRC (se ejecuta una vez termine el paso anterior)
        await _crcEmailValidationService.ValidateEmailsAsync(cancellationToken);

        _logger.LogInformation("Internal process: Completed task logic at {Time}", DateTime.Now);
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
