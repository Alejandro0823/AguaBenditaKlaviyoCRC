namespace KlaviyoCRC;

/// <summary>
/// Ventana "Ver interfaz": arriba un panel "Programador" para configurar cada cuánto corren los
/// procesos (antes fijo en appsettings.json), abajo el mismo log de texto plano que antes se veía
/// por consola. Al cerrarla con la X solo se oculta (la app sigue corriendo en la bandeja); solo
/// se destruye realmente al salir de la aplicación.
/// </summary>
public sealed class LogViewerForm : Form
{
    private readonly TrayLogViewerProvider _logProvider;
    private readonly ISchedulerConfigService _schedulerConfigService;
    private readonly ISchedulerStatusService _schedulerStatusService;
    private readonly IProcessExecutor _processExecutor;

    private readonly TextBox _textBox;

    private readonly ComboBox _scheduleTypeCombo;
    private readonly FlowLayoutPanel _intervalPanel;
    private readonly NumericUpDown _intervalValueNumeric;
    private readonly ComboBox _intervalUnitCombo;
    private readonly FlowLayoutPanel _dailyPanel;
    private readonly ListBox _dailyTimesList;
    private readonly DateTimePicker _dailyTimePicker;
    private readonly Label _dailyTimesPreviewLabel;
    private readonly CheckBox _runOnStartupCheck;
    private readonly Label _nextRunLabel;
    private readonly Label _lastRunLabel;
    private readonly Button _stopButton;
    private readonly Button _saveButton;
    private readonly Button _runNowButton;
    private readonly Label _statusMessageLabel;

    private readonly System.Windows.Forms.Timer _countdownTimer;
    private bool _isLoadingConfig;

    public LogViewerForm(
        TrayLogViewerProvider logProvider,
        ISchedulerConfigService schedulerConfigService,
        ISchedulerStatusService schedulerStatusService,
        IProcessExecutor processExecutor,
        Icon? icon)
    {
        _logProvider = logProvider;
        _schedulerConfigService = schedulerConfigService;
        _schedulerStatusService = schedulerStatusService;
        _processExecutor = processExecutor;

        Text = "KlaviyoCRC — Estado de ejecución";
        Width = 950;
        Height = 650;
        MinimumSize = new Size(760, 480);
        StartPosition = FormStartPosition.CenterScreen;
        if (icon != null)
            Icon = icon;

        // Todo el panel se arma con FlowLayoutPanel/TableLayoutPanel (en vez de Location/Size
        // fijos en píxeles) para que la distribución se ajuste sola al ancho del texto de cada
        // control y al escalado de DPI de Windows; con coordenadas fijas, una etiqueta más ancha
        // de lo calculado terminaba montada sobre el control vecino.
        var schedulerGroup = new GroupBox
        {
            Text = "Programador",
            Dock = DockStyle.Top,
            Height = 275,
            Padding = new Padding(10, 8, 10, 10),
        };

        var rootTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        rootTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        rootTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var leftFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        var typeRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        var typeLabel = new Label { Text = "Tipo de programación:", AutoSize = true, Margin = new Padding(0, 6, 8, 0) };
        _scheduleTypeCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 200,
            Margin = new Padding(0),
        };
        _scheduleTypeCombo.Items.AddRange(new object[] { "Cada intervalo", "Horas fijas del día" });
        _scheduleTypeCombo.SelectedIndexChanged += (_, _) => { OnScheduleTypeChanged(); MarkDirty(); };
        typeRow.Controls.AddRange(new Control[] { typeLabel, _scheduleTypeCombo });

        _intervalPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        var intervalLabel = new Label { Text = "Ejecutar cada", AutoSize = true, Margin = new Padding(0, 6, 8, 0) };
        _intervalValueNumeric = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 1000,
            Value = 1,
            Width = 70,
            Margin = new Padding(0, 2, 8, 0),
        };
        _intervalValueNumeric.ValueChanged += (_, _) => MarkDirty();
        _intervalUnitCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 130,
            Margin = new Padding(0),
        };
        _intervalUnitCombo.Items.AddRange(new object[] { "segundos", "minutos", "horas" });
        _intervalUnitCombo.SelectedIndexChanged += (_, _) => MarkDirty();
        _intervalPanel.Controls.AddRange(new Control[] { intervalLabel, _intervalValueNumeric, _intervalUnitCombo });

        _dailyPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Visible = false, Margin = new Padding(0, 0, 0, 6) };
        _dailyTimesList = new ListBox { Size = new Size(170, 92), Margin = new Padding(0, 0, 10, 0) };
        var dailyButtonsColumn = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        _dailyTimePicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Time,
            ShowUpDown = true,
            Width = 120,
            Margin = new Padding(0, 0, 0, 6),
        };
        var addTimeButton = new Button { Text = "Agregar", Size = new Size(120, 28), Margin = new Padding(0, 0, 0, 6) };
        addTimeButton.Click += (_, _) => AddDailyTime();
        var removeTimeButton = new Button { Text = "Quitar", Size = new Size(120, 28), Margin = new Padding(0) };
        removeTimeButton.Click += (_, _) => RemoveSelectedDailyTime();
        dailyButtonsColumn.Controls.AddRange(new Control[] { _dailyTimePicker, addTimeButton, removeTimeButton });
        _dailyPanel.Controls.AddRange(new Control[] { _dailyTimesList, dailyButtonsColumn });

        // Vista previa que se recalcula en vivo (antes de guardar) para que sea imposible no darse
        // cuenta de horarios "viejos" que quedaron en la lista: siempre muestra TODOS los horarios
        // activos y cuál de ellos disparará primero, así el usuario ve el efecto real de la lista
        // completa y no solo del horario que acaba de agregar.
        _dailyTimesPreviewLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = Color.DimGray,
            Visible = false,
            Margin = new Padding(0, 0, 0, 6),
        };

        _runOnStartupCheck = new CheckBox
        {
            Text = "Ejecutar automáticamente al iniciar la aplicación",
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 6),
        };
        _runOnStartupCheck.CheckedChanged += (_, _) => MarkDirty();

        var statusRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        _nextRunLabel = new Label { Text = "Próxima ejecución: --", AutoSize = true, Margin = new Padding(0, 0, 20, 0) };
        _lastRunLabel = new Label { Text = "Última ejecución: --", AutoSize = true, Margin = new Padding(0) };
        statusRow.Controls.AddRange(new Control[] { _nextRunLabel, _lastRunLabel });

        leftFlow.Controls.AddRange(new Control[] { typeRow, _intervalPanel, _dailyPanel, _dailyTimesPreviewLabel, _runOnStartupCheck, statusRow });

        var rightFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(20, 0, 0, 0),
        };

        _stopButton = new Button { Text = "Detener procesos", Size = new Size(170, 32), Margin = new Padding(0, 0, 0, 8) };
        _stopButton.Click += (_, _) => ToggleStop();

        _saveButton = new Button { Text = "Guardar cambios", Size = new Size(170, 32), Enabled = false, Margin = new Padding(0, 0, 0, 8) };
        _saveButton.Click += async (_, _) => await SaveChangesAsync();

        _runNowButton = new Button { Text = "Ejecutar ahora", Size = new Size(170, 32), Margin = new Padding(0, 0, 0, 8) };
        _runNowButton.Click += async (_, _) => await RunNowAsync();

        _statusMessageLabel = new Label { Text = "", AutoSize = true, MaximumSize = new Size(170, 0), Margin = new Padding(0) };

        rightFlow.Controls.AddRange(new Control[] { _stopButton, _saveButton, _runNowButton, _statusMessageLabel });

        rootTable.Controls.Add(leftFlow, 0, 0);
        rootTable.Controls.Add(rightFlow, 1, 0);
        schedulerGroup.Controls.Add(rootTable);

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
        Controls.Add(schedulerGroup);

        _countdownTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _countdownTimer.Tick += (_, _) => RefreshStatusLabels(_schedulerStatusService.Current);

        Load += OnLoad;
        FormClosing += OnFormClosing;

        _logProvider.LineWritten += OnLineWritten;
        _schedulerStatusService.StatusChanged += OnStatusChanged;
    }

    private void OnLoad(object? sender, EventArgs e)
    {
        LoadBufferedLines();
        LoadSchedulerConfig();
        RefreshStatusLabels(_schedulerStatusService.Current);
        _countdownTimer.Start();
    }

    private void LoadSchedulerConfig()
    {
        _isLoadingConfig = true;
        try
        {
            var config = _schedulerConfigService.GetCurrent();

            _scheduleTypeCombo.SelectedIndex = config.ScheduleType == ScheduleKind.Daily ? 1 : 0;
            OnScheduleTypeChanged();

            _intervalValueNumeric.Value = Math.Clamp(config.IntervalValue, (int)_intervalValueNumeric.Minimum, (int)_intervalValueNumeric.Maximum);
            _intervalUnitCombo.SelectedIndex = config.IntervalUnit switch
            {
                IntervalUnit.Minutes => 1,
                IntervalUnit.Hours => 2,
                _ => 0,
            };

            _dailyTimesList.Items.Clear();
            foreach (var t in config.DailyRunTimes)
                _dailyTimesList.Items.Add(t);
            UpdateDailyTimesPreview();

            _runOnStartupCheck.Checked = config.RunOnStartup;

            _statusMessageLabel.Text = "";
        }
        finally
        {
            _isLoadingConfig = false;
            _saveButton.Enabled = false;
        }
    }

    private void OnScheduleTypeChanged()
    {
        var isDaily = _scheduleTypeCombo.SelectedIndex == 1;
        _intervalPanel.Visible = !isDaily;
        _dailyPanel.Visible = isDaily;
        _dailyTimesPreviewLabel.Visible = isDaily;
    }

    private void MarkDirty()
    {
        if (_isLoadingConfig)
            return;

        _saveButton.Enabled = true;
        _statusMessageLabel.Text = "";
    }

    private void AddDailyTime()
    {
        // Invariante: independientemente del formato de hora/separador regional de Windows en esta
        // instalación (el DateTimePicker se ve en el formato local, p.ej. 12h AM/PM), el horario se
        // guarda siempre como HH:mm:ss con ':' literal, para que se lea igual en cualquier máquina.
        var time = _dailyTimePicker.Value.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        if (!_dailyTimesList.Items.Contains(time))
        {
            var sorted = _dailyTimesList.Items.Cast<string>().Append(time).OrderBy(t => t).ToArray();
            _dailyTimesList.Items.Clear();
            _dailyTimesList.Items.AddRange(sorted);
        }

        UpdateDailyTimesPreview();
        MarkDirty();
    }

    private void RemoveSelectedDailyTime()
    {
        if (_dailyTimesList.SelectedItem == null)
            return;

        _dailyTimesList.Items.Remove(_dailyTimesList.SelectedItem);
        UpdateDailyTimesPreview();
        MarkDirty();
    }

    /// <summary>
    /// Recalcula, sobre la lista tal como está en pantalla (aunque todavía no se haya guardado),
    /// cuáles son todos los horarios activos y cuál de ellos dispararía primero. Existe porque
    /// "Agregar" solo añade a la lista: sin esto, un horario viejo que el usuario ya no recuerda
    /// (p. ej. sembrado por defecto) puede terminar disparándose antes que el que acaba de agregar,
    /// sin que haya forma de notarlo hasta que ya ocurrió.
    /// </summary>
    private void UpdateDailyTimesPreview()
    {
        var times = _dailyTimesList.Items.Cast<string>().ToList();
        if (times.Count == 0)
        {
            _dailyTimesPreviewLabel.Text = "No hay horarios configurados.";
            return;
        }

        var now = DateTime.Now;
        var nextRun = DailyScheduleCalculator.GetNextRun(times, now);
        var nextRunText = nextRun.HasValue
            ? $"Próxima ejecución si guardas ahora: {nextRun.Value:HH:mm:ss} ({(nextRun.Value.Date == now.Date ? "hoy" : "mañana")})"
            : "Próxima ejecución: --";

        _dailyTimesPreviewLabel.Text = $"Horarios activos ({times.Count}): {string.Join(", ", times)}{Environment.NewLine}{nextRunText}";
    }

    private async Task SaveChangesAsync()
    {
        var config = new SchedulerConfig
        {
            ScheduleType = _scheduleTypeCombo.SelectedIndex == 1 ? ScheduleKind.Daily : ScheduleKind.Interval,
            IntervalValue = (int)_intervalValueNumeric.Value,
            IntervalUnit = _intervalUnitCombo.SelectedIndex switch
            {
                1 => IntervalUnit.Minutes,
                2 => IntervalUnit.Hours,
                _ => IntervalUnit.Seconds,
            },
            DailyRunTimes = _dailyTimesList.Items.Cast<string>().ToList(),
            RunOnStartup = _runOnStartupCheck.Checked,
        };

        _saveButton.Enabled = false;
        try
        {
            await _schedulerConfigService.SaveAsync(config);

            // Si el usuario detuvo los procesos para reprogramar, guardar reanuda automáticamente:
            // no debe quedar detenido esperando un clic aparte en "Reanudar".
            var wasStopped = _schedulerStatusService.Current.IsPaused;
            if (wasStopped)
                _schedulerStatusService.Resume();

            _statusMessageLabel.ForeColor = Color.SeaGreen;
            _statusMessageLabel.Text = wasStopped ? "Guardado ✓ (reanudado)" : "Guardado ✓";
        }
        catch (ArgumentException ex)
        {
            _statusMessageLabel.ForeColor = Color.Firebrick;
            _statusMessageLabel.Text = ex.Message;
            _saveButton.Enabled = true;
        }
        catch (Exception ex)
        {
            _statusMessageLabel.ForeColor = Color.Firebrick;
            _statusMessageLabel.Text = $"No se pudo guardar: {ex.Message}";
            _saveButton.Enabled = true;
        }
    }

    private void ToggleStop()
    {
        if (_schedulerStatusService.Current.IsPaused)
            _schedulerStatusService.Resume();
        else
            _schedulerStatusService.StopAll();
    }

    private async Task RunNowAsync()
    {
        if (_schedulerStatusService.Current.IsRunningNow)
            return;

        _runNowButton.Enabled = false;
        var runToken = _schedulerStatusService.BeginRun(CancellationToken.None);
        try
        {
            await _processExecutor.ExecuteAsync(runToken);
        }
        finally
        {
            _schedulerStatusService.MarkRunCompleted(succeeded: !runToken.IsCancellationRequested);
            if (!IsDisposed)
                _runNowButton.Enabled = true;
        }
    }

    private void OnStatusChanged(SchedulerStatus status)
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action<SchedulerStatus>(RefreshStatusLabels), status);
        }
        else
        {
            RefreshStatusLabels(status);
        }
    }

    private void RefreshStatusLabels(SchedulerStatus status)
    {
        if (IsDisposed)
            return;

        if (status.IsPaused)
        {
            _nextRunLabel.Text = "Próxima ejecución: Detenido (guarda los cambios para reanudar)";
        }
        else if (status.NextRunAt is DateTime next)
        {
            var remaining = next - DateTime.Now;
            var remainingText = remaining > TimeSpan.Zero ? FormatTimeSpan(remaining) : "en curso";
            _nextRunLabel.Text = $"Próxima ejecución: {next:HH:mm:ss} (en {remainingText})";
        }
        else
        {
            _nextRunLabel.Text = "Próxima ejecución: --";
        }

        _lastRunLabel.Text = status.LastRunAt is DateTime last
            ? $"Última ejecución: {last:HH:mm:ss}" + (status.LastRunSucceeded == false ? " (no completada)" : "")
            : "Última ejecución: --";

        _runNowButton.Enabled = !status.IsRunningNow;

        _stopButton.Text = status.IsPaused ? "Reanudar procesos" : "Detener procesos";
        _stopButton.BackColor = status.IsPaused ? Color.LightGoldenrodYellow : DefaultBackColor;
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
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
            _schedulerStatusService.StatusChanged -= OnStatusChanged;
            _countdownTimer.Stop();
            _countdownTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
