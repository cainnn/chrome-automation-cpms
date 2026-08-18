using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ChromeAutomation.CpszzNavigate;

namespace ChromeAutomation.CpmsExport.Ui;

public partial class MainWindow : Window
{
    private readonly WorkflowRunner _runner = new();
    private readonly ErpWorkflowRunner _erpRunner = new();
    private CancellationTokenSource? _cts;
    private AppSettings _settings;
    private DispatcherTimer? _scheduleTimer;
    private DateTime? _nextScheduledRun;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsManager.Load();
        LoadSettingsToUi();

        _runner.Log += OnLog;
        _runner.IsRunningChanged += () => Dispatcher.UIThread.Post(UpdateUiState);
        _erpRunner.Log += OnLog;
        _erpRunner.IsRunningChanged += () => Dispatcher.UIThread.Post(UpdateUiState);

        RunButton.Click += OnRunClick;
        StopButton.Click += OnStopClick;
        ScheduleToggle.IsCheckedChanged += OnScheduleToggled;
        SaveSettingsButton.Click += OnSaveSettings;
        TaskList.SelectionChanged += OnTaskChanged;

        UpdateUiState();
    }

    private void OnLog(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            var line = $"[{time}] {message}\n";

            var text = LogBox.Text ?? "";
            if (text.Length + line.Length > 50000)
                text = text[^30000..];

            LogBox.Text = text + line;
            LogBox.CaretIndex = LogBox.Text.Length;
        });
    }

    private void OnTaskChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selected = TaskList.SelectedIndex;

        CpmsSettingsPanel.IsVisible = selected == 0;
        ErpSettingsPanel.IsVisible = selected == 1;

        Title = selected switch
        {
            0 => "CPMS 项目明细导出",
            1 => "ERP 支出明细导出",
            _ => "自动化工具"
        };
    }

    private int GetScheduleHour()
    {
        if (int.TryParse(ScheduleHourBox.Text, out var h) && h >= 0 && h <= 23) return h;
        return 8;
    }

    private int GetScheduleMinute()
    {
        if (int.TryParse(ScheduleMinuteBox.Text, out var m) && m >= 0 && m <= 59) return m;
        return 0;
    }

    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (_runner.IsRunning || _erpRunner.IsRunning) return;

        var taskIndex = TaskList.SelectedIndex;
        if (taskIndex < 0 || taskIndex > 1)
        {
            OnLog("该任务尚未实现。");
            return;
        }

        ApplySettingsToEnv();
        _cts = new CancellationTokenSource();

        LogBox.Text = "";
        UpdateUiState();

        try
        {
            if (taskIndex == 0)
            {
                if (_settings.SkipExport)
                {
                    await _runner.RunDownloadOnlyAsync(
                        _settings.DownloadListUrl,
                        Environment.GetEnvironmentVariable("CPMS_SERIAL"),
                        _cts.Token);
                }
                else
                {
                    await _runner.RunFullWorkflowAsync(
                        _settings.ReportUrl,
                        _settings.DownloadListUrl,
                        _cts.Token);
                }
            }
            else
            {
                var erpSettings = BuildErpSettings();
                await _erpRunner.RunFullWorkflowAsync(erpSettings, _cts.Token);
            }
            OnLog("全部完成。");
        }
        catch (OperationCanceledException)
        {
            OnLog("已停止。");
        }
        catch (Exception ex)
        {
            OnLog($"错误: {ex.Message}");
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            Dispatcher.UIThread.Post(UpdateUiState);
        }
    }

    private void OnStopClick(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        OnLog("正在停止...");
    }

    private void OnScheduleToggled(object? sender, RoutedEventArgs e)
    {
        _settings.ScheduleEnabled = ScheduleToggle.IsChecked == true;
        SchedulePanel.IsVisible = _settings.ScheduleEnabled;

        if (_settings.ScheduleEnabled)
            StartScheduleTimer();
        else
            StopScheduleTimer();

        SettingsManager.Save(_settings);
        UpdateUiState();
    }

    private void StartScheduleTimer()
    {
        StopScheduleTimer();
        ReadScheduleFromUi();
        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _scheduleTimer.Tick += OnScheduleTimerTick;
        _scheduleTimer.Start();
        CalculateNextRun();
        OnLog($"定时任务已启用: 每天 {_settings.ScheduleHour:D2}:{_settings.ScheduleMinute:D2}");
    }

    private void StopScheduleTimer()
    {
        if (_scheduleTimer != null)
        {
            _scheduleTimer.Stop();
            _scheduleTimer = null;
        }
        _nextScheduledRun = null;
    }

    private void ReadScheduleFromUi()
    {
        _settings.ScheduleHour = GetScheduleHour();
        _settings.ScheduleMinute = GetScheduleMinute();
    }

    private void CalculateNextRun()
    {
        var now = DateTime.Now;
        var todayTarget = new DateTime(now.Year, now.Month, now.Day,
            _settings.ScheduleHour, _settings.ScheduleMinute, 0);
        _nextScheduledRun = now >= todayTarget ? todayTarget.AddDays(1) : todayTarget;
    }

    private async void OnScheduleTimerTick(object? sender, EventArgs e)
    {
        if (_runner.IsRunning || _nextScheduledRun == null) return;
        if (TaskList.SelectedIndex != 0) return;

        ReadScheduleFromUi();
        CalculateNextRun();

        if (DateTime.Now >= _nextScheduledRun)
        {
            OnLog("[定时任务] 到达预定时间，开始执行...");
            _nextScheduledRun = DateTime.Today.AddDays(1)
                .AddHours(_settings.ScheduleHour)
                .AddMinutes(_settings.ScheduleMinute);

            _cts = new CancellationTokenSource();
            ApplySettingsToEnv();

            try
            {
                if (_settings.SkipExport)
                {
                    await _runner.RunDownloadOnlyAsync(
                        _settings.DownloadListUrl, null, _cts.Token);
                }
                else
                {
                    await _runner.RunFullWorkflowAsync(
                        _settings.ReportUrl,
                        _settings.DownloadListUrl,
                        _cts.Token);
                }
                OnLog("[定时任务] 执行完成。");
            }
            catch (OperationCanceledException)
            {
                OnLog("[定时任务] 已停止。");
            }
            catch (Exception ex)
            {
                OnLog($"[定时任务] 错误: {ex.Message}");
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }

            CalculateNextRun();
        }

        UpdateUiState();
    }

    private void UpdateUiState()
    {
        var running = _runner.IsRunning || _erpRunner.IsRunning;
        RunButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        StatusText.Text = running ? "运行中..." : "空闲";
        StatusText.Foreground = running
            ? Avalonia.Media.Brushes.DodgerBlue
            : Avalonia.Media.Brushes.Gray;

        NextRunText.Text = _settings.ScheduleEnabled && _nextScheduledRun.HasValue
            ? $"下次: {_nextScheduledRun.Value:HH:mm}"
            : "";

        ScheduleToggle.IsChecked = _settings.ScheduleEnabled;
        SchedulePanel.IsVisible = _settings.ScheduleEnabled;
    }

    private void LoadSettingsToUi()
    {
        ReportUrlBox.Text = _settings.ReportUrl;
        DownloadUrlBox.Text = _settings.DownloadListUrl;
        ConnectionStringBox.Text = _settings.ConnectionString;
        AsposeLicenseBox.Text = _settings.AsposeLicensePath;
        SkipExportCheck.IsChecked = _settings.SkipExport;
        ForceNewTabCheck.IsChecked = _settings.ForceNewTab;
        ScheduleToggle.IsChecked = _settings.ScheduleEnabled;
        ScheduleHourBox.Text = $"{_settings.ScheduleHour:D2}";
        ScheduleMinuteBox.Text = $"{_settings.ScheduleMinute:D2}";
        ErpPortalUrlBox.Text = _settings.ErpPortalUrl;
        ErpTreeExpandBox.Text = _settings.ErpTreeExpand;
        ErpCuxTextBox.Text = _settings.ErpCuxText;
        ErpSkipImportCheck.IsChecked = _settings.ErpSkipImport;
    }

    private void ApplySettingsToEnv()
    {
        Environment.SetEnvironmentVariable("CPMS_NEW_TAB",
            _settings.ForceNewTab ? "1" : null);
        Environment.SetEnvironmentVariable("CPMS_SKIP_EXPORT",
            _settings.SkipExport ? "1" : null);
        Environment.SetEnvironmentVariable("CPMS_EXPORT_TASK_URL",
            string.IsNullOrWhiteSpace(_settings.DownloadListUrl) ? null : _settings.DownloadListUrl);
        Environment.SetEnvironmentVariable("NET_IMPORT_CONNECTION",
            string.IsNullOrWhiteSpace(_settings.ConnectionString) ? null : _settings.ConnectionString);
        Environment.SetEnvironmentVariable("ASPOSE_LICENSE_PATH",
            string.IsNullOrWhiteSpace(_settings.AsposeLicensePath) ? null : _settings.AsposeLicensePath);

        Environment.SetEnvironmentVariable("ERP_PORTAL_URL",
            string.IsNullOrWhiteSpace(_settings.ErpPortalUrl) ? null : _settings.ErpPortalUrl);
        Environment.SetEnvironmentVariable("ERP_TREE_EXPAND",
            string.IsNullOrWhiteSpace(_settings.ErpTreeExpand) ? null : _settings.ErpTreeExpand);
        Environment.SetEnvironmentVariable("ERP_CUX_TEXT",
            string.IsNullOrWhiteSpace(_settings.ErpCuxText) ? null : _settings.ErpCuxText);
        Environment.SetEnvironmentVariable("ERP_SKIP_IMPORT",
            _settings.ErpSkipImport ? "1" : null);
    }

    private ErpSettings BuildErpSettings() => new()
    {
        PortalUrl = string.IsNullOrWhiteSpace(_settings.ErpPortalUrl)
            ? ErpSettings.FromEnvironment().PortalUrl
            : _settings.ErpPortalUrl,
        TreeExpandText = string.IsNullOrWhiteSpace(_settings.ErpTreeExpand)
            ? ErpSettings.FromEnvironment().TreeExpandText
            : _settings.ErpTreeExpand,
        CuxLinkText = string.IsNullOrWhiteSpace(_settings.ErpCuxText)
            ? ErpSettings.FromEnvironment().CuxLinkText
            : _settings.ErpCuxText,
        SkipImport = _settings.ErpSkipImport
    };

    private void OnSaveSettings(object? sender, RoutedEventArgs e)
    {
        _settings.ReportUrl = ReportUrlBox.Text ?? "";
        _settings.DownloadListUrl = DownloadUrlBox.Text ?? "";
        _settings.ConnectionString = ConnectionStringBox.Text ?? "";
        _settings.AsposeLicensePath = AsposeLicenseBox.Text ?? "";
        _settings.SkipExport = SkipExportCheck.IsChecked == true;
        _settings.ForceNewTab = ForceNewTabCheck.IsChecked == true;
        _settings.ErpPortalUrl = ErpPortalUrlBox.Text ?? "";
        _settings.ErpTreeExpand = ErpTreeExpandBox.Text ?? "";
        _settings.ErpCuxText = ErpCuxTextBox.Text ?? "";
        _settings.ErpSkipImport = ErpSkipImportCheck.IsChecked == true;
        ReadScheduleFromUi();

        SettingsManager.Save(_settings);

        if (_settings.ScheduleEnabled)
            CalculateNextRun();

        OnLog("设置已保存。");
        UpdateUiState();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _cts?.Cancel();
        StopScheduleTimer();
        SettingsManager.Save(_settings);
        base.OnClosing(e);
    }
}
