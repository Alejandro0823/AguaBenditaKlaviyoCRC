using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using KlaviyoCRC;

// Evita dos instancias simultáneas (posible con autoarranque + doble clic manual):
// correr el mismo scheduler dos veces duplicaría llamadas a la API de Klaviyo/CRC y a la BD.
using var singleInstanceMutex = new Mutex(true, "KlaviyoCRC_TrayApp_SingleInstance", out var isFirstInstance);
if (!isFirstInstance)
{
    MessageBox.Show(
        "KlaviyoCRC ya se está ejecutando (revisa los íconos ocultos de la bandeja del sistema).",
        "KlaviyoCRC",
        MessageBoxButtons.OK,
        MessageBoxIcon.Information);
    return;
}

ApplicationConfiguration.Initialize();
Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // Cuando la app corre instalada (autoarranque, acceso directo), el directorio de
    // trabajo no es necesariamente la carpeta de instalación; anclar el content root
    // al directorio del ejecutable asegura que appsettings.json siempre se encuentre.
    ContentRootPath = AppContext.BaseDirectory,
});

// Configuración del programador editable desde la ventana "Ver interfaz": se guarda en una
// carpeta de usuario (no en appsettings.json, que puede vivir en Program Files sin permisos de
// escritura y contiene secretos). Se siembra una sola vez desde appsettings.json si el archivo de
// AppData todavía no existe; de ahí en adelante SchedulerConfigService lee/escribe ese archivo
// directamente (no vía IConfiguration, para evitar que un array más corto en AppData no logre
// truncar el de appsettings.json al superponerse).
SchedulerConfigService.SeedIfMissing(builder.Configuration);

// Colorea cada línea del log de consola según la marca (BrandOptions.Code) en ejecución.
// Se conserva por si la app se ejecuta manualmente desde una terminal para depurar.
builder.Logging.AddConsole(options => options.FormatterName = BrandConsoleFormatter.FormatterName)
    .AddConsoleFormatter<BrandConsoleFormatter, ConsoleFormatterOptions>();

// Alimenta la ventana "Ver interfaz" de la bandeja con el mismo log.
var trayLogProvider = new TrayLogViewerProvider();
builder.Logging.AddProvider(trayLogProvider);

// Register dependencies
builder.Services.AddTransient<IKlaviyoCustomerFetcher, KlaviyoCustomerFetcher>();
builder.Services.AddTransient<ICrcApiTokenService, CrcApiTokenService>();
builder.Services.AddTransient<ICrcEmailValidationService, CrcEmailValidationService>();
builder.Services.AddTransient<ICrcPhoneValidationService, CrcPhoneValidationService>();
builder.Services.AddTransient<IKlaviyoEmailExclusionSyncService, KlaviyoEmailExclusionSyncService>();
builder.Services.AddSingleton<IProcessExecutor, ProcessExecutor>();
builder.Services.AddSingleton<ISchedulerConfigService, SchedulerConfigService>();
builder.Services.AddSingleton<ISchedulerStatusService, SchedulerStatusService>();
builder.Services.AddHostedService<SchedulerService>();

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
Application.ThreadException += (_, e) =>
    logger.LogError(e.Exception, "Excepción no controlada en el hilo de interfaz.");
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    logger.LogCritical(e.ExceptionObject as Exception, "Excepción no controlada en un hilo de fondo.");

await host.StartAsync();

using var trayContext = new TrayApplicationContext(host, trayLogProvider);
Application.Run(trayContext);

await host.StopAsync();
host.Dispose();
