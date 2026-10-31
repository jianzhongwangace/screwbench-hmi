using System.Globalization;
using ScrewBench;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}
List<Sample> Run(Recipe recipe)
{
    var source = new Simulator(recipe);
    return Enumerable.Range(0, recipe.Seconds * 20).Select(_ => source.Next(0.05)).ToList();
}

Check(Recipe.Default.Validate() == "", "default recipe");
Check((Recipe.Default with { Lead = double.NaN }).Validate() != "", "reject NaN");
Check((Recipe.Default with { Mode = (Mode)99 }).Validate() != "", "reject unknown mode");
Check((Recipe.Default with { Specimen = " " }).Validate() != "", "reject empty specimen");
Check((Recipe.Default with { Force = 376 }).Validate() != "", "reject load outside simulator range");
var efficiencySamples = Run(Recipe.Default);
Check(efficiencySamples.Count == 600 && efficiencySamples[^1].Sequence == 600, "sample sequence");
var steady = efficiencySamples.Where(s => Metrics.Efficiency(s).HasValue).ToArray();
Check(steady.Any(s => s.Rpm > 0) && steady.Any(s => s.Rpm < 0), "both drive directions");
Check(steady.All(s => Metrics.Efficiency(s) is > 88 and < 92), "efficiency in both directions");
Check(Metrics.Efficiency(steady[0] with { Torque = 0 }) is null, "reject zero input power");
Check(Metrics.Efficiency(steady[0] with { Valid = false }) is null, "reject invalid sample");
Check(Metrics.Efficiency(steady[0] with { Steady = false }) is null, "reject transient sample");
Check(Metrics.Efficiency(steady[0] with { Torque = steady[0].Torque / 2 }) is null, "reject efficiency above 100 percent");
Check(Metrics.Calculate(Recipe.Default, [])[0].Value == "--", "insufficient data");
var backlashRecipe = Recipe.Default with { Mode = Mode.Backlash };
var backlash = Metrics.Calculate(backlashRecipe, Run(backlashRecipe));
Check(Math.Abs(double.Parse(backlash[0].Value) - 16) < 0.1, "simulated reversal residual is 16 um");
var stiffnessRecipe = Recipe.Default with { Mode = Mode.Stiffness };
var stiffness = Metrics.Calculate(stiffnessRecipe, Run(stiffnessRecipe));
Check(Math.Abs(double.Parse(stiffness[0].Value) - 50) < 1, "simulated stiffness is 50 N per um");
Check(double.Parse(stiffness[1].Value) > 0.99, "stiffness fit quality");
var fatigueRecipe = Recipe.Default with { Mode = Mode.Fatigue };
var fatigueSamples = Run(fatigueRecipe);
Check(fatigueSamples[^1].Cycles > 0, "fatigue round trips");
Check(fatigueSamples.Zip(fatigueSamples.Skip(1)).All(pair => pair.Second.Cycles >= pair.First.Cycles), "monotonic cycle counter");
const string positionJson = """{"version":1,"session":"test","sequence":1,"time":0.05,"position":25,"valid":true}""";
var positionSnapshot = MotionSnapshot.Parse(positionJson);
Check(positionSnapshot.Position == 25 && positionSnapshot.Angle is null && positionSnapshot.Force is null, "position-only snapshot preserves missing channels");
void Reject(string json, string name)
{
    bool rejected = false;
    try { MotionSnapshot.Parse(json); }
    catch (Exception e) when (e is System.Text.Json.JsonException or ArgumentException) { rejected = true; }
    Check(rejected, name);
}
Reject(positionJson.Replace("\"position\":25,", ""), "missing position is not zero");
Reject(positionJson.Replace("\"valid\":true", "\"validity\":true"), "misspelled fields rejected");
Reject(positionJson.Replace("\"version\":1", "\"version\":2"), "unknown snapshot version");
Reject(positionJson.Replace("\"time\":0.05", "\"time\":-1"), "negative snapshot time");
Check(!MotionSnapshot.Parse(positionJson.Replace("true", "false")).Valid, "invalid quality flag preserved");
Console.WriteLine($"All {passed} checks passed.");
