using System.Text;
using System.Text.Json;

namespace ScrewBench;

public enum Mode { Efficiency, Backlash, Stiffness, Fatigue }
public enum State { Disconnected, Ready, Running, Paused, Completed, Stopped, Fault }
public sealed record Recipe(string Specimen, string Operator, Mode Mode, double Lead, double Rpm, double Force, double Stroke, int Seconds, int Cycles)
{
    public static Recipe Default => new("DEMO-001", "操作员", Mode.Efficiency, 2, 300, 100, 20, 30, 10);
    public string Validate()
    {
        if (string.IsNullOrWhiteSpace(Specimen) || Specimen.Length > 80) return "样件编号必须为 1-80 个字符。";
        if (!Enum.IsDefined(Mode)) return "测试模式无效。";
        if (!double.IsFinite(Lead) || Lead <= 0 || Lead > 50) return "导程须大于 0 且不超过 50 mm。";
        if (!double.IsFinite(Rpm) || Rpm < 10 || Rpm > 1500) return "模拟转速范围：10-1500 rpm。";
        if (!double.IsFinite(Force) || Force < 10 || Force > 375) return "模拟轴向载荷范围：10-375 N。";
        if (!double.IsFinite(Stroke) || Stroke < 1 || Stroke > 100) return "模拟行程范围：1-100 mm。";
        if (Seconds < 10 || Seconds > 3600 || Cycles < 1 || Cycles > 2800000) return "时长范围 10-3600 s，循环数范围 1-2800000。";
        return "";
    }
}
public sealed record Sample(long Sequence, double Time, double Force, double Torque, double Rpm, double Angle, double Position, double Velocity, long Cycles, bool Valid, bool Steady);
public sealed record Result(string Parameter, string Value, string Unit, string Note);
public sealed record Record(string Id, DateTimeOffset Started, Recipe Recipe, string Source, string StopReason, int Samples, Result[] Results);

public static class Metrics
{
    public static double? Efficiency(Sample sample)
    {
        double input = sample.Torque * sample.Rpm * Math.PI / 30;
        double output = -sample.Force * sample.Velocity / 1000;
        if (!sample.Valid || !sample.Steady || input <= 1e-8 || output <= 0) return null;
        double value = output / input * 100;
        return double.IsFinite(value) && value <= 100 ? value : null;
    }
    public static Result[] Calculate(Recipe recipe, IReadOnlyList<Sample> samples)
    {
        var valid = samples.Where(sample => sample.Valid).ToArray();
        if (valid.Length < 5) return [new("数据不足", "--", "", "有效采样不足，未计算结果。")];
        if (recipe.Mode == Mode.Efficiency)
        {
            var values = valid.Select(Efficiency).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
            return values.Length == 0 ? [new("稳态效率", "--", "%", "没有有效稳态区间。")] :
                [new("稳态效率", values.Average().ToString("F2"), "%", $"有效点 {values.Length}；仅驱动工况，不作合格判定。"),
                 new("效率范围", $"{values.Min():F2} - {values.Max():F2}", "%", "剔除加减速、换向和非物理数据，不截断异常值。")];
        }
        if (recipe.Mode == Mode.Stiffness)
        {
            var points = valid.Where(sample => sample.Steady && Math.Abs(sample.Rpm) < 0.01).ToArray();
            if (points.Length < 5) return [new("轴向等效刚度", "--", "N/μm", "保载且角位置保持的数据不足。")];
            double avgX = points.Average(sample => sample.Position * 1000);
            double avgForce = points.Average(sample => sample.Force);
            double variance = points.Sum(sample => Math.Pow(sample.Position * 1000 - avgX, 2));
            if (variance < 1e-12) return [new("轴向等效刚度", "--", "N/μm", "位移变化不足，无法拟合。")];
            double slope = points.Sum(sample => (sample.Position * 1000 - avgX) * (sample.Force - avgForce)) / variance;
            double residual = points.Sum(sample => Math.Pow(sample.Force - avgForce - slope * (sample.Position * 1000 - avgX), 2));
            double total = points.Sum(sample => Math.Pow(sample.Force - avgForce, 2));
            return [new("轴向等效刚度", slope.ToString("F2"), "N/μm", "装配边界下 F-x 斜率；未扣除支撑与台架柔度。"),
                    new("拟合 R²", (total > 0 ? 1 - residual / total : 0).ToString("F4"), "", "保载段线性拟合；实测需独立评估加载/卸载滞回。")];
        }
        if (recipe.Mode == Mode.Backlash)
        {
            var forward = valid.Where(sample => sample.Steady && sample.Rpm > 0).Select(sample => recipe.Lead * sample.Angle / (2 * Math.PI) - sample.Position).ToArray();
            var reverse = valid.Where(sample => sample.Steady && sample.Rpm < 0).Select(sample => recipe.Lead * sample.Angle / (2 * Math.PI) - sample.Position).ToArray();
            return forward.Length < 5 || reverse.Length < 5 ? [new("换向残差差值", "--", "μm", "正反方向稳定数据不足。")] :
                [new("换向残差差值", (Math.Abs(forward.Average() - reverse.Average()) * 1000).ToString("F2"), "μm", "演示残差法，包含弹性和摩擦影响，不作为实测游隙验收值。")];
        }
        return [new("已完成往复循环", valid.Max(sample => sample.Cycles).ToString(), "次", "往返计一个循环；达到目标次数不代表失效。"),
                new("有效运行时间", valid.Last().Time.ToString("F1"), "s", "不含暂停时间；未判定试件真实疲劳寿命。")];
    }
}

public sealed class Simulator
{
    private readonly Recipe recipe;
    private double seconds, angle, theoreticalPosition, position = -0.008, stiffnessForce, turnAge;
    private int direction = 1;
    private long halfCycles, sequence;
    public Simulator(Recipe recipe)
    {
        if (recipe.Validate() is { Length: > 0 } error) throw new ArgumentException(error);
        this.recipe = recipe;
    }
    public Sample Next(double elapsed)
    {
        if (!double.IsFinite(elapsed) || elapsed <= 0 || elapsed > 1) throw new ArgumentOutOfRangeException(nameof(elapsed));
        seconds += elapsed;
        turnAge += elapsed;
        sequence++;
        if (recipe.Mode == Mode.Stiffness)
        {
            int step = Math.Min(9, (int)(seconds / recipe.Seconds * 10));
            double target = recipe.Force * (step < 5 ? step + 1 : 10 - step) / 5;
            stiffnessForce += (target - stiffnessForce) * (1 - Math.Exp(-elapsed * 8));
            return new(sequence, seconds, stiffnessForce, 0, 0, 0, stiffnessForce / 50000 + Math.Sin(seconds * 5) * 0.00001, 0, 0, true, Math.Abs(target - stiffnessForce) < 0.2);
        }
        double speed = recipe.Mode == Mode.Backlash ? Math.Min(recipe.Rpm, 60) : recipe.Rpm;
        double rpm = direction * speed * Math.Min(seconds / 1.5, 1);
        theoreticalPosition += rpm * recipe.Lead / 60 * elapsed;
        angle += rpm * 2 * Math.PI / 60 * elapsed;
        double previous = position;
        position = direction > 0 ? Math.Max(position, theoreticalPosition - 0.008) : Math.Min(position, theoreticalPosition + 0.008);
        double efficiency = 0.90 + Math.Sin(seconds * 2) * 0.008;
        var sample = new Sample(sequence, seconds, -direction * recipe.Force, direction * recipe.Force * recipe.Lead / (2000 * Math.PI * efficiency), rpm, angle, position, (position - previous) / elapsed, halfCycles / 2, true, seconds > 2 && turnAge > 0.6);
        double limit = recipe.Mode == Mode.Backlash ? Math.Min(recipe.Stroke, 2) : recipe.Stroke;
        if ((direction > 0 && theoreticalPosition >= limit) || (direction < 0 && theoreticalPosition <= 0))
        {
            direction = -direction;
            halfCycles++;
            turnAge = 0;
        }
        return sample with { Cycles = halfCycles / 2 };
    }
}

public sealed class RunStore : IDisposable
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScrewBench", "runs");
    public string Id { get; } = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
    public string Folder => Path.Combine(Root, Id);
    private readonly StreamWriter writer;
    public RunStore()
    {
        Directory.CreateDirectory(Folder);
        writer = new StreamWriter(Path.Combine(Folder, "samples.csv"), false, new UTF8Encoding(true));
        writer.WriteLine("sequence,seconds,force_N,torque_Nm,rpm,angle_rad,position_mm,velocity_mm_s,cycles,valid,steady,source");
        writer.Flush();
    }
    public void Append(Sample sample)
    {
        writer.WriteLine(FormattableString.Invariant($"{sample.Sequence},{sample.Time:F4},{sample.Force:F6},{sample.Torque:F8},{sample.Rpm:F4},{sample.Angle:F8},{sample.Position:F8},{sample.Velocity:F6},{sample.Cycles},{sample.Valid},{sample.Steady},SIMULATED"));
        if (sample.Sequence % 10 == 0) writer.Flush();
    }
    public void Save(Record record)
    {
        writer.Flush();
        string destination = Path.Combine(Folder, "result.json");
        File.WriteAllText(destination + ".tmp", JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(destination + ".tmp", destination, true);
    }
    public static IReadOnlyList<Record> History()
    {
        if (!Directory.Exists(Root)) return [];
        var results = new List<Record>();
        foreach (string file in Directory.EnumerateFiles(Root, "result.json", SearchOption.AllDirectories))
        {
            try { if (JsonSerializer.Deserialize<Record>(File.ReadAllText(file)) is { } record) results.Add(record); }
            catch (JsonException) { }
            catch (IOException) { }
        }
        return results.OrderByDescending(record => record.Started).ToArray();
    }
    public void Dispose() => writer.Dispose();
}
