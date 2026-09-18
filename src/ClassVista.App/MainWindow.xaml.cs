using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClassVista.Camera.Abstractions;
using ClassVista.Camera.Windows;

namespace ClassVista.App;

public partial class MainWindow : Window
{
    private readonly string dataDirectory;
    private readonly List<CameraPane> panes = [];
    private readonly List<CaptureSession> sessions = [];
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
    private CaptureReport? report;
    private bool starting;
    private bool stopping;
    private bool closeRequested;
    private bool allowClose;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? dataDirectory)
    {
        this.dataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassVista");
        InitializeComponent();
        var defaults = new[] { new CameraSettings("simulation:left"), new CameraSettings("simulation:right") };
        try
        {
            var path = Path.Combine(this.dataDirectory, "cameras.json");
            if (File.Exists(path))
            {
                var stored = JsonSerializer.Deserialize<CameraSettings[]>(File.ReadAllText(path));
                if (stored is null || stored.Length != 2 || stored.Any(item => item is null))
                    throw new InvalidDataException("設定檔必須包含兩路攝影機。");
                foreach (var setting in stored)
                    setting.Validate();
                defaults = stored;
            }
        }
        catch (Exception exception)
        {
            RunStatus.Text = $"設定檔讀取失敗，改用模擬來源：{exception.Message}";
        }
        for (var index = 0; index < defaults.Length; index++)
        {
            var pane = new CameraPane(index == 0 ? "左側攝影機" : "右側攝影機", defaults[index]);
            panes.Add(pane);
            Grid.SetColumn(pane.Root, index * 2);
            CameraGrid.Children.Add(pane.Root);
        }
        timer.Tick += Render;
        timer.Start();
    }

    private async void WindowLoaded(object sender, RoutedEventArgs args) => await RefreshDevicesAsync();
    private async void RefreshClicked(object sender, RoutedEventArgs args) => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        RefreshButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        var choices = new List<CameraDevice>
        {
            new("simulation:left", "模擬來源 / Left", -1),
            new("simulation:right", "模擬來源 / Right", -1)
        };
        try
        {
            choices.AddRange(await Task.Run(CameraCatalog.Enumerate));
        }
        catch (Exception exception)
        {
            RunStatus.Text = "無法列舉實體攝影機：" + exception.Message;
        }
        foreach (var pane in panes)
            pane.SetDevices(choices);
        RefreshButton.IsEnabled = true;
        StartButton.IsEnabled = true;
    }

    private async void StartClicked(object sender, RoutedEventArgs args)
    {
        if (sessions.Count != 0 || starting || stopping)
            return;
        starting = true;
        try
        {
            var settings = panes.Select(pane => pane.ReadSettings()).ToArray();
            if (settings.Select(setting => setting.DeviceId).Distinct().Count() != settings.Length)
                throw new InvalidOperationException("兩路不可選擇同一個來源。");
            StartButton.IsEnabled = false;
            RefreshButton.IsEnabled = false;
            foreach (var pane in panes)
            {
                pane.SetRunning(true);
                pane.Clear("連線中");
            }
            Directory.CreateDirectory(dataDirectory);
            var path = Path.Combine(dataDirectory, "cameras.json");
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
            foreach (var setting in settings)
            {
                var simulated = setting.DeviceId.StartsWith("simulation:", StringComparison.Ordinal);
                sessions.Add(new CaptureSession(setting, simulated ? () => new SyntheticCameraSource() : () => new WindowsCameraSource(), simulated));
            }
            var reportDirectory = Path.Combine(dataDirectory, "Reports");
            Directory.CreateDirectory(reportDirectory);
            var reportPath = Path.Combine(reportDirectory, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
            foreach (var session in sessions)
                session.Start();
            report = new CaptureReport(reportPath, sessions.ToArray());
            StopButton.IsEnabled = true;
            RunStatus.Text = $"取像中；診斷報告：{reportPath}";
        }
        catch (Exception exception)
        {
            await StopAsync();
            RunStatus.Text = "啟動失敗：" + exception.Message;
        }
        finally
        {
            starting = false;
        }
        if (closeRequested)
            await StopAsync();
    }

    private void Render(object? sender, EventArgs args)
    {
        for (var index = 0; index < sessions.Count; index++)
        {
            var session = sessions[index];
            var pane = panes[index];
            using var frame = session.Frames.TakeLatest();
            if (frame is not null)
                pane.Present(frame);
            var metrics = session.Metrics.Snapshot(Stopwatch.GetTimestamp());
            if (metrics.FrameAgeMs is > 2000)
                pane.Clear("影像已逾時");
            var manual = !session.IsSimulation && (session.Settings.Exposure is null || session.Settings.WhiteBalance is null || session.Settings.Focus is null)
                ? "；手動鎖定未完整設定" : "";
            pane.UpdateStatus(session.Status + manual,
                $"平均 {metrics.AverageFps:F1} FPS / 近期 {metrics.RecentFps:F1} FPS / 影格 {metrics.Frames:N0}\n" +
                $"讀取 P95 {metrics.ReadP95Ms:F1} ms / 影格年齡 {metrics.FrameAgeMs:F0} ms\n" +
                $"佇列 {session.Frames.Count} / 預覽丟棄 {session.Frames.Dropped} / 失敗 {metrics.ReadFailures} / 重連 {metrics.Reconnects}",
                DiagnosticsToggle.IsChecked == true);
        }
        if (report?.Error is { } error)
            RunStatus.Text = "診斷報告寫入失敗：" + error;
    }

    private async void StopClicked(object sender, RoutedEventArgs args) => await StopAsync();

    private async Task StopAsync()
    {
        if (stopping)
            return;
        stopping = true;
        StopButton.IsEnabled = false;
        RunStatus.Text = "停止中，等待取像工作釋放裝置…";
        try
        {
            foreach (var session in sessions)
                session.RequestStop();
            var shutdown = Task.WhenAll(sessions.Select(session => session.DisposeAsync().AsTask()));
            try
            {
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                RunStatus.Text = "攝影機驅動仍在等待，介面可回應；請拔除裝置。停止完成前無法重新啟動。";
                await shutdown;
            }
            if (report is not null)
            {
                await report.DisposeAsync();
                RunStatus.Text = report.Error is { } error ? "報告寫入失敗：" + error : "已停止；診斷報告已寫入。";
            }
            else
                RunStatus.Text = "已停止。";
        }
        catch (Exception exception)
        {
            RunStatus.Text = "停止時發生錯誤：" + exception.Message;
        }
        finally
        {
            sessions.Clear();
            report = null;
            foreach (var pane in panes)
            {
                pane.SetRunning(false);
                pane.Clear("已停止");
                pane.UpdateStatus("已停止", "", DiagnosticsToggle.IsChecked == true);
            }
            stopping = false;
            StartButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
        }
        if (closeRequested)
        {
            allowClose = true;
            Close();
        }
    }

    private void ReportsClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "Reports");
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            RunStatus.Text = "無法開啟報告資料夾：" + exception.Message;
        }
    }

    private async void WindowClosing(object? sender, CancelEventArgs args)
    {
        if (starting)
        {
            args.Cancel = true;
            closeRequested = true;
            return;
        }
        if (allowClose || (sessions.Count == 0 && !stopping))
        {
            timer.Stop();
            return;
        }
        args.Cancel = true;
        closeRequested = true;
        await StopAsync();
    }
}