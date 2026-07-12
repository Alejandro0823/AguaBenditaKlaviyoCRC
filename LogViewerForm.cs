namespace KlaviyoCRC;

/// <summary>
/// Ventana "Ver interfaz": panel de texto plano con el mismo log que antes se veía
/// por consola. Al cerrarla con la X solo se oculta (la app sigue corriendo en la
/// bandeja); solo se destruye realmente al salir de la aplicación.
/// </summary>
public sealed class LogViewerForm : Form
{
    private readonly TextBox _textBox;
    private readonly TrayLogViewerProvider _logProvider;

    public LogViewerForm(TrayLogViewerProvider logProvider, Icon? icon)
    {
        _logProvider = logProvider;

        Text = "KlaviyoCRC — Estado de ejecución";
        Width = 900;
        Height = 550;
        StartPosition = FormStartPosition.CenterScreen;
        if (icon != null)
            Icon = icon;

        _textBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9.5f),
            BackColor = Color.Black,
            ForeColor = Color.WhiteSmoke,
        };
        Controls.Add(_textBox);

        Load += (_, _) => LoadBufferedLines();
        FormClosing += OnFormClosing;

        _logProvider.LineWritten += OnLineWritten;
    }

    private void LoadBufferedLines()
    {
        var lines = _logProvider.GetBufferedLines();
        _textBox.Text = string.Join(Environment.NewLine, lines);
        ScrollToEnd();
    }

    private void OnLineWritten(string line)
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(AppendLine), line);
        }
        else
        {
            AppendLine(line);
        }
    }

    private void AppendLine(string line)
    {
        if (IsDisposed)
            return;

        _textBox.AppendText(line + Environment.NewLine);
        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        _textBox.SelectionStart = _textBox.TextLength;
        _textBox.ScrollToCaret();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>Cierra realmente la ventana (usado solo al salir de la aplicación).</summary>
    public void ForceClose()
    {
        FormClosing -= OnFormClosing;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _logProvider.LineWritten -= OnLineWritten;
        }

        base.Dispose(disposing);
    }
}
