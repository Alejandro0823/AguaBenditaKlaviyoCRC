namespace KlaviyoCRC;

/// <summary>Estado observable del programador: qué es lo próximo y qué pasó la última vez.</summary>
public sealed record SchedulerStatus(
    DateTime? NextRunAt,
    DateTime? LastRunAt,
    bool? LastRunSucceeded,
    bool IsRunningNow,
    bool IsPaused);

public interface ISchedulerStatusService
{
    SchedulerStatus Current { get; }

    /// <summary>Se dispara con cada cambio de estado (útil para refrescar la UI).</summary>
    event Action<SchedulerStatus>? StatusChanged;

    /// <summary>
    /// Se dispara SOLO cuando StopAll()/Resume() cambian el estado de pausa. Es una señal aparte
    /// de StatusChanged para que SchedulerService pueda escucharla e interrumpir su espera sin
    /// terminar cancelándose a sí mismo cada vez que reporta NextRunAt/IsRunningNow (que también
    /// disparan StatusChanged).
    /// </summary>
    event Action<bool>? PauseStateChanged;

    void SetNextRunAt(DateTime? nextRunAt);

    /// <summary>
    /// Marca el inicio de una corrida (automática o manual) y entrega el token que debe usarse
    /// para ejecutarla: se cancela si se llama StopAll() mientras está en curso, o si
    /// <paramref name="externalToken"/> se cancela (p.ej. apagado de la app).
    /// </summary>
    CancellationToken BeginRun(CancellationToken externalToken);

    void MarkRunCompleted(bool succeeded);

    /// <summary>
    /// Corta de inmediato cualquier proceso en curso (cancela el token entregado por BeginRun) y
    /// deja el programador en pausa para que no arranque una corrida nueva mientras se reprograma.
    /// </summary>
    void StopAll();

    /// <summary>Reanuda el programador: la próxima corrida se calcula con la configuración vigente.</summary>
    void Resume();
}

/// <summary>
/// Mismo patrón que <see cref="TrayLogViewerProvider"/>: guarda el último estado conocido y
/// notifica a quien esté escuchando (la ventana "Ver interfaz", si está abierta) para que se
/// pueda mostrar "Próxima ejecución" / "Última ejecución" en tiempo real, y coordina el botón
/// "Detener procesos" tanto con las corridas automáticas (SchedulerService) como con "Ejecutar
/// ahora" (LogViewerForm), sin importar cuál de las dos disparó la corrida actual.
/// </summary>
public sealed class SchedulerStatusService : ISchedulerStatusService
{
    private readonly object _lock = new();
    private SchedulerStatus _current = new(null, null, null, false, false);
    private CancellationTokenSource? _currentRunCts;

    public event Action<SchedulerStatus>? StatusChanged;
    public event Action<bool>? PauseStateChanged;

    public SchedulerStatus Current
    {
        get { lock (_lock) return _current; }
    }

    public void SetNextRunAt(DateTime? nextRunAt) => Update(s => s with { NextRunAt = nextRunAt });

    public CancellationToken BeginRun(CancellationToken externalToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        lock (_lock)
        {
            _currentRunCts = cts;
        }

        Update(s => s with { IsRunningNow = true });
        return cts.Token;
    }

    public void MarkRunCompleted(bool succeeded)
    {
        lock (_lock)
        {
            _currentRunCts?.Dispose();
            _currentRunCts = null;
        }

        Update(s => s with { IsRunningNow = false, LastRunAt = DateTime.Now, LastRunSucceeded = succeeded });
    }

    public void StopAll()
    {
        CancellationTokenSource? runCts;
        lock (_lock)
        {
            runCts = _currentRunCts;
        }

        try
        {
            runCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // La corrida terminó justo mientras se pedía detenerla: no hay nada que cancelar.
        }

        var wasChanged = false;
        Update(s =>
        {
            wasChanged = !s.IsPaused;
            return s with { IsPaused = true, NextRunAt = null };
        });

        if (wasChanged)
            PauseStateChanged?.Invoke(true);
    }

    public void Resume()
    {
        var wasChanged = false;
        Update(s =>
        {
            wasChanged = s.IsPaused;
            return s with { IsPaused = false };
        });

        if (wasChanged)
            PauseStateChanged?.Invoke(false);
    }

    private void Update(Func<SchedulerStatus, SchedulerStatus> mutate)
    {
        SchedulerStatus updated;
        lock (_lock)
        {
            _current = mutate(_current);
            updated = _current;
        }

        StatusChanged?.Invoke(updated);
    }
}
