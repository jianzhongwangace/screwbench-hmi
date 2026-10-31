using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace ScrewBench.Desktop;

public sealed class MechanismView : Grid, IDisposable
{
    private readonly WebView2 browser = new();
    private readonly TextBlock message = new() { Text = "正在初始化三维视图…", Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap };
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string pending = "{}";
    private bool initializing, loaded, disposed;
    private bool externalInput;
    private readonly System.Windows.Threading.DispatcherTimer demoTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly System.Diagnostics.Stopwatch demoClock = new();
    private MotionDemo? demo;
    private long demoSequence;
    private string demoSession = "";
    private MotionSnapshot? demoLast;
    private readonly Button demoPause = new() { Content = "暂停演示", IsEnabled = false, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button demoStop = new() { Content = "停止演示", IsEnabled = false, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
    private string simulatedFrame = "{}";
    private readonly TextBlock inputStatus = new() { Text = "跟随模拟采样", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) };
    public Task Ready => ready.Task;

    public MechanismView()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        var toolbar = new WrapPanel { Margin = new Thickness(8, 4, 8, 4) };
        var import = new Button { Content = "导入位置 JSON", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        var resume = new Button { Content = "恢复模拟显示", Padding = new Thickness(12, 6, 12, 6) };
        import.Click += (_, _) => ImportFile();
        resume.Click += (_, _) => ResumeSimulation();
        var playDemo = new Button { Content = "播放演示 JSON", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
        playDemo.Click += (_, _) =>
        {
            try { StartDemo(); }
            catch (Exception e) { MessageBox.Show(e.Message, "无法播放演示"); }
        };
        demoPause.Click += (_, _) => PauseDemo(); demoStop.Click += (_, _) => StopDemo();
        demoTimer.Tick += (_, _) => DemoTick();
        toolbar.Children.Add(playDemo); toolbar.Children.Add(demoPause); toolbar.Children.Add(demoStop);
        toolbar.Children.Add(import); toolbar.Children.Add(resume); toolbar.Children.Add(inputStatus);
        Children.Add(toolbar); SetRow(browser, 1); SetRow(message, 1);
        Children.Add(browser); Children.Add(message);
        Loaded += async (_, _) => await InitializeAsync();
    }

    private void ImportFile()
    {
        var dialog = new OpenFileDialog { Filter = "位置快照 (*.json)|*.json", Title = "导入三维位置（仅显示，不控制设备）" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 65536) throw new ArgumentException("位置快照不能超过64KB。");
            ImportPositionJson(File.ReadAllText(dialog.FileName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        { MessageBox.Show(e.Message, "位置导入失败，保留原显示", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    public void ImportPositionJson(string json)
    {
        var sample = MotionSnapshot.Parse(json); StopDemo(); DisplayPosition(sample, State.Stopped, "IMPORTED");
    }

    public void StartDemo()
    {
        var data = MotionDemo.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "motion-demo.json")));
        StopDemo(); demo = data; demoSequence = 0; demoSession = "DEMO-" + Guid.NewGuid().ToString("N");
        demoPause.Content = "暂停演示"; demoPause.IsEnabled = demoStop.IsEnabled = true;
        demoClock.Restart(); DemoTick(); demoTimer.Start();
    }
    private void DemoTick()
    {
        if (disposed || demo is null) return;
        var (position, angle, rpm, finished) = demo.At(demoClock.Elapsed.TotalSeconds);
        double force = -demo.LoadN * Math.Tanh(rpm / 30);
        double torque = -force * demo.LeadMm / (2000 * Math.PI * demo.Efficiency);
        demoLast = new MotionSnapshot { Version = 1, Session = demoSession, Sequence = ++demoSequence,
            Time = demoClock.Elapsed.TotalSeconds, Position = position, Angle = angle, Rpm = rpm, Force = force, Torque = torque, Valid = true };
        DisplayPosition(demoLast, State.Running, "DEMO");
        if (finished) StopDemo();
    }
    public void PauseDemo()
    {
        if (demo is null) return;
        if (demoTimer.IsEnabled)
        {
            demoTimer.Stop(); demoClock.Stop(); demoPause.Content = "继续演示";
            if (demoLast is not null) DisplayPosition(demoLast, State.Paused, "DEMO");
        }
        else { demoClock.Start(); demoPause.Content = "暂停演示"; demoTimer.Start(); }
    }
    public void StopDemo()
    {
        demoTimer.Stop(); demoClock.Stop();
        if (demo is not null && demoLast is not null && !disposed) DisplayPosition(demoLast, State.Stopped, "DEMO");
        demo = null; demoPause.IsEnabled = demoStop.IsEnabled = false; demoPause.Content = "暂停演示";
    }

    // Call from the future acquisition adapter, with a device session ID and monotonically increasing sample sequence.
    public void UpdateMeasured(MotionSnapshot sample, State state)
    {
        sample.Validate();
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => UpdateMeasured(sample, state)); return; }
        StopDemo(); DisplayPosition(sample, state, "MEASURED");
    }

    private void DisplayPosition(MotionSnapshot sample, State state, string source)
    {
        sample.Validate();
        if (!Enum.IsDefined(state)) throw new ArgumentException("未知设备状态。");
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => DisplayPosition(sample, state, source)); return; }
        if (disposed) return;
        externalInput = true;
        inputStatus.Text = source == "DEMO" ? "JSON演示 · 编造数据 · 无硬件输出" : source == "IMPORTED" ? "文件快照 · 非实时 · 仅三维显示" : "外部实测输入 · 仅三维显示";
        pending = JsonSerializer.Serialize(new { type = "frame", source, session = sample.Session, state = state.ToString(),
            sample = new { sequence = sample.Sequence, time = sample.Time, position = sample.Position, angle = sample.Angle,
                rpm = sample.Rpm, force = sample.Force, torque = sample.Torque, valid = sample.Valid } });
        SendPending();
    }

    public void ResumeSimulation()
    {
        StopDemo();
        externalInput = false; inputStatus.Text = "跟随模拟采样"; pending = simulatedFrame; SendPending();
    }

    private async Task InitializeAsync()
    {
        if (initializing || disposed) return;
        initializing = true;
        try
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "Scene");
            if (!File.Exists(Path.Combine(folder, "bundle.js"))) throw new FileNotFoundException("缺少三维资源 bundle.js，请重新构建三维资源。");
            var environment = await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScrewBench", "WebView2"));
            if (disposed) return;
            await browser.EnsureCoreWebView2Async(environment);
            if (disposed) return;
            var core = browser.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.SetVirtualHostNameToFolderMapping("screwbench.local", folder, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "screwbench.local") e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.ProcessFailed += (_, _) => ShowFailure("三维渲染进程已中断，请重启软件。采集数据不受三维窗口控制。");
            core.WebMessageReceived += (_, e) =>
            {
                if (e.Source != "https://screwbench.local/index.html") return;
                try
                {
                    using var data = JsonDocument.Parse(e.WebMessageAsJson);
                    if (data.RootElement.GetProperty("type").GetString() != "ready") return;
                    loaded = true; message.Visibility = Visibility.Collapsed;
                    core.PostWebMessageAsJson(pending); ready.TrySetResult();
                }
                catch (JsonException) { }
                catch (KeyNotFoundException) { }
            };
            core.NavigationCompleted += (_, e) => { if (!e.IsSuccess) ShowFailure("三维页面加载失败：" + e.WebErrorStatus); };
            core.Navigate("https://screwbench.local/index.html");
        }
        catch (Exception exception)
        {
            ShowFailure("无法启动三维视图：" + exception.Message + "\n请检查 Microsoft Edge WebView2 Runtime。原有测试页面仍可使用。");
            ready.TrySetException(exception);
        }
    }

    private void ShowFailure(string text)
    {
        loaded = false; message.Text = text; message.Visibility = Visibility.Visible;
    }

    public void Update(Sample? sample, State state, string? session)
    {
        if (disposed) return;
        bool finite = sample is not null && double.IsFinite(sample.Position) && double.IsFinite(sample.Angle)
            && double.IsFinite(sample.Time) && double.IsFinite(sample.Rpm) && double.IsFinite(sample.Force) && double.IsFinite(sample.Torque);
        simulatedFrame = JsonSerializer.Serialize(new
        {
            type = "frame", source = "SIMULATED", session = session ?? "idle", state = state.ToString(),
            sample = sample is null ? null : new
            {
                sequence = sample.Sequence, time = finite ? sample.Time : 0,
                position = finite ? sample.Position : 0, angle = finite ? sample.Angle : 0,
                rpm = finite ? sample.Rpm : 0, force = finite ? sample.Force : 0,
                torque = finite ? sample.Torque : 0, valid = finite && sample.Valid
            }
        });
        if (externalInput) return;
        pending = simulatedFrame; SendPending();
    }

    private void SendPending()
    {
        if (!loaded || disposed) return;
        try { browser.CoreWebView2.PostWebMessageAsJson(pending); }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        { ShowFailure("三维数据更新中断：" + exception.Message); }
    }

    public async Task<string> InspectAsync() => await browser.CoreWebView2.ExecuteScriptAsync("window.screwBench.inspect()");
    public async Task CaptureAsync(string path)
    {
        using var stream = File.Create(path);
        await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
    }
    public void Dispose() { disposed = true; demoTimer.Stop(); demoClock.Stop(); browser.Dispose(); }
}
