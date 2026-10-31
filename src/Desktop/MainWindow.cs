using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ScrewBench.Desktop;

public sealed class MainWindow : Window
{
    private static readonly Brush Ink = Color("#253337"), Muted = Color("#6E8086"), Accent = Color("#007E72");
    private readonly Dictionary<string, TextBox> fields = new();
    private readonly ComboBox mode = new(), signal = new();
    private readonly TextBlock connection = Text("未连接", 13), runStatus = Text("等待连接", 14), telemetry = Text("转速 -- rpm  ·  线速度 -- mm/s", 12), cycle = Text("循环 0  ·  0.0 s", 12), error = Text("", 12);
    private readonly TextBlock[] values = Enumerable.Range(0, 4).Select(_ => Text("--", 25)).ToArray();
    private readonly StackPanel recipePanel = new();
    private readonly TrendChart chart = new();
    private readonly MechanismView mechanism = new();
    private TabControl tabs = null!;
    private string? sceneSession;
    private readonly DataGrid results = GridView(("参数", "Parameter", 165), ("结果", "Value", 130), ("单位", "Unit", 80), ("计算说明", "Note", 490));
    private readonly DataGrid history = GridView(("测试编号", "Id", 235), ("样件", "Recipe.Specimen", 150), ("模式", "Recipe.Mode", 115), ("开始时间", "Started", 240), ("来源", "Source", 100), ("停止原因", "StopReason", 210));
    private readonly TextBox search = new(), historyDetail = new();
    private readonly ListBox logs = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Stopwatch elapsed = new();
    private readonly List<Sample> samples = new();
    private Button connect = null!, start = null!, pause = null!, stop = null!, fault = null!, reset = null!;
    private State state;
    private Simulator? simulator;
    private RunStore? store;
    private Recipe activeRecipe = Recipe.Default;
    private DateTimeOffset started;
    private double simulatedUntil;
    private readonly bool smoke;
    private readonly string qaRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScrewBench", "qa");

    public MainWindow(bool smoke, bool demo = false, bool demoCheck = false)
    {
        this.smoke = smoke;
        Title = "丝杠螺母综合测试 · ScrewBench";
        Width = 1440; Height = 920; MinWidth = 1040; MinHeight = 720;
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13; Foreground = Ink;
        Background = Color("#F2F5F5"); WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = Build();
        timer.Tick += Tick;
        Closing += OnClosing;
        RefreshHistory(); UpdateState(State.Disconnected);
        Log("软件启动；当前仅模拟设备，无真实硬件运动输出。");
        if (smoke) Loaded += async (_, _) => await SmokeAsync();
        else if (demo || demoCheck) Loaded += async (_, _) =>
        {
            try
            {
                tabs.SelectedIndex = 1;
                await mechanism.Ready.WaitAsync(TimeSpan.FromSeconds(40));
                mechanism.StartDemo();
                Title = "丝杠螺母综合测试 · JSON演示（非实测）";
                if (!demoCheck) return;
                Directory.CreateDirectory(qaRoot);
                await Task.Delay(800);
                using var first = JsonDocument.Parse(await mechanism.InspectAsync());
                await mechanism.CaptureAsync(Path.Combine(qaRoot, "demo-first.png"));
                await Task.Delay(1500);
                using var second = JsonDocument.Parse(await mechanism.InspectAsync());
                await mechanism.CaptureAsync(Path.Combine(qaRoot, "demo-second.png"));
                if (Math.Abs(first.RootElement.GetProperty("carriageX").GetDouble() - second.RootElement.GetProperty("carriageX").GetDouble()) < 5
                    || second.RootElement.GetProperty("source").GetString() != "DEMO") throw new Exception("JSON演示未产生位移变化");
                mechanism.PauseDemo(); await Task.Delay(200);
                using var paused = JsonDocument.Parse(await mechanism.InspectAsync());
                await Task.Delay(400);
                using var held = JsonDocument.Parse(await mechanism.InspectAsync());
                if (paused.RootElement.GetProperty("carriageX").GetDouble() != held.RootElement.GetProperty("carriageX").GetDouble()) throw new Exception("演示暂停未保持位置");
                Capture("demo-window.png");
                mechanism.PauseDemo(); await Task.Delay(800);
                using var resumed = JsonDocument.Parse(await mechanism.InspectAsync());
                if (resumed.RootElement.GetProperty("carriageX").GetDouble() == held.RootElement.GetProperty("carriageX").GetDouble()) throw new Exception("演示继续失败");
                mechanism.StopDemo();
                File.WriteAllText(Path.Combine(qaRoot, "demo-check.txt"), "PASS: JSON playback / moving reader and carriage / DEMO source / pause / resume / stop");
                Close();
            }
            catch (Exception e)
            {
                if (demoCheck) { Directory.CreateDirectory(qaRoot); File.WriteAllText(Path.Combine(qaRoot, "demo-check.txt"), e.ToString()); Environment.ExitCode = 1; Close(); }
                else MessageBox.Show(e.Message, "演示启动失败");
            }
        };
    }
    private static Brush Color(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;
    private static TextBlock Text(string text, double size = 13) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center };
    private static Button Button(string label, RoutedEventHandler action, bool primary = false)
    {
        var button = new Button { Content = label, Padding = new Thickness(13, 8, 13, 8), Margin = new Thickness(0, 0, 8, 0), MinHeight = 36,
            Background = primary ? Accent : Brushes.White, Foreground = primary ? Brushes.White : Ink, BorderBrush = Color("#CBD6D7"), Cursor = System.Windows.Input.Cursors.Hand };
        button.Click += action;
        return button;
    }
    private static Button IconButton(string glyph, string tooltip, RoutedEventHandler action)
    {
        var button = Button(glyph, action);
        button.FontFamily = new FontFamily("Segoe MDL2 Assets"); button.ToolTip = tooltip;
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        return button;
    }
    private static DataGrid GridView(params (string title, string path, double width)[] columns)
    {
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, RowHeight = 38, ColumnHeaderHeight = 36,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = Color("#E4EAEA"), BorderThickness = new Thickness(0), Background = Brushes.White, SelectionMode = DataGridSelectionMode.Single };
        foreach (var column in columns) grid.Columns.Add(new DataGridTextColumn { Header = column.title, Binding = new Binding(column.path), Width = column.width });
        return grid;
    }
    private static Border Surface(UIElement child, double padding = 16) => new() { Background = Brushes.White, Padding = new Thickness(padding), Child = child };
    private static Grid Rows(params GridLength[] heights)
    {
        var grid = new Grid();
        foreach (var height in heights) grid.RowDefinitions.Add(new RowDefinition { Height = height });
        return grid;
    }
    private static void Place(Grid grid, UIElement child, int row, int column = 0)
    {
        Grid.SetRow(child, row); Grid.SetColumn(child, column); grid.Children.Add(child);
    }
    private UIElement Build()
    {
        var root = Rows(new GridLength(90), new GridLength(1, GridUnitType.Star), new GridLength(32));
        var header = new DockPanel();
        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        headerActions.Children.Add(new Border { Background = Color("#FFF1C8"), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 18, 0), Child = Text("模拟设备 · 非实测数据") });
        connection.Margin = new Thickness(0, 0, 16, 0); headerActions.Children.Add(connection);
        connect = Button("连接模拟设备", Connect); headerActions.Children.Add(connect);
        var inspector = Button("数据包调试", OpenInspector); inspector.ToolTip = "打开独立窗口：监听 / 连接 TCP，查看原始字节并按布局解析";
        headerActions.Children.Add(inspector);
        DockPanel.SetDock(headerActions, Dock.Right); header.Children.Add(headerActions);
        var branding = new StackPanel(); branding.Children.Add(Text("丝杠螺母综合测试", 22));
        var subtitle = Text("SCREWBENCH  /  第一阶段测试工作站", 11); subtitle.Foreground = Muted; subtitle.Margin = new Thickness(0, 5, 0, 0); branding.Children.Add(subtitle);
        header.Children.Add(branding); Place(root, Surface(header, 20), 0);
        tabs = new TabControl { Margin = new Thickness(20, 14, 20, 12), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        foreach (var tab in new[] { ("测试工作台", Workbench()), ("三维联动", (UIElement)mechanism), ("历史记录", HistoryPage()), ("设备与标定", DevicePage()), ("操作日志", (UIElement)logs) })
            tabs.Items.Add(new TabItem { Header = tab.Item1, Padding = new Thickness(22, 10, 22, 10), Content = tab.Item2 });
        Place(root, tabs, 1);
        var footer = new DockPanel();
        var storage = Text("数据：LocalAppData / ScrewBench / runs", 11); DockPanel.SetDock(storage, Dock.Right); footer.Children.Add(storage);
        footer.Children.Add(Text("SIMULATION · 无硬件运动输出 · v0.1", 11));
        Place(root, new Border { Background = Color("#E6ECEC"), Padding = new Thickness(20, 5, 20, 5), Child = footer }, 2);
        return root;
    }
    private UIElement Workbench()
    {
        var content = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(292) });
        var left = Rows(new GridLength(42), new GridLength(108), new GridLength(1, GridUnitType.Star), new GridLength(136)); left.Margin = new Thickness(0, 0, 16, 0);
        var heading = new DockPanel(); DockPanel.SetDock(runStatus, Dock.Right); runStatus.Foreground = Accent; heading.Children.Add(runStatus); heading.Children.Add(Text("实时监控", 18)); Place(left, heading, 0);
        var cards = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4 };
        string[] titles = ["输入转矩 / N·m", "轴向力 / N", "螺母位置 / mm", "稳态效率 / %"];
        for (int index = 0; index < 4; index++)
        {
            var stack = new StackPanel(); var label = Text(titles[index], 12); label.Foreground = Muted; stack.Children.Add(label);
            values[index].Margin = new Thickness(0, 10, 0, 0); stack.Children.Add(values[index]);
            var panel = Surface(stack, 14); panel.Margin = new Thickness(0, 0, index == 3 ? 0 : 8, 12);
            if (index == 3) { panel.Background = Color("#E0F1EA"); values[index].Foreground = Accent; }
            cards.Children.Add(panel);
        }
        Place(left, cards, 1);
        var plot = Rows(new GridLength(38), new GridLength(1, GridUnitType.Star), new GridLength(32));
        var plotHeader = new DockPanel();
        signal.Width = 155; signal.Padding = new Thickness(6); signal.ItemsSource = new[] { "输入转矩 / N·m", "轴向力 / N", "螺母位置 / mm", "转速 / rpm", "稳态效率 / %" }; signal.SelectedIndex = 0;
        signal.SelectionChanged += (_, _) => UpdateChart(); DockPanel.SetDock(signal, Dock.Right); plotHeader.Children.Add(signal); plotHeader.Children.Add(Text("采样趋势", 15));
        Place(plot, plotHeader, 0); Place(plot, chart, 1);
        var plotFooter = new DockPanel(); DockPanel.SetDock(cycle, Dock.Right); plotFooter.Children.Add(cycle); plotFooter.Children.Add(telemetry); telemetry.Foreground = Muted; cycle.Foreground = Muted;
        Place(plot, plotFooter, 2); Place(left, Surface(plot), 2);
        var resultPanel = Rows(new GridLength(36), new GridLength(1, GridUnitType.Star)); resultPanel.Margin = new Thickness(0, 12, 0, 0);
        var resultHeading = new DockPanel(); var disclaimer = Text("模拟结果不作合格判定", 12); disclaimer.Foreground = Color("#936F18"); DockPanel.SetDock(disclaimer, Dock.Right); resultHeading.Children.Add(disclaimer); resultHeading.Children.Add(Text("测试结果", 16));
        Place(resultPanel, resultHeading, 0); Place(resultPanel, results, 1); Place(left, resultPanel, 3); Place(content, left, 0);
        var right = new StackPanel(); right.Children.Add(Text("测试配方", 17));
        mode.ItemsSource = new[] { "传动效率", "游隙 / 换向残差", "轴向等效刚度", "往复疲劳" }; mode.SelectedIndex = 0; mode.Padding = new Thickness(7); mode.Margin = new Thickness(0, 5, 0, 12);
        recipePanel.Children.Add(Text("测试模式")); recipePanel.Children.Add(mode); recipePanel.Margin = new Thickness(0, 14, 0, 0);
        AddField(recipePanel, "Specimen", "样件编号", "DEMO-001"); AddField(recipePanel, "Operator", "操作人员", "操作员");
        AddPair("Lead", "导程 / mm", "2", "Rpm", "转速 / rpm", "300");
        AddPair("Force", "轴向载荷 / N", "100", "Stroke", "行程 / mm", "20");
        AddPair("Seconds", "定时时长 / s", "30", "Cycles", "疲劳目标 / 次", "10");
        var recipeActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        recipeActions.Children.Add(IconButton("\uE8E5", "导入配方", ImportRecipe)); recipeActions.Children.Add(IconButton("\uE74E", "保存配方", SaveRecipe)); recipePanel.Children.Add(recipeActions); right.Children.Add(recipePanel);
        start = Button("开始测试", Start, true); start.Margin = new Thickness(0, 0, 0, 10); right.Children.Add(start);
        var commands = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 }; pause = Button("暂停", Pause); stop = Button("停止", Stop); stop.Margin = new Thickness(0); commands.Children.Add(pause); commands.Children.Add(stop); right.Children.Add(commands);
        fault = Button("模拟故障停机", Fault); fault.Foreground = Color("#B53D37"); fault.Margin = new Thickness(0, 12, 0, 8); right.Children.Add(fault);
        reset = Button("复位模拟故障", Reset); reset.Margin = new Thickness(0); right.Children.Add(reset); error.Foreground = Color("#B53D37"); error.Margin = new Thickness(0, 12, 0, 0); right.Children.Add(error);
        Place(content, Surface(new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }), 0, 1);
        return content;
    }
    private void AddField(Panel parent, string key, string label, string value)
    {
        parent.Children.Add(Text(label, 12));
        var field = new TextBox { Text = value, Padding = new Thickness(7, 6, 7, 6), Margin = new Thickness(0, 5, 0, 12), BorderBrush = Color("#CBD6D7") };
        System.Windows.Automation.AutomationProperties.SetName(field, label);
        fields[key] = field; parent.Children.Add(field);
    }
    private void AddPair(string first, string firstLabel, string firstValue, string second, string secondLabel, string secondValue)
    {
        var pair = new Grid(); pair.ColumnDefinitions.Add(new ColumnDefinition()); pair.ColumnDefinitions.Add(new ColumnDefinition());
        var firstPanel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) }; var secondPanel = new StackPanel();
        AddField(firstPanel, first, firstLabel, firstValue); AddField(secondPanel, second, secondLabel, secondValue);
        Place(pair, firstPanel, 0); Place(pair, secondPanel, 0, 1); recipePanel.Children.Add(pair);
    }
    private UIElement HistoryPage()
    {
        var page = Rows(new GridLength(52), new GridLength(1, GridUnitType.Star), new GridLength(130)); page.Margin = new Thickness(0, 16, 0, 0);
        var toolbar = new DockPanel(); var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(IconButton("\uE72C", "刷新历史记录", (_, _) => Guard(RefreshHistory)));
        actions.Children.Add(Button("导出 CSV", ExportCsv)); actions.Children.Add(Button("导出报告", ExportReport)); actions.Children.Add(IconButton("\uE8B7", "打开数据目录", (_, _) => Guard(() => { Directory.CreateDirectory(RunStore.Root); Process.Start(new ProcessStartInfo(RunStore.Root) { UseShellExecute = true }); })));
        DockPanel.SetDock(actions, Dock.Right); toolbar.Children.Add(actions);
        search.Width = 240; search.HorizontalAlignment = HorizontalAlignment.Left; search.Padding = new Thickness(8); search.Margin = new Thickness(0, 0, 0, 10); search.ToolTip = "按样件编号或测试编号搜索";
        search.TextChanged += (_, _) => Guard(RefreshHistory); toolbar.Children.Add(search); Place(page, toolbar, 0); Place(page, history, 1);
        historyDetail.IsReadOnly = true; historyDetail.TextWrapping = TextWrapping.Wrap; historyDetail.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; historyDetail.Margin = new Thickness(0, 12, 0, 0); historyDetail.Padding = new Thickness(12);
        history.SelectionChanged += (_, _) => { if (history.SelectedItem is Record record) historyDetail.Text = string.Join(Environment.NewLine, record.Results.Select(result => $"{result.Parameter}：{result.Value} {result.Unit}  |  {result.Note}")); };
        Place(page, historyDetail, 2); return page;
    }
    private UIElement DevicePage()
    {
        var page = new StackPanel { Margin = new Thickness(24), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var item in new[] {
            ("设备接入状态", 20), ("当前数据源：内置模拟器；真实设备控制未启用。", 14),
            ("STM32 协议、传感器量程、标定系数和安全联锁需与硬件团队确认后接入。当前版本不会向网络、串口或驱动器发送运动命令。", 13),
            ("通道与基准", 17), ("轴向力 / N · 输入转矩 / N·m · 转速 / rpm · 角度 / rad · 床身基准位移 / mm", 13),
            ("模拟采样：20 Hz；图表显示最近 60 秒。标定状态：未标定 · 模拟通道。", 13),
            ("安全边界", 17), ("实际急停、STO、硬限位与超力保护由硬件安全回路和底层控制器执行。上位机按钮不能替代硬件急停；模拟故障按钮仅验证软件状态与数据记录。", 13),
            ("当前版本范围", 17), ("效率、刚度、换向残差和疲劳循环为流程验证；未实现真实标定、硬件通信、自动性能失效判定、B+/B− 游隙验收算法及数字孪生。", 13) })
        { var text = Text(item.Item1, item.Item2); text.Margin = new Thickness(0, 0, 0, 18); page.Children.Add(text); }
        return new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private Recipe ReadRecipe()
    {
        double Number(string key) => double.Parse(fields[key].Text, CultureInfo.CurrentCulture);
        var recipe = new Recipe(fields["Specimen"].Text.Trim(), fields["Operator"].Text.Trim(), (Mode)mode.SelectedIndex, Number("Lead"), Number("Rpm"), Number("Force"), Number("Stroke"), int.Parse(fields["Seconds"].Text), int.Parse(fields["Cycles"].Text));
        if (recipe.Validate() is { Length: > 0 } message) throw new ArgumentException(message);
        return recipe;
    }
    private void Guard(Action action)
    {
        try { error.Text = ""; action(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or JsonException or Win32Exception)
        { error.Text = exception.Message; Log("错误：" + exception.Message); }
    }
    private void OpenInspector(object? sender, RoutedEventArgs args)
    {
        var window = new PacketInspectorWindow { Owner = this };
        window.Show();
        Log("已打开数据包调试窗口（独立窗口，不参与实时控制，不写入试验记录）。");
    }
    private void Connect(object? sender, RoutedEventArgs args) => Guard(() =>
    {
        if (state is State.Running or State.Paused or State.Fault) return;
        UpdateState(state == State.Disconnected ? State.Ready : State.Disconnected);
        Log(state == State.Ready ? "模拟设备已连接。" : "模拟设备已断开。");
    });
    private void Start(object? sender, RoutedEventArgs args) => Guard(() =>
    {
        if (state is State.Disconnected or State.Running or State.Paused or State.Fault) return;
        activeRecipe = ReadRecipe();
        simulator = new Simulator(activeRecipe); samples.Clear(); results.ItemsSource = null; chart.Set([]);
        store?.Dispose(); store = new RunStore(); started = DateTimeOffset.Now;
        store.Save(new(store.Id, started, activeRecipe, "SIMULATED", "运行中 / 未完成", 0, []));
        sceneSession = store.Id;
        simulatedUntil = 0; elapsed.Restart(); UpdateState(State.Running); timer.Start(); Log($"启动 {store.Id} / {activeRecipe.Mode} / {activeRecipe.Specimen}");
    });
    private void Tick(object? sender, EventArgs args)
    {
        if (state != State.Running || simulator is null || store is null) return;
        try
        {
            double target = elapsed.Elapsed.TotalSeconds;
            if (target - simulatedUntil > 2) { Finish("界面调度超时，模拟数据不再连续", State.Fault); return; }
            while (simulatedUntil + 0.05 <= target && state == State.Running)
            {
                var sample = simulator.Next(0.05); simulatedUntil += 0.05; samples.Add(sample); store.Append(sample);
                if (samples.Count % 100 == 0) store.Save(new(store.Id, started, activeRecipe, "SIMULATED", "运行中 / 未完成", samples.Count, []));
                if ((activeRecipe.Mode == Mode.Fatigue && sample.Cycles >= activeRecipe.Cycles) || (activeRecipe.Mode != Mode.Fatigue && sample.Time >= activeRecipe.Seconds)) Finish("达到设定目标（非失效寿命）", State.Completed);
            }
            if (samples.Count > 0) UpdateTelemetry(samples[^1]);
            UpdateChart();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            timer.Stop(); elapsed.Stop(); UpdateState(State.Fault); error.Text = "数据写入失败，模拟测试已停止：" + exception.Message; Log(error.Text);
            try { store?.Dispose(); } catch (IOException) { } store = null;
        }
    }
    private void UpdateTelemetry(Sample sample)
    {
        mechanism.Update(sample, state, sceneSession);
        values[0].Text = sample.Torque.ToString("F4"); values[1].Text = sample.Force.ToString("F1"); values[2].Text = sample.Position.ToString("F4"); values[3].Text = Metrics.Efficiency(sample)?.ToString("F2") ?? "--";
        telemetry.Text = $"转速 {sample.Rpm:F0} rpm  ·  线速度 {sample.Velocity:F2} mm/s";
        cycle.Text = $"循环 {sample.Cycles}  ·  {sample.Time:F1} s";
    }
    private void UpdateChart()
    {
        var points = samples.TakeLast(1200).Select(sample => (sample.Time, Value: signal.SelectedIndex switch { 1 => sample.Force, 2 => sample.Position, 3 => sample.Rpm, 4 => Metrics.Efficiency(sample) ?? double.NaN, _ => sample.Torque })).ToArray();
        chart.Set(points);
    }
    private void Pause(object? sender, RoutedEventArgs args)
    {
        if (state == State.Running) { timer.Stop(); elapsed.Stop(); UpdateState(State.Paused); Log("暂停测试。"); }
        else if (state == State.Paused) { elapsed.Start(); UpdateState(State.Running); timer.Start(); Log("继续测试。"); }
    }
    private void Stop(object? sender, RoutedEventArgs args) => Guard(() => { if (state is State.Running or State.Paused) Finish("操作人员停止", State.Stopped); });
    private void Fault(object? sender, RoutedEventArgs args) => Guard(() => { if (state is State.Running or State.Paused) Finish("模拟超力故障", State.Fault); });
    private void Reset(object? sender, RoutedEventArgs args) { if (state == State.Fault) { UpdateState(State.Ready); error.Text = ""; Log("模拟故障已人工复位。"); } }
    private void Finish(string reason, State finalState)
    {
        timer.Stop(); elapsed.Stop(); UpdateState(finalState);
        var calculated = Metrics.Calculate(activeRecipe, samples); results.ItemsSource = calculated;
        if (store is not null)
        {
            try { store.Save(new(store.Id, started, activeRecipe, "SIMULATED", reason, samples.Count, calculated)); }
            finally { store.Dispose(); store = null; }
        }
        Log(reason); RefreshHistory();
    }
    private void UpdateState(State next)
    {
        state = next;
        mechanism.Update(samples.Count > 0 ? samples[^1] : null, state, sceneSession);
        runStatus.Text = next switch { State.Disconnected => "等待连接", State.Ready => "就绪", State.Running => "测试运行中", State.Paused => "已暂停", State.Completed => "测试完成", State.Stopped => "已停止", _ => "故障已锁存" };
        connection.Text = next == State.Disconnected ? "未连接" : "模拟设备在线";
        connect.Content = next == State.Disconnected ? "连接模拟设备" : "断开连接";
        bool busy = next is State.Running or State.Paused;
        connect.IsEnabled = !busy && next != State.Fault; start.IsEnabled = next is State.Ready or State.Completed or State.Stopped;
        pause.IsEnabled = busy; pause.Content = next == State.Paused ? "继续" : "暂停"; stop.IsEnabled = busy; fault.IsEnabled = busy; reset.IsEnabled = next == State.Fault; recipePanel.IsEnabled = !busy;
    }
    private void Log(string message)
    {
        string line = $"{DateTimeOffset.Now:HH:mm:ss}  {message}"; logs.Items.Insert(0, line);
        if (logs.Items.Count > 500) logs.Items.RemoveAt(500);
        try { Directory.CreateDirectory(RunStore.Root); File.AppendAllText(Path.Combine(RunStore.Root, "operations.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); } catch (IOException) { }
    }
    private void RefreshHistory()
    {
        history.ItemsSource = RunStore.History().Where(record => record.Id.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || record.Recipe.Specimen.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private void SaveRecipe(object? sender, RoutedEventArgs args) => Guard(() =>
    {
        var recipe = ReadRecipe(); var dialog = new SaveFileDialog { Filter = "测试配方 (*.json)|*.json", FileName = "recipe.json" };
        if (dialog.ShowDialog() == true) { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(recipe, new JsonSerializerOptions { WriteIndented = true })); Log("配方已保存。"); }
    });
    private void ImportRecipe(object? sender, RoutedEventArgs args) => Guard(() =>
    {
        if (state is State.Running or State.Paused) return;
        var dialog = new OpenFileDialog { Filter = "测试配方 (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        var recipe = JsonSerializer.Deserialize<Recipe>(File.ReadAllText(dialog.FileName)) ?? throw new ArgumentException("配方为空。");
        if (recipe.Validate() is { Length: > 0 } message) throw new ArgumentException(message);
        mode.SelectedIndex = (int)recipe.Mode; fields["Specimen"].Text = recipe.Specimen; fields["Operator"].Text = recipe.Operator;
        foreach (var pair in new[] { ("Lead", recipe.Lead), ("Rpm", recipe.Rpm), ("Force", recipe.Force), ("Stroke", recipe.Stroke), ("Seconds", (double)recipe.Seconds), ("Cycles", (double)recipe.Cycles) }) fields[pair.Item1].Text = pair.Item2.ToString(CultureInfo.CurrentCulture);
        Log("配方已导入并通过范围检查。");
    });
    private Record Selected() => history.SelectedItem as Record ?? throw new ArgumentException("请先选择一条历史记录。");
    private void ExportCsv(object? sender, RoutedEventArgs args) => Guard(() =>
    {
        var record = Selected(); var dialog = new SaveFileDialog { Filter = "原始采样 (*.csv)|*.csv", FileName = record.Id + ".csv" };
        if (dialog.ShowDialog() == true) { File.Copy(Path.Combine(RunStore.Root, record.Id, "samples.csv"), dialog.FileName, true); Log("原始数据已导出。"); }
    });
    private void ExportReport(object? sender, RoutedEventArgs args) => Guard(() =>
    {
        var record = Selected(); var dialog = new SaveFileDialog { Filter = "离线报告 (*.html)|*.html", FileName = record.Id + "-report.html" };
        if (dialog.ShowDialog() == true) { WriteReport(record, dialog.FileName); Log("离线 HTML 报告已导出。"); }
    });
    private static void WriteReport(Record record, string path)
    {
        string Encode(object? value) => WebUtility.HtmlEncode(value?.ToString() ?? "");
        string rows = string.Join("", record.Results.Select(result => $"<tr><td>{Encode(result.Parameter)}</td><td>{Encode(result.Value)} {Encode(result.Unit)}</td><td>{Encode(result.Note)}</td></tr>"));
        File.WriteAllText(path, $"<!doctype html><html lang='zh-CN'><meta charset='utf-8'><title>模拟测试报告</title><style>body{{font:15px/1.8 'Microsoft YaHei',sans-serif;max-width:1000px;margin:40px auto;color:#263637}}table{{border-collapse:collapse;width:100%}}td,th{{border:1px solid #ccd6d6;padding:12px;text-align:left}}.warning{{background:#fff2c9;padding:16px}}h1{{font-size:25px}}</style><h1>丝杠螺母模拟测试报告</h1><p class='warning'>SIMULATED · 非实测数据，不用于验收或合格判定。</p><p>测试编号：{Encode(record.Id)}<br>样件：{Encode(record.Recipe.Specimen)}<br>操作人员：{Encode(record.Recipe.Operator)}<br>模式：{Encode(record.Recipe.Mode)}<br>开始：{Encode(record.Started)}<br>停止原因：{Encode(record.StopReason)}<br>采样点数：{record.Samples}</p><p>导程 {record.Recipe.Lead} mm；转速 {record.Recipe.Rpm} rpm；载荷 {record.Recipe.Force} N；行程 {record.Recipe.Stroke} mm。</p><table><tr><th>参数</th><th>结果</th><th>计算说明</th></tr>{rows}</table><p>软件：ScrewBench v0.1；标定：未标定；数据来源：内置模拟器。原始文件：samples.csv；元数据：result.json。</p></html>", new UTF8Encoding(true));
    }
    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (state is State.Running or State.Paused)
        {
            if (!smoke && MessageBox.Show("当前测试尚未结束，停止并保存后退出？", "确认退出", MessageBoxButton.YesNo) != MessageBoxResult.Yes) { args.Cancel = true; return; }
            Guard(() => Finish("关闭软件，测试中止", State.Stopped));
        }
        timer.Stop(); store?.Dispose(); mechanism.Dispose();
    }
    private void Capture(string file)
    {
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(qaRoot, file)); encoder.Save(stream);
    }
    private async Task SmokeAsync()
    {
        Directory.CreateDirectory(qaRoot);
        try
        {
            tabs.SelectedIndex = 1;
            await mechanism.Ready.WaitAsync(TimeSpan.FromSeconds(40));
            tabs.SelectedIndex = 0;
            Connect(null, new()); Start(null, new()); await Task.Delay(5500); Pause(null, new());
            if (state != State.Paused || samples.Count < 60) throw new Exception("采样/暂停失败");
            Capture("desktop.png"); Width = 1060; Height = 740; await Task.Delay(500); Capture("compact.png");
            if (chart.ActualHeight < 80 || chart.ActualWidth < 100) throw new Exception("紧凑窗口趋势图尺寸不足");
            tabs.SelectedIndex = 1; await Task.Delay(500);
            await mechanism.CaptureAsync(Path.Combine(qaRoot, "3d-compact.png"));
            using (var inspection = JsonDocument.Parse(await mechanism.InspectAsync()))
            {
                var root = inspection.RootElement;
                if (Math.Abs(root.GetProperty("carriageX").GetDouble() - samples[^1].Position) > 0.00001)
                    throw new Exception("三维位置与采样值不一致");
                if (root.GetProperty("source").GetString() != "SIMULATED" || root.GetProperty("calls").GetInt32() < 10)
                    throw new Exception("三维数据来源或渲染异常");
            }
            Width = 1440; Height = 920; await Task.Delay(500);
            await mechanism.CaptureAsync(Path.Combine(qaRoot, "3d-desktop.png"));
            mechanism.ImportPositionJson("""{"version":1,"session":"SMOKE-IMPORTED","sequence":1,"time":0.05,"position":25,"valid":true}""");
            mechanism.Update(samples[^1], state, sceneSession);
            await Task.Delay(300);
            using (var imported = JsonDocument.Parse(await mechanism.InspectAsync()))
            {
                var root = imported.RootElement;
                if (root.GetProperty("source").GetString() != "IMPORTED" || root.GetProperty("carriageX").GetDouble() != 25
                    || root.GetProperty("readHeadPosition")[0].GetDouble() != 25 || root.GetProperty("rulerPosition")[0].GetDouble() != 50)
                    throw new Exception("独立位置输入或固定尺身验证失败");
            }
            await mechanism.CaptureAsync(Path.Combine(qaRoot, "3d-imported-position.png"));
            mechanism.ResumeSimulation(); await Task.Delay(300);
            using (var resumed = JsonDocument.Parse(await mechanism.InspectAsync()))
                if (resumed.RootElement.GetProperty("source").GetString() != "SIMULATED") throw new Exception("恢复模拟显示失败");
            tabs.SelectedIndex = 0;
            Pause(null, new()); await Task.Delay(1000); Fault(null, new());
            if (state != State.Fault || store is not null || start.IsEnabled) throw new Exception("故障锁存失败");
            Reset(null, new()); if (state != State.Ready) throw new Exception("故障复位失败");
            foreach (var testMode in new[] { Mode.Backlash, Mode.Stiffness, Mode.Fatigue })
            {
                mode.SelectedIndex = (int)testMode; fields["Seconds"].Text = "10"; fields["Cycles"].Text = "1";
                Start(null, new()); await Task.Delay(11500);
                if (state != State.Completed) throw new Exception("模式未完成：" + testMode);
            }
            var record = RunStore.History().First(); WriteReport(record, Path.Combine(qaRoot, "report.html"));
            File.WriteAllText(Path.Combine(qaRoot, "smoke.txt"), "PASS: connect/start/pause/resume/fault/reset/three modes/save/report/responsive snapshots/3D position binding/imported position/reader motion/fixed scale/source isolation/resume simulation");
        }
        catch (Exception exception) { Environment.ExitCode = 1; File.WriteAllText(Path.Combine(qaRoot, "smoke.txt"), exception.ToString()); }
        Close();
    }
}

public sealed class TrendChart : FrameworkElement
{
    private (double time, double value)[] points = [];
    public void Set((double time, double value)[] points) { this.points = points; InvalidateVisual(); }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        double width = ActualWidth, height = ActualHeight;
        if (width < 100 || height < 80) return;
        var area = new Rect(60, 20, width - 80, height - 54);
        var valid = points.Where(point => double.IsFinite(point.value)).ToArray();
        double minimum = valid.Length > 0 ? valid.Min(point => point.value) : -1;
        double maximum = valid.Length > 0 ? valid.Max(point => point.value) : 1;
        double padding = Math.Max((maximum - minimum) * 0.12, Math.Max(Math.Abs(maximum) * 0.05, 0.0001)); minimum -= padding; maximum += padding;
        void Label(string text, double left, double top) => context.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 11, Brushes.Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(left, top));
        for (int line = 0; line <= 4; line++)
        {
            double vertical = area.Top + area.Height * line / 4;
            context.DrawLine(new Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(229, 235, 235)), 1), new Point(area.Left, vertical), new Point(area.Right, vertical));
            Label((maximum - (maximum - minimum) * line / 4).ToString("G4"), 0, vertical - 8);
        }
        if (points.Length < 2) { Label("等待采样", area.Left + area.Width / 2 - 26, area.Top + area.Height / 2); return; }
        double first = points[0].time, last = points[^1].time;
        Label($"{first:F1} s", area.Left, area.Bottom + 8); Label($"{last:F1} s", area.Right - 48, area.Bottom + 8);
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        {
            bool open = false;
            foreach (var point in points)
            {
                if (!double.IsFinite(point.value)) { open = false; continue; }
                var position = new Point(area.Left + (point.time - first) / Math.Max(last - first, 0.01) * area.Width, area.Bottom - (point.value - minimum) / (maximum - minimum) * area.Height);
                if (!open) { drawing.BeginFigure(position, false, false); open = true; } else drawing.LineTo(position, true, false);
            }
        }
        geometry.Freeze(); context.DrawGeometry(null, new Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 126, 114)), 2), geometry);
    }
}
