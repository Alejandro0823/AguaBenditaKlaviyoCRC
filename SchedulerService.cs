using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KlaviyoCRC;

public class SchedulerService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IProcessExecutor _processExecutor;
    private readonly ILogger<SchedulerService> _logger;

    public SchedulerService(
        IConfiguration configuration,
        IProcessExecutor processExecutor,
        ILogger<SchedulerService> logger)
    {
        _configuration = configuration;
        _processExecutor = processExecutor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Scheduler Background Service is starting.");

        var runOnStartup = _configuration.GetValue<bool>("SchedulerSettings:RunOnStartup");
        if (runOnStartup)
        {
            _logger.LogInformation("RunOnStartup is enabled. Triggering immediate execution on startup.");
            await _processExecutor.ExecuteAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var type = _configuration["SchedulerSettings:ScheduleType"] ?? "Interval";
                if (string.Equals(type, "Daily", StringComparison.OrdinalIgnoreCase))
                {
                    var delay = CalculateNextDailyDelay();
                    if (delay == null)
                    {
                        _logger.LogWarning("No valid daily run times configured. Checking again in 1 minute.");
                        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                        continue;
                    }

                    _logger.LogInformation("Next daily task scheduled in {Duration} (at {Time})", 
                        delay.Value, DateTime.Now.Add(delay.Value));

                    await Task.Delay(delay.Value, stoppingToken);
                }
                else
                {
                    // Default to Interval mode
                    var seconds = _configuration.GetValue<int>("SchedulerSettings:IntervalSeconds", 60);
                    if (seconds <= 0) seconds = 60;

                    _logger.LogInformation("Next task scheduled in {Seconds} seconds", seconds);
                    await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
                }

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                _logger.LogInformation("Triggering scheduled process execution at local machine time: {Time}", DateTime.Now);
                await _processExecutor.ExecuteAsync(stoppingToken);
            }
            catch (TaskCanceledException)
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

        _logger.LogInformation("Scheduler Background Service has stopped.");
    }

    private TimeSpan? CalculateNextDailyDelay()
    {
        var dailyTimes = _configuration.GetSection("SchedulerSettings:DailyRunTimes").Get<string[]>();
        if (dailyTimes == null || dailyTimes.Length == 0)
        {
            return null;
        }

        var now = DateTime.Now;
        DateTime? nextRun = null;

        foreach (var timeStr in dailyTimes)
        {
            if (TimeSpan.TryParse(timeStr, out var timeOfDay))
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
                _logger.LogWarning("Invalid time format in configuration: {TimeStr}. Use HH:mm:ss format.", timeStr);
            }
        }

        if (nextRun == null)
        {
            return null;
        }

        return nextRun.Value - now;
    }
}
