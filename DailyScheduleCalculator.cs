using Microsoft.Extensions.Logging;

namespace KlaviyoCRC;

/// <summary>
/// Único punto donde se decide "cuál es el próximo horario en dispararse" a partir de una lista de
/// horas HH:mm:ss. Lo usan tanto el SchedulerService (para el disparo real) como la UI (para
/// mostrar una vista previa antes de guardar), así ambos siempre coinciden.
/// </summary>
internal static class DailyScheduleCalculator
{
    public static DateTime? GetNextRun(IReadOnlyList<string> dailyTimes, DateTime now, ILogger? logger = null)
    {
        DateTime? nextRun = null;

        foreach (var timeStr in dailyTimes)
        {
            if (TimeSpan.TryParse(timeStr, System.Globalization.CultureInfo.InvariantCulture, out var timeOfDay))
            {
                var candidate = now.Date.Add(timeOfDay);
                if (candidate <= now)
                    candidate = candidate.AddDays(1);

                if (nextRun == null || candidate < nextRun.Value)
                    nextRun = candidate;
            }
            else
            {
                logger?.LogWarning("Invalid time format in configuration: {TimeStr}. Use HH:mm:ss format.", timeStr);
            }
        }

        return nextRun;
    }
}
