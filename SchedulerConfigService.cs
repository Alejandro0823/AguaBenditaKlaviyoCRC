using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KlaviyoCRC;

public enum ScheduleKind
{
    Interval,
    Daily,
}

public enum IntervalUnit
{
    Seconds,
    Minutes,
    Hours,
}

/// <summary>Configuración del programador tal como la edita el usuario desde la UI.</summary>
public sealed class SchedulerConfig
{
    public ScheduleKind ScheduleType { get; set; } = ScheduleKind.Interval;
    public int IntervalValue { get; set; } = 60;
    public IntervalUnit IntervalUnit { get; set; } = IntervalUnit.Seconds;
    public List<string> DailyRunTimes { get; set; } = new();
    public bool RunOnStartup { get; set; } = true;

    public int IntervalSeconds => IntervalUnit switch
    {
        IntervalUnit.Hours => IntervalValue * 3600,
        IntervalUnit.Minutes => IntervalValue * 60,
        _ => IntervalValue,
    };

    public SchedulerConfig Clone() => new()
    {
        ScheduleType = ScheduleType,
        IntervalValue = IntervalValue,
        IntervalUnit = IntervalUnit,
        DailyRunTimes = new List<string>(DailyRunTimes),
        RunOnStartup = RunOnStartup,
    };
}

public interface ISchedulerConfigService
{
    SchedulerConfig GetCurrent();
    Task SaveAsync(SchedulerConfig config, CancellationToken cancellationToken = default);
    event Action<SchedulerConfig>? ConfigChanged;
}

/// <summary>
/// Lee/escribe la configuración del programador en %AppData%\KlaviyoCRC\scheduler-settings.json,
/// separado de appsettings.json (que puede vivir en Program Files sin permisos de escritura y
/// contiene secretos que no queremos arriesgar a corromper).
///
/// GetCurrent/SaveAsync leen y escriben ese archivo directamente en vez de pasar por
/// IConfiguration: los proveedores de configuración de .NET no truncan arrays al superponerse
/// (si el archivo de AppData define menos "DailyRunTimes" que appsettings.json, los sobrantes de
/// appsettings.json seguirían "filtrándose"), así que leer el archivo tal cual evita ese problema
/// y garantiza que lo mostrado sea exactamente lo último guardado.
/// </summary>
public sealed class SchedulerConfigService : ISchedulerConfigService
{
    private const int MinIntervalSeconds = 10;

    private readonly string _filePath;
    private readonly ILogger<SchedulerConfigService> _logger;

    public event Action<SchedulerConfig>? ConfigChanged;

    public SchedulerConfigService(ILogger<SchedulerConfigService> logger)
    {
        _logger = logger;
        _filePath = GetSettingsFilePath();
    }

    public static string GetSettingsFilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KlaviyoCRC");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "scheduler-settings.json");
    }

    /// <summary>
    /// Crea el archivo en AppData con los valores de appsettings.json como semilla, si todavía
    /// no existe. Debe llamarse antes de agregar la capa de configuración de AppData al builder.
    /// </summary>
    public static void SeedIfMissing(IConfiguration initialConfiguration)
    {
        var filePath = GetSettingsFilePath();
        if (File.Exists(filePath))
            return;

        var section = initialConfiguration.GetSection("SchedulerSettings");
        var scheduleType = section["ScheduleType"] ?? "Interval";
        var intervalSeconds = section.GetValue<int>("IntervalSeconds", 60);
        if (intervalSeconds <= 0) intervalSeconds = 60;
        var dailyTimes = section.GetSection("DailyRunTimes").Get<string[]>() ?? Array.Empty<string>();
        var runOnStartup = section.GetValue<bool>("RunOnStartup", true);

        var (value, unit) = SplitIntoFriendlyUnit(intervalSeconds);

        var payload = new SchedulerSettingsFile
        {
            SchedulerSettings = new SchedulerSettingsSection
            {
                ScheduleType = scheduleType,
                IntervalValue = value,
                IntervalUnit = unit.ToString(),
                IntervalSeconds = intervalSeconds,
                DailyRunTimes = dailyTimes.ToList(),
                RunOnStartup = runOnStartup,
            },
        };

        File.WriteAllText(filePath, JsonSerializer.Serialize(payload, SerializerOptions));
    }

    public SchedulerConfig GetCurrent()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var parsed = JsonSerializer.Deserialize<SchedulerSettingsFile>(json, DeserializerOptions);
                if (parsed?.SchedulerSettings != null)
                    return FromSection(parsed.SchedulerSettings);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer scheduler-settings.json; se usarán valores por defecto.");
        }

        return new SchedulerConfig();
    }

    public async Task SaveAsync(SchedulerConfig config, CancellationToken cancellationToken = default)
    {
        Validate(config);

        var payload = new SchedulerSettingsFile
        {
            SchedulerSettings = new SchedulerSettingsSection
            {
                ScheduleType = config.ScheduleType.ToString(),
                IntervalValue = config.IntervalValue,
                IntervalUnit = config.IntervalUnit.ToString(),
                IntervalSeconds = config.IntervalSeconds,
                DailyRunTimes = config.DailyRunTimes,
                RunOnStartup = config.RunOnStartup,
            },
        };

        var json = JsonSerializer.Serialize(payload, SerializerOptions);

        // Escritura atómica: primero a un .tmp y luego se reemplaza, para que un lector
        // concurrente (SchedulerService, o el reload de IConfiguration) nunca vea un JSON a medias.
        var tempPath = _filePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, _filePath, overwrite: true);

        _logger.LogInformation(
            "Configuración del programador guardada: {ScheduleType}, intervalo {Value} {Unit}, RunOnStartup={RunOnStartup}",
            config.ScheduleType, config.IntervalValue, config.IntervalUnit, config.RunOnStartup);

        ConfigChanged?.Invoke(config.Clone());
    }

    private static void Validate(SchedulerConfig config)
    {
        if (config.IntervalValue <= 0)
            throw new ArgumentException("El valor del intervalo debe ser mayor que cero.");

        if (config.ScheduleType == ScheduleKind.Interval && config.IntervalSeconds < MinIntervalSeconds)
            throw new ArgumentException($"El intervalo mínimo permitido es de {MinIntervalSeconds} segundos.");

        if (config.ScheduleType == ScheduleKind.Daily)
        {
            if (config.DailyRunTimes.Count == 0)
                throw new ArgumentException("Debe configurar al menos un horario para el modo 'Horas fijas del día'.");

            foreach (var time in config.DailyRunTimes)
            {
                // Invariante: incluso si el instalador cambia la configuración regional de Windows,
                // el horario guardado debe seguir siendo válido en formato HH:mm:ss con ':' literal.
                if (!TimeSpan.TryParse(time, System.Globalization.CultureInfo.InvariantCulture, out _))
                    throw new ArgumentException($"Horario inválido: '{time}'. Use el formato HH:mm.");
            }
        }
    }

    private static SchedulerConfig FromSection(SchedulerSettingsSection section)
    {
        var scheduleType = string.Equals(section.ScheduleType, "Daily", StringComparison.OrdinalIgnoreCase)
            ? ScheduleKind.Daily
            : ScheduleKind.Interval;

        var intervalSeconds = section.IntervalSeconds > 0 ? section.IntervalSeconds : 60;

        int value;
        IntervalUnit unit;
        if (section.IntervalValue > 0 && Enum.TryParse<IntervalUnit>(section.IntervalUnit, true, out var parsedUnit))
        {
            value = section.IntervalValue;
            unit = parsedUnit;
        }
        else
        {
            (value, unit) = SplitIntoFriendlyUnit(intervalSeconds);
        }

        return new SchedulerConfig
        {
            ScheduleType = scheduleType,
            IntervalValue = value,
            IntervalUnit = unit,
            DailyRunTimes = section.DailyRunTimes ?? new List<string>(),
            RunOnStartup = section.RunOnStartup,
        };
    }

    /// <summary>Convierte segundos "crudos" a la unidad más amigable posible sin perder precisión.</summary>
    private static (int Value, IntervalUnit Unit) SplitIntoFriendlyUnit(int intervalSeconds)
    {
        if (intervalSeconds % 3600 == 0) return (intervalSeconds / 3600, IntervalUnit.Hours);
        if (intervalSeconds % 60 == 0) return (intervalSeconds / 60, IntervalUnit.Minutes);
        return (intervalSeconds, IntervalUnit.Seconds);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions DeserializerOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class SchedulerSettingsFile
    {
        public SchedulerSettingsSection SchedulerSettings { get; set; } = new();
    }

    private sealed class SchedulerSettingsSection
    {
        public string ScheduleType { get; set; } = "Interval";
        public int IntervalValue { get; set; } = 60;
        public string IntervalUnit { get; set; } = "Seconds";
        public int IntervalSeconds { get; set; } = 60;
        public List<string> DailyRunTimes { get; set; } = new();
        public bool RunOnStartup { get; set; } = true;
    }
}
