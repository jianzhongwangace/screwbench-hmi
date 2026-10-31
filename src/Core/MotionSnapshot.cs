using System.Text.Json;

namespace ScrewBench;

// Engineering-unit input for visualization only; this is not the controller wire protocol.
public sealed record MotionSnapshot
{
    public required int Version { get; init; }
    public required string Session { get; init; }
    public required long Sequence { get; init; }
    public required double Time { get; init; }
    public required double Position { get; init; }
    public required bool Valid { get; init; }
    public double? Angle { get; init; }
    public double? Rpm { get; init; }
    public double? Force { get; init; }
    public double? Torque { get; init; }

    public void Validate()
    {
        if (Version != 1) throw new ArgumentException("位置输入版本必须为1。");
        if (string.IsNullOrWhiteSpace(Session) || Session.Length > 80) throw new ArgumentException("必须提供1-80字符的采集会话编号。");
        if (Sequence < 0 || Sequence > 9007199254740991L) throw new ArgumentException("采样序号超出有效范围。");
        if (!double.IsFinite(Time) || Time < 0 || !double.IsFinite(Position)) throw new ArgumentException("时间和位移必须为有限数值，时间不得为负数。");
        if (new[] { Angle, Rpm, Force, Torque }.Any(v => v.HasValue && !double.IsFinite(v.Value)))
            throw new ArgumentException("可选测量通道不得为NaN或无穷值。");
    }

    public static MotionSnapshot Parse(string json)
    {
        var result = JsonSerializer.Deserialize<MotionSnapshot>(json, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
            ?? throw new ArgumentException("位置数据为空。");
        result.Validate();
        return result;
    }
}
