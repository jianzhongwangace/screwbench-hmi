using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScrewBench.Desktop;

public sealed class PacketField
{
    public string Name { get; set; } = "";
    public int Offset { get; set; }
    public string Type { get; set; } = "uint16";
    public double Scale { get; set; } = 1;
    public string Unit { get; set; } = "";
}

public sealed class PacketLayout
{
    public string Note { get; set; } = "";
    public string Endian { get; set; } = "LE";
    public int SyncOffset { get; set; }
    public string SyncPattern { get; set; } = "";
    public int LengthOffset { get; set; } = 2;
    public string LengthType { get; set; } = "uint16";
    public bool LengthIncludesHeader { get; set; } = true;
    public int MinFrame { get; set; } = 8;
    public string TextSyncPattern { get; set; } = "";
    public List<PacketField> Fields { get; set; } = new();

    public byte[] SyncBytes() => HexToBytes(SyncPattern);
    public byte[] TextSyncBytes() => HexToBytes(TextSyncPattern);

    private static byte[] HexToBytes(string pattern)
    {
        var bytes = new List<byte>();
        for (int i = 0; i + 1 < pattern.Length; i += 2)
            if (byte.TryParse(pattern.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value)) bytes.Add(value);
        return bytes.ToArray();
    }
}

public sealed class ParsedRow
{
    public string Field { get; set; } = "";
    public string Raw { get; set; } = "";
    public string Value { get; set; } = "";
    public string Unit { get; set; } = "";
}

/// <summary>
/// 数据包调试窗口：接收 TCP 字节流，按外置 JSON 字段定义切帧并解析显示。
/// 本窗口只消费协议，不定义协议；字段定义可替换，协议冻结后无需改代码。
/// </summary>
public sealed class PacketInspectorWindow : Window
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly ComboBox role = new();
    private readonly TextBox host = new() { Text = "127.0.0.1", Width = 130 };
    private readonly TextBox port = new() { Text = "9000", Width = 80 };
    private readonly Button toggle;
    private readonly TextBlock status = new() { FontSize = 12, Text = "未启动" };
    private readonly TextBlock layoutNote = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private readonly TextBox hex = new()
    {
        IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Background = Brushes.White, BorderThickness = new Thickness(1), BorderBrush = Brushes.LightGray
    };
    private readonly TextBox textField = new() { Text = "lab202", Width = 110 };
    private readonly DataGrid parsed = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
        RowHeight = 30, ColumnHeaderHeight = 32, Background = Brushes.White
    };
    private readonly TextBlock stats = new() { FontSize = 12, Text = "帧 0 · 字节 0 · 0 B/s · 序号跳变 0" };
    private readonly ObservableCollection<ParsedRow> rows = new();

    private PacketLayout layout = DraftLayout();
    private readonly List<byte> buffer = new();
    private readonly object gate = new();
    private TcpListener? listener;
    private CancellationTokenSource? cts;
    private long frames, bytesTotal, seqBreaks, bytesWindow, crcErrors;
    private long lastSeq = -1;
    private DateTimeOffset windowStart = DateTimeOffset.Now;
    private bool running;
    private readonly List<string> recent = new();
    private readonly DispatcherTimer pump = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public PacketInspectorWindow()
    {
        Title = "数据包调试 · 传感器原始数据";
        Width = 940; Height = 620;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 10, 12, 4) };
        role.Items.Add("TCP 服务端（监听端口）"); role.Items.Add("TCP 客户端（连接设备）"); role.SelectedIndex = 0;
        bar.Children.Add(role);
        bar.Children.Add(Labeled("地址", host));
        bar.Children.Add(Labeled("端口", port));
        toggle = new Button { Content = "启动", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(10, 0, 0, 0), MinWidth = 82 };
        toggle.Click += Toggle;
        bar.Children.Add(toggle);
        bar.Children.Add(Plain("发送测试帧", SendTestFrame));
        bar.Children.Add(Labeled("文本", textField));
        bar.Children.Add(Plain("发送文本帧", SendTextFrame));
        bar.Children.Add(Plain("加载布局", LoadLayout));
        bar.Children.Add(Plain("另存布局", SaveLayout));
        bar.Children.Add(Plain("清空", Clear));
        var statusBorder = new Border { Background = Brushes.WhiteSmoke, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(12, 0, 0, 0), Child = status };
        bar.Children.Add(statusBorder);
        Grid.SetRow(root, bar, 0);

        layoutNote.Text = "布局：" + layout.Note;
        layoutNote.Margin = new Thickness(12, 0, 12, 8);
        Grid.SetRow(root, layoutNote, 1);

        var body = new Grid { Margin = new Thickness(12, 0, 12, 8) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        var hexPanel = new DockPanel();
        var hexTitle = new TextBlock { Text = "原始字节流（十六进制 · 最近 60 帧）", FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(hexTitle, Dock.Top);
        hexPanel.Children.Add(hexTitle);
        hexPanel.Children.Add(hex);
        Grid.SetColumn(body, new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Child = hexPanel }, 0);

        var rightPanel = new DockPanel { Margin = new Thickness(10, 0, 0, 0) };
        var gridTitle = new TextBlock { Text = "按布局解析（最近一帧）", FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(gridTitle, Dock.Top);
        rightPanel.Children.Add(gridTitle);
        parsed.ItemsSource = rows;
        parsed.Columns.Add(new DataGridTextColumn { Header = "字段", Binding = new System.Windows.Data.Binding("Field"), Width = 130 });
        parsed.Columns.Add(new DataGridTextColumn { Header = "原始值", Binding = new System.Windows.Data.Binding("Raw"), Width = 85 });
        parsed.Columns.Add(new DataGridTextColumn { Header = "工程值", Binding = new System.Windows.Data.Binding("Value"), Width = 95 });
        parsed.Columns.Add(new DataGridTextColumn { Header = "单位", Binding = new System.Windows.Data.Binding("Unit"), Width = 50 });
        rightPanel.Children.Add(parsed);
        Grid.SetColumn(body, rightPanel, 1);
        Grid.SetRow(root, body, 2);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 12) };
        footer.Children.Add(stats);
        footer.Children.Add(new TextBlock { Text = "  ·  调试窗口不参与实时控制，也不写入试验记录", FontSize = 12, Foreground = Brushes.DimGray });
        Grid.SetRow(root, footer, 3);

        Content = root;
        pump.Tick += (_, _) => Drain();
        pump.Start();
        Closed += (_, _) => { pump.Stop(); Stop(); };
    }

    private static UIElement Labeled(string title, UIElement child)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        panel.Children.Add(child);
        return panel;
    }
    private static Button Plain(string label, RoutedEventHandler action)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        button.Click += action;
        return button;
    }

    private void Toggle(object sender, RoutedEventArgs e)
    {
        if (running) { Stop(); return; }
        if (!int.TryParse(port.Text, out int portNumber) || portNumber < 1 || portNumber > 65535)
        {
            status.Text = "端口无效"; return;
        }
        try
        {
            cts = new CancellationTokenSource();
            if (role.SelectedIndex == 0)
            {
                listener = new TcpListener(IPAddress.Any, portNumber);
                listener.Start();
                _ = Task.Run(() => AcceptLoopAsync(listener, cts.Token));
                status.Text = $"监听中 · 端口 {portNumber}";
            }
            else
            {
                status.Text = $"连接中 · {host.Text}:{portNumber}";
                _ = Task.Run(() => ClientLoopAsync(host.Text, portNumber, cts.Token));
            }
            running = true;
            toggle.Content = "停止";
        }
        catch (Exception exception)
        {
            status.Text = "启动失败：" + exception.Message;
            running = false; cts = null; listener = null;
        }
    }

    private void Stop()
    {
        running = false;
        try { cts?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        listener = null; cts = null;
        toggle.Content = "启动";
        status.Text = "已停止";
    }

    private async Task AcceptLoopAsync(TcpListener server, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await server.AcceptTcpClientAsync(token);
                Dispatcher.Invoke(() => status.Text = $"已连接 · {client.Client.RemoteEndPoint}");
                _ = Task.Run(() => ReadLoopAsync(client, token));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception exception) { Dispatcher.Invoke(() => status.Text = "监听异常：" + exception.Message); }
    }

    private async Task ClientLoopAsync(string address, int portNumber, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(address, portNumber, token);
                Dispatcher.Invoke(() => status.Text = $"已连接 · {address}:{portNumber}");
                await ReadLoopAsync(client, token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception)
            {
                Dispatcher.Invoke(() => status.Text = "连接断开，3 秒后重试");
                try { await Task.Delay(3000, token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ReadLoopAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            using var stream = client.GetStream();
            var chunk = new byte[8192];
            while (!token.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(chunk, token);
                if (read <= 0) break;
                lock (gate)
                {
                    for (int i = 0; i < read; i++) buffer.Add(chunk[i]);
                    if (buffer.Count > 2_000_000) buffer.RemoveRange(0, buffer.Count - 1_000_000);
                }
                Interlocked.Add(ref bytesTotal, read);
                Interlocked.Add(ref bytesWindow, read);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            try { client.Dispose(); } catch { }
            if (running) Dispatcher.Invoke(() => status.Text = "连接已断开");
        }
    }

    private void Drain()
    {
        List<(byte[] Frame, bool IsText)>? framesFound = null;
        var sampleSync = layout.SyncBytes();
        var textSync = layout.TextSyncBytes();
        lock (gate)
        {
            while (true)
            {
                if (sampleSync.Length == 0 && textSync.Length == 0)
                {
                    if (buffer.Count < layout.MinFrame) break;
                    var fixedFrame = buffer.Take(layout.MinFrame).ToArray();
                    buffer.RemoveRange(0, layout.MinFrame);
                    (framesFound ??= new()).Add((fixedFrame, false));
                    continue;
                }
                int sampleIdx = sampleSync.Length > 0 ? IndexOf(buffer, sampleSync) : -1;
                int textIdx = textSync.Length > 0 ? IndexOf(buffer, textSync) : -1;
                if (sampleIdx < 0 && textIdx < 0)
                {
                    int keep = Math.Max(sampleSync.Length, textSync.Length);
                    if (buffer.Count > keep + 4096) buffer.RemoveRange(0, buffer.Count - keep);
                    break;
                }
                bool isText;
                int start;
                if (sampleIdx >= 0 && (textIdx < 0 || sampleIdx < textIdx)) { start = sampleIdx; isText = false; }
                else { start = textIdx; isText = true; }
                if (start > 0) buffer.RemoveRange(0, start);
                int length = isText ? TextFrameLength(buffer) : FrameLength(buffer);
                if (length <= 0 || length > buffer.Count) break;
                var frame = buffer.Take(length).ToArray();
                buffer.RemoveRange(0, length);
                (framesFound ??= new()).Add((frame, isText));
                if (framesFound.Count >= 200) break;
            }
        }
        if (framesFound is null)
        {
            UpdateStats();
            return;
        }
        foreach (var (frame, isText) in framesFound) HandleFrame(frame, isText);
        hex.Text = string.Join(Environment.NewLine, recent);
        hex.ScrollToEnd();
        UpdateStats();
    }

    private int TextFrameLength(List<byte> data)
    {
        int size = TypeSize(layout.LengthType);
        if (size <= 0 || layout.LengthOffset + size > data.Count) return 0;
        double value = ReadNumber(data, layout.LengthOffset, layout.LengthType);
        int length = layout.LengthIncludesHeader ? (int)value : (int)value + layout.LengthOffset;
        return length < 6 ? 6 : length;
    }

    private static int IndexOf(List<byte> source, byte[] pattern)
    {
        if (pattern.Length == 0 || source.Count < pattern.Length) return -1;
        for (int i = 0; i + pattern.Length <= source.Count; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++) if (source[i + j] != pattern[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    private int FrameLength(List<byte> data)
    {
        if (data.Count < layout.MinFrame) return 0;
        if (string.IsNullOrWhiteSpace(layout.SyncPattern)) return layout.MinFrame;
        int size = TypeSize(layout.LengthType);
        if (size <= 0 || layout.LengthOffset + size > data.Count) return 0;
        double value = ReadNumber(data, layout.LengthOffset, layout.LengthType);
        int length = layout.LengthIncludesHeader ? (int)value : (int)value + layout.LengthOffset;
        return length < layout.MinFrame ? layout.MinFrame : length;
    }

    private void HandleFrame(byte[] frame, bool isText)
    {
        frames++;
        if (isText)
        {
            rows.Clear();
            int textLen = frame.Length - 6;   // 去掉 sync2 + length2 + crc2
            string text = textLen > 0 ? Encoding.ASCII.GetString(frame, 4, textLen) : "";
            ushort expected = (ushort)ReadNumber(frame, frame.Length - 2, "uint16");
            bool ok = expected == Crc16(frame, frame.Length - 2);
            if (!ok) crcErrors++;
            rows.Add(new ParsedRow { Field = "text", Raw = $"{textLen} B", Value = text, Unit = "" });
            rows.Add(new ParsedRow { Field = "crc16", Raw = $"0x{expected:X4}", Value = ok ? "OK" : "BAD", Unit = "" });
            recent.Add($"#{frames,6}  {frame.Length,4} B  {Convert.ToHexString(frame)}   TEXT \"{text}\"  CRC={(ok ? "OK" : "BAD")}");
            if (recent.Count > 60) recent.RemoveAt(0);
            return;
        }
        var summary = new StringBuilder();
        rows.Clear();
        foreach (var field in layout.Fields)
        {
            if (field.Offset + TypeSize(field.Type) > frame.Length) continue;
            double raw = ReadNumber(frame, field.Offset, field.Type);
            double value = raw * field.Scale;
            string rawText = field.Type == "float" || field.Type == "double" ? raw.ToString("F4", CultureInfo.InvariantCulture) : ((long)raw).ToString(CultureInfo.InvariantCulture);
            string valueText = Math.Abs(field.Scale - 1) < 1e-12 ? rawText : value.ToString("G6", CultureInfo.InvariantCulture);
            rows.Add(new ParsedRow { Field = field.Name, Raw = rawText, Value = valueText, Unit = field.Unit });
            summary.Append($"{field.Name}={valueText}{field.Unit} ");
            if (field.Name.Contains("seq", StringComparison.OrdinalIgnoreCase))
            {
                long seq = (long)raw;
                if (lastSeq >= 0 && seq != lastSeq + 1) seqBreaks++;
                lastSeq = seq;
            }
        }
        var crcField = layout.Fields.FirstOrDefault(field => field.Name.Contains("crc", StringComparison.OrdinalIgnoreCase));
        if (crcField is not null && TypeSize(crcField.Type) == 2 && crcField.Offset + 2 <= frame.Length)
        {
            ushort expected = (ushort)ReadNumber(frame, crcField.Offset, crcField.Type);
            bool ok = expected == Crc16(frame, crcField.Offset);
            if (!ok) crcErrors++;
            summary.Append($"CRC={(ok ? "OK" : "BAD")} ");
        }
        recent.Add($"#{frames,6}  {frame.Length,4} B  {Convert.ToHexString(frame)}");
        if (recent.Count > 60) recent.RemoveAt(0);
        recent[^1] += "   " + summary.ToString().Trim();
    }

    private void UpdateStats()
    {
        double seconds = (DateTimeOffset.Now - windowStart).TotalSeconds;
        if (seconds >= 1)
        {
            double rate = bytesWindow / seconds;
            windowStart = DateTimeOffset.Now;
            bytesWindow = 0;
            stats.Text = $"帧 {frames} · 字节 {bytesTotal} · {rate / 1024:F1} KiB/s · 序号跳变 {seqBreaks} · CRC 错 {crcErrors}";
        }
        else
        {
            stats.Text = $"帧 {frames} · 字节 {bytesTotal} · 序号跳变 {seqBreaks} · CRC 错 {crcErrors}";
        }
    }

    private void Clear(object sender, RoutedEventArgs e)
    {
        lock (gate) buffer.Clear();
        recent.Clear(); rows.Clear(); hex.Text = "";
        frames = 0; bytesTotal = 0; seqBreaks = 0; bytesWindow = 0; crcErrors = 0; lastSeq = -1;
        UpdateStats();
    }

    private void SendTestFrame(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(port.Text, out int portNumber)) { status.Text = "端口无效"; return; }
        try
        {
            var frame = BuildTestFrame(frames + 1);
            using var client = new TcpClient();
            client.Connect("127.0.0.1", portNumber);
            using var stream = client.GetStream();
            stream.Write(frame);
            stream.Flush();
            status.Text = $"已发送测试帧 {frame.Length} B（本机回环，仅验证解析）";
        }
        catch (Exception exception) { status.Text = "发送失败：" + exception.Message; }
    }

    private void SendTextFrame(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(port.Text, out int portNumber)) { status.Text = "端口无效"; return; }
        try
        {
            var payload = Encoding.ASCII.GetBytes(textField.Text ?? "");
            int length = 4 + payload.Length + 2;
            var frame = new byte[length];
            var sync = layout.TextSyncBytes();
            for (int i = 0; i < sync.Length && i < length; i++) frame[i] = sync[i];
            WriteNumber(frame, layout.LengthOffset, layout.LengthType, length);
            Array.Copy(payload, 0, frame, 4, payload.Length);
            WriteNumber(frame, length - 2, "uint16", Crc16(frame, length - 2));
            using var client = new TcpClient();
            client.Connect("127.0.0.1", portNumber);
            using var stream = client.GetStream();
            stream.Write(frame);
            stream.Flush();
            status.Text = $"已发送文本帧 \"{textField.Text}\"（{length} B）";
        }
        catch (Exception exception) { status.Text = "发送失败：" + exception.Message; }
    }

    private byte[] BuildTestFrame(long sequence)
    {
        int last = layout.Fields.Count == 0 ? 0 : layout.Fields.Max(field => field.Offset + TypeSize(field.Type));
        int size = Math.Max(layout.MinFrame, last);
        var frame = new byte[size];
        var sync = layout.SyncBytes();
        for (int i = 0; i < sync.Length && layout.SyncOffset + i < size; i++) frame[layout.SyncOffset + i] = sync[i];
        WriteNumber(frame, layout.LengthOffset, layout.LengthType, size);
        long tick = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var field in layout.Fields)
        {
            double value = field.Name switch
            {
                "timestamp" => tick % 4_000_000_000,
                "sample_seq" => sequence,
                "torque" => 12.5 + (sequence % 7) * 0.1,
                "force" => 980 + (sequence % 11),
                "position" => (sequence % 200) * 5,
                "rpm" => 300,
                "angle" => (sequence % 6283),
                "status" => 0,
                "quality" => 0,
                _ => sequence % 1000
            };
            WriteNumber(frame, field.Offset, field.Type, field.Scale == 0 ? value : Math.Round(value / field.Scale));
        }
        var crcField = layout.Fields.FirstOrDefault(field => field.Name.Contains("crc", StringComparison.OrdinalIgnoreCase));
        if (crcField is not null && TypeSize(crcField.Type) == 2 && crcField.Offset + 2 <= size)
        {
            WriteNumber(frame, crcField.Offset, crcField.Type, 0);
            WriteNumber(frame, crcField.Offset, crcField.Type, Crc16(frame, crcField.Offset));
        }
        return frame;
    }

    /// <summary>CRC16/CCITT-FALSE：多项式 0x1021，初值 0xFFFF，非反射，输出不异或。</summary>
    public static ushort Crc16(byte[] data, int length)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < length && i < data.Length; i++)
        {
            crc ^= (ushort)(data[i] << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }

    private void LoadLayout(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "布局 JSON|*.json|所有文件|*.*", FileName = "packet-layout-draft.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var text = File.ReadAllText(dialog.FileName);
            if (JsonSerializer.Deserialize<PacketLayout>(text, Json) is { } loaded)
            {
                layout = loaded;
                layoutNote.Text = "布局：" + layout.Note + "（来源 " + Path.GetFileName(dialog.FileName) + "）";
                status.Text = "布局已加载";
            }
        }
        catch (Exception exception) { status.Text = "布局加载失败：" + exception.Message; }
    }

    private void SaveLayout(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "布局 JSON|*.json", FileName = "packet-layout-draft.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(layout, Json));
            status.Text = "布局已另存";
        }
        catch (Exception exception) { status.Text = "保存失败：" + exception.Message; }
    }

    private static int TypeSize(string type) => type switch
    {
        "int8" or "uint8" => 1,
        "int16" or "uint16" => 2,
        "int32" or "uint32" or "float" => 4,
        "double" => 8,
        _ => 0
    };

    private double ReadNumber(IReadOnlyList<byte> data, int offset, string type)
    {
        bool big = layout.Endian.Equals("BE", StringComparison.OrdinalIgnoreCase);
        var span = new ReadOnlySpan<byte>(data is byte[] array ? array : data.ToArray(), offset, TypeSize(type));
        return type switch
        {
            "int8" => (sbyte)span[0],
            "uint8" => span[0],
            "int16" => big ? BinaryPrimitives.ReadInt16BigEndian(span) : BinaryPrimitives.ReadInt16LittleEndian(span),
            "uint16" => big ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span),
            "int32" => big ? BinaryPrimitives.ReadInt32BigEndian(span) : BinaryPrimitives.ReadInt32LittleEndian(span),
            "uint32" => big ? BinaryPrimitives.ReadUInt32BigEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span),
            "float" => (big ? BinaryPrimitives.ReadSingleBigEndian(span) : BinaryPrimitives.ReadSingleLittleEndian(span)),
            "double" => (big ? BinaryPrimitives.ReadDoubleBigEndian(span) : BinaryPrimitives.ReadDoubleLittleEndian(span)),
            _ => 0
        };
    }

    private void WriteNumber(byte[] frame, int offset, string type, double value)
    {
        bool big = layout.Endian.Equals("BE", StringComparison.OrdinalIgnoreCase);
        var span = new Span<byte>(frame, offset, TypeSize(type));
        switch (type)
        {
            case "int8": span[0] = unchecked((byte)(sbyte)value); break;
            case "uint8": span[0] = unchecked((byte)value); break;
            case "int16": if (big) BinaryPrimitives.WriteInt16BigEndian(span, (short)value); else BinaryPrimitives.WriteInt16LittleEndian(span, (short)value); break;
            case "uint16": if (big) BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)value); else BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)value); break;
            case "int32": if (big) BinaryPrimitives.WriteInt32BigEndian(span, (int)value); else BinaryPrimitives.WriteInt32LittleEndian(span, (int)value); break;
            case "uint32": if (big) BinaryPrimitives.WriteUInt32BigEndian(span, (uint)value); else BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)value); break;
            case "float": if (big) BinaryPrimitives.WriteSingleBigEndian(span, (float)value); else BinaryPrimitives.WriteSingleLittleEndian(span, (float)value); break;
            case "double": if (big) BinaryPrimitives.WriteDoubleBigEndian(span, value); else BinaryPrimitives.WriteDoubleLittleEndian(span, value); break;
        }
    }

    public static PacketLayout DraftLayout() => new()
    {
        Note = "草案 v0.1：32 字节采样帧，含 CRC16（非冻结协议）。协议正文见 docs/上位机与STM32通信协议v0.1草案.md。",
        Endian = "LE",
        SyncOffset = 0,
        SyncPattern = "AA55",
        TextSyncPattern = "AA54",
        LengthOffset = 2,
        LengthType = "uint16",
        LengthIncludesHeader = true,
        MinFrame = 32,
        Fields =
        [
            new PacketField { Name = "timestamp", Offset = 4, Type = "uint32", Scale = 0.001, Unit = "s" },
            new PacketField { Name = "sample_seq", Offset = 8, Type = "uint32", Scale = 1, Unit = "" },
            new PacketField { Name = "torque", Offset = 12, Type = "int16", Scale = 0.01, Unit = "N·m" },
            new PacketField { Name = "force", Offset = 14, Type = "int16", Scale = 0.1, Unit = "N" },
            new PacketField { Name = "position", Offset = 16, Type = "int32", Scale = 0.001, Unit = "mm" },
            new PacketField { Name = "rpm", Offset = 20, Type = "int16", Scale = 1, Unit = "rpm" },
            new PacketField { Name = "angle", Offset = 22, Type = "uint32", Scale = 0.0001, Unit = "rad" },
            new PacketField { Name = "status", Offset = 26, Type = "uint16", Scale = 1, Unit = "位域" },
            new PacketField { Name = "quality", Offset = 28, Type = "uint8", Scale = 1, Unit = "枚举" },
            new PacketField { Name = "flags", Offset = 29, Type = "uint8", Scale = 1, Unit = "位域" },
            new PacketField { Name = "crc16", Offset = 30, Type = "uint16", Scale = 1, Unit = "" }
        ]
    };
}
