using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using System.Collections.Concurrent;

namespace KlaviyoCRC;

/// <summary>
/// Formatter de consola que colorea cada línea según la marca (BrandOptions.Code)
/// presente en el logging scope (ver ProcessExecutor.RunBrandPipelineAsync). Los
/// logs sin marca en el scope se imprimen sin color (formato por defecto). Los
/// colores se asignan por orden de aparición desde una paleta fija, así que nuevas
/// marcas en appsettings.json quedan coloreadas automáticamente.
///
/// El color se aplica embebiendo códigos de escape ANSI directamente en el texto
/// (mismo mecanismo que usan los formatters propios de .NET), en vez de asignar
/// Console.ForegroundColor: el TextWriter que recibe Write() es un buffer interno
/// que se vuelca a la consola después, en otro hilo, así que cambiar el color de
/// consola durante Write() no tiene ningún efecto sobre lo que finalmente se imprime.
/// </summary>
public sealed class BrandConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "brand";
    private const string AnsiReset = "\x1B[0m";

    // Colores ANSI "brillantes" (90-97) para que cada marca resalte con fuerza en consola.
    // El amarillo brillante (93) se deja fuera de esta paleta: está reservado para los logs
    // informativos sin marca (arranque, scheduler, hosting), ver InfoNoBrandColor más abajo.
    private static readonly string[] Palette =
    {
        "\x1B[92m", // verde brillante
        "\x1B[94m", // azul brillante
        "\x1B[96m", // cian brillante
        "\x1B[95m", // magenta brillante
        "\x1B[91m", // rojo brillante
        "\x1B[36m", // cian
        "\x1B[33m", // amarillo
    };

    // Error/Critical siempre en rojo, sin importar el color de la marca, para que resalten sobre el resto del log.
    private const string ErrorColor = "\x1B[91m";

    // Logs informativos que no pertenecen a ninguna marca (arranque del host, scheduler, etc.)
    // en amarillo brillante, para diferenciarlos del texto plano por defecto.
    private const string InfoNoBrandColor = "\x1B[93m";

    private static readonly ConcurrentDictionary<string, string> BrandColors = new();
    private static int _nextColorIndex = -1;

    public BrandConsoleFormatter() : base(FormatterName)
    {
    }

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception == null)
            return;

        var brandCode = FindBrandCode(scopeProvider);
        var isError = logEntry.LogLevel is LogLevel.Error or LogLevel.Critical;

        string? ansiColor;
        if (isError)
            ansiColor = ErrorColor;
        else if (brandCode != null)
            ansiColor = GetColorForBrand(brandCode);
        else if (logEntry.LogLevel == LogLevel.Information)
            ansiColor = InfoNoBrandColor;
        else
            ansiColor = null;

        if (ansiColor != null)
            textWriter.Write(ansiColor);

        textWriter.Write($"[{logEntry.LogLevel}] {logEntry.Category}: {message}");
        if (logEntry.Exception != null)
            textWriter.Write(logEntry.Exception);

        if (ansiColor != null)
            textWriter.Write(AnsiReset);

        textWriter.Write(Environment.NewLine);
    }

    private static string? FindBrandCode(IExternalScopeProvider? scopeProvider)
    {
        string? found = null;
        scopeProvider?.ForEachScope((scope, _) =>
        {
            if (found != null)
                return;

            if (scope is IEnumerable<KeyValuePair<string, object>> scopeValues)
            {
                foreach (var kvp in scopeValues)
                {
                    if (kvp.Key == "Brand" && kvp.Value is string code)
                    {
                        found = code;
                        break;
                    }
                }
            }
        }, (object?)null);

        return found;
    }

    private static string GetColorForBrand(string brandCode)
    {
        return BrandColors.GetOrAdd(brandCode, _ =>
        {
            var index = Interlocked.Increment(ref _nextColorIndex);
            return Palette[index % Palette.Length];
        });
    }
}
