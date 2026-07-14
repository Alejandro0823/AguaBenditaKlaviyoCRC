using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KlaviyoCRC;

/// <summary>
/// Contexto de aplicación sin ventana principal: solo vive en la bandeja del sistema.
/// Controla el ciclo de vida del <see cref="IHost"/> (que ya corre SchedulerService)
/// y la ventana opcional "Ver interfaz".
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly IHost _host;
    private readonly TrayLogViewerProvider _logProvider;
    private readonly ISchedulerConfigService _schedulerConfigService;
    private readonly ISchedulerStatusService _schedulerStatusService;
    private readonly IProcessExecutor _processExecutor;
    private readonly ILogger<TrayApplicationContext> _logger;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _appIcon;
    private LogViewerForm? _logViewerForm;
    private bool _exiting;

    public TrayApplicationContext(IHost host, TrayLogViewerProvider logProvider)
    {
        _host = host;
        _logProvider = logProvider;
        _logger = host.Services.GetRequiredService<ILogger<TrayApplicationContext>>();
        _schedulerConfigService = host.Services.GetRequiredService<ISchedulerConfigService>();
        _schedulerStatusService = host.Services.GetRequiredService<ISchedulerStatusService>();
        _processExecutor = host.Services.GetRequiredService<IProcessExecutor>();

        _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("KlaviyoCRC — Sincronización Klaviyo/CRC") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Ver interfaz", null, (_, _) => ShowLogViewer());
        menu.Items.Add("Cerrar aplicación", null, (_, _) => _ = ExitApplicationAsync());

        _notifyIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "KlaviyoCRC - Sincronización Klaviyo/CRC",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowLogViewer();
    }

    private void ShowLogViewer()
    {
        if (_logViewerForm == null || _logViewerForm.IsDisposed)
        {
            _logViewerForm = new LogViewerForm(_logProvider, _schedulerConfigService, _schedulerStatusService, _processExecutor, _appIcon);
        }

        if (!_logViewerForm.Visible)
        {
            _logViewerForm.Show();
        }

        if (_logViewerForm.WindowState == FormWindowState.Minimized)
        {
            _logViewerForm.WindowState = FormWindowState.Normal;
        }

        _logViewerForm.Activate();
        _logViewerForm.BringToFront();
    }

    private async Task ExitApplicationAsync()
    {
        if (_exiting)
            return;
        _exiting = true;

        _notifyIcon.Visible = false;

        try
        {
            _logger.LogInformation("Cierre solicitado desde la bandeja del sistema. Deteniendo procesos en curso...");
            await _host.StopAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al detener la aplicación de forma controlada.");
        }

        _logViewerForm?.ForceClose();
        _notifyIcon.Dispose();
        ExitThread();
    }
}
