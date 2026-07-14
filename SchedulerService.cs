using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KlaviyoCRC;

public class SchedulerService : BackgroundService
{
    private readonly IProcessExecutor _processExecutor;
    private readonly ISchedulerConfigService _schedulerConfigService;
    private readonly ISchedulerStatusService _schedulerStatusService;
    private readonly ILogger<SchedulerService> _logger;

    // Se cancela (y se reemplaza) cada vez que la UI guarda una nueva configuración, para
    // interrumpir el Task.Delay en curso y recalcular el próximo horario de inmediato en vez de
    // esperar a que termine el intervalo viejo.
    private CancellationTokenSource _configChangedCts = new();

    public SchedulerService(
        IProcessExecutor processExecutor,
        ISchedulerConfigService schedulerConfigService,
        ISchedulerStatusService schedulerStatusService,
        ILogger<SchedulerService> logger)
    {
        _processExecutor = processExecutor;
        _schedulerConfigService = schedulerConfigService;
        _schedulerStatusService = schedulerStatusService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Scheduler Background Service is starting.");

        _schedulerConfigService.ConfigChanged += OnConfigChanged;
        _schedulerStatusService.PauseStateChanged += OnPauseStateChanged;
        try
        {
            await RunLoopAsync(stoppingToken);
        }
        finally
        {
            _schedulerConfigService.ConfigChanged -= OnConfigChanged;
            _schedulerStatusService.PauseStateChanged -= OnPauseStateChanged;
        }

        _logger.LogInformation("Scheduler Background Service has stopped.");
    }

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_schedulerConfigService.GetCurrent().RunOnStartup)
            {
                _logger.LogInformation("RunOnStartup is enabled. Triggering immediate execution on startup.");
                await RunProcessAsync(stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error durante la ejecución inicial (RunOnStartup).");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var configChangedCts = new CancellationTokenSource();
                _configChangedCts = configChangedCts;

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, configChangedCts.Token);

                if (_schedulerStatusService.Current.IsPaused)
                {
                    _logger.LogInformation("Ejecución automática detenida desde la interfaz; en espera hasta que se reanude (guardar cambios reanuda automáticamente).");
                    await Task.Delay(Timeout.InfiniteTimeSpan, linkedCts.Token);
                    continue;
                }

                var config = _schedulerConfigService.GetCurrent();

                TimeSpan? delay;
                if (config.ScheduleType == ScheduleKind.Daily)
                {
                    delay = CalculateNextDailyDelay(config.DailyRunTimes, _logger);
                    if (delay == null)
                    {
                        _logger.LogWarning("No hay horarios diarios válidos configurados. Revisando de nuevo en 1 minuto.");
                        _schedulerStatusService.SetNextRunAt(null);
                        await Task.Delay(TimeSpan.FromMinutes(1), linkedCts.Token);
                        continue;
                    }
                }
                else
                {
                    var seconds = config.IntervalSeconds;
                    if (seconds <= 0) seconds = 60;
                    delay = TimeSpan.FromSeconds(seconds);
                }

                var nextRunAt = DateTime.Now.Add(delay.Value);
                _schedulerStatusService.SetNextRunAt(nextRunAt);
                _logger.LogInformation("Next task scheduled in {Duration} (at {Time})", delay.Value, nextRunAt);

                await Task.Delay(delay.Value, linkedCts.Token);

                if (stoppingToken.IsCancellationRequested)
                    break;

                _logger.LogInformation("Triggering scheduled process execution at local machine time: {Time}", DateTime.Now);
                await RunProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Configuración del programador actualizada; recalculando el próximo horario.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Scheduler is stopping due to task cancellation.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred in the scheduler background loop");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task RunProcessAsync(CancellationToken stoppingToken)
    {
        // El token que entrega BeginRun se cancela si desde la UI se pide "Detener procesos"
        // mientras esta corrida está en curso (además de cancelarse si la app se apaga).
        var runToken = _schedulerStatusService.BeginRun(stoppingToken);
        try
        {
            // ProcessExecutor ya captura sus propios errores internamente (no relanza), así que
            // el resultado se infiere de si el token terminó cancelado (detenido manualmente) o no.
            await _processExecutor.ExecuteAsync(runToken);
            _schedulerStatusService.MarkRunCompleted(succeeded: !runToken.IsCancellationRequested);
        }
        catch (Exception)
        {
            _schedulerStatusService.MarkRunCompleted(succeeded: false);
            throw;
        }
    }

    private void OnConfigChanged(SchedulerConfig _) => InterruptCurrentWait();

    private void OnPauseStateChanged(bool _) => InterruptCurrentWait();

    private void InterruptCurrentWait()
    {
        try
        {
            _configChangedCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static TimeSpan? CalculateNextDailyDelay(IReadOnlyList<string> dailyTimes, ILogger logger)
    {
        if (dailyTimes.Count == 0)
        {
            return null;
        }

        var now = DateTime.Now;
        DateTime? nextRun = null;

        foreach (var timeStr in dailyTimes)
        {
            // Invariante: el horario se guarda siempre en formato HH:mm:ss con ':' literal, sin
            // importar la configuración regional de Windows en la máquina donde corra la app.
            if (TimeSpan.TryParse(timeStr, System.Globalization.CultureInfo.InvariantCulture, out var timeOfDay))
            {
                var candidate = now.Date.Add(timeOfDay);

                if (candidate <= now)
                {
                    candidate = candidate.AddDays(1);
                }

                if (nextRun == null || candidate < nextRun.Value)
                {
                    nextRun = candidate;
                }
            }
            else
            {
                logger.LogWarning("Invalid time format in configuration: {TimeStr}. Use HH:mm:ss format.", timeStr);
            }
        }

        if (nextRun == null)
        {
            return null;
        }

        return nextRun.Value - now;
    }
}
