using System.Text.Json;

namespace ScrewBench.Desktop;

internal sealed class MotionDemo
{
    internal sealed record Frame(double Time, double Position, double Angle);
    private readonly Frame[] frames;
    private readonly bool loop;
    public double LoadN { get; private init; }
    public double LeadMm { get; private init; }
    public double Efficiency { get; private init; }
    public double Duration => frames[^1].Time;
    private MotionDemo(Frame[] frames, bool loop) { this.frames = frames; this.loop = loop; }
    public static MotionDemo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("source").GetString() != "DEMO")
            throw new ArgumentException("只接受version=1、source=DEMO的演示数据。");
        var frames = root.GetProperty("frames").EnumerateArray().Select(f => new Frame(
            f.GetProperty("time").GetDouble(), f.GetProperty("position").GetDouble(), f.GetProperty("angle").GetDouble())).ToArray();
        if (frames.Length < 2 || frames.Length > 1000 || frames[0].Time != 0) throw new ArgumentException("演示需要2-1000个关键帧，从0秒开始。");
        for (int i = 0; i < frames.Length; i++)
        {
            var f = frames[i];
            if (!double.IsFinite(f.Time) || !double.IsFinite(f.Position) || !double.IsFinite(f.Angle) || f.Position < 0 || f.Position > 100
                || f.Time > 3600 || (i > 0 && f.Time <= frames[i-1].Time)) throw new ArgumentException("关键帧时间必须递增，位移范围0-100mm，数值必须有效。");
        }
        bool loop = root.GetProperty("loop").GetBoolean();
        if (loop && (Math.Abs(frames[0].Position - frames[^1].Position) > 1e-6 || Math.Abs(frames[0].Angle - frames[^1].Angle) > 1e-6))
            throw new ArgumentException("循环演示的首尾位置与转角必须一致，避免跳变。");
        double load = root.GetProperty("loadN").GetDouble(), lead = root.GetProperty("leadMm").GetDouble(), efficiency = root.GetProperty("efficiency").GetDouble();
        if (!double.IsFinite(load) || load < 0 || load > 1000 || !double.IsFinite(lead) || lead <= 0 || lead > 50 || !double.IsFinite(efficiency) || efficiency <= 0 || efficiency > 1)
            throw new ArgumentException("演示载荷、导程或效率配置不合法。");
        return new(frames, loop) { LoadN = load, LeadMm = lead, Efficiency = efficiency };
    }
    public (double position, double angle, double rpm, bool finished) At(double elapsed)
    {
        double t = loop ? elapsed % Duration : Math.Min(elapsed, Duration);
        int index = Array.FindIndex(frames, f => f.Time >= t);
        if (index <= 0) return (frames[0].Position, frames[0].Angle, 0, false);
        var a = frames[index-1]; var b = frames[index];
        double factor = (1 - Math.Cos(Math.PI * (t - a.Time) / (b.Time - a.Time))) / 2;
        double omega = (b.Angle-a.Angle) * Math.PI * Math.Sin(Math.PI*(t-a.Time)/(b.Time-a.Time)) / (2*(b.Time-a.Time));
        return (a.Position + (b.Position-a.Position)*factor, a.Angle + (b.Angle-a.Angle)*factor, omega*30/Math.PI, !loop && elapsed >= Duration);
    }
}
