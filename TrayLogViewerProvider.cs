using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace KlaviyoCRC;

/// <summary>
/// ILoggerProvider en memoria que alimenta la ventana "Ver interfaz" de la bandeja.
/// Mantiene un buffer acotado (para que la ventana muestre historial reciente al
/// abrirse) y notifica cada línea nueva vía <see cref="LineWritten"/> para que la
/// ventana, si está abierta, se actualice en tiempo real.
/// </summary>
public sealed class TrayLogViewerProvider : ILoggerProvider
{
    private const int MaxBufferedLines = 2000;

    private readonly ConcurrentQueue<string> _buffer = new();

    public event Action<string>? LineWritten;

    public IReadOnlyCollection<string> GetBufferedLines() => _buffer.ToArray();

    public ILogger CreateLogger(string categoryName) => new TrayLogger(this, categoryName);

    private void Append(string line)
    {
        _buffer.Enqueue(line);
        while (_buffer.Count > MaxBufferedLines && _buffer.TryDequeue(out _))
        {
        }

        LineWritten?.Invoke(line);
    }

    public void Dispose()
    {
    }

    private sealed class TrayLogger : ILogger
    {
        private readonly TrayLogViewerProvider _owner;
        private readonly string _categoryName;

        public TrayLogger(TrayLogViewerProvider owner, string categoryName)
        {
            _owner = owner;
            _categoryName = categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception == null)
                return;

            var line = $"{DateTime.Now:HH:mm:ss} [{logLevel}] {_categoryName}: {message}";
            if (exception != null)
                line += Environment.NewLine + exception;

            _owner.Append(line);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
