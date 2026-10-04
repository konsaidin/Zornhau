using CombatOverhaul.Animations;
using Newtonsoft.Json;
using WarbandCombat;

int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var source = JsonConvert.DeserializeObject<Dictionary<string, AnimationJson>>(
    File.ReadAllText(Path.Combine(root, "third-party/reanimated/clips.json")))!;
foreach (var (package, domain) in new[] { ("combatoverhaulfork_0.18.8", "combatoverhaul"), ("armoryfork_1.11.3", "armory") })
foreach (var file in Directory.GetFiles(Path.Combine(root, $".artifacts/compat/unpacked/{package}/assets/{domain}/config/animations"), "*.json", SearchOption.AllDirectories))
    foreach (var (code, clip) in JsonConvert.DeserializeObject<Dictionary<string, AnimationJson>>(File.ReadAllText(file))!)
        source.TryAdd(code.Contains(':') ? code : domain + ":" + code, clip);
foreach (var (code, original) in source)
{
    if (original.PlayerKeyFrames.Length == 0) continue;
    var split = OverhaulPhaseAnimations.Split(code, original);
    var windup = split[OverhaulPhaseAnimations.Code(code, "windup")];
    foreach (int strike in WeaponTiming.StrikeDurations)
    {
        var swing = split[OverhaulPhaseAnimations.Code(code, "swing", strike)];
        var recovery = split[OverhaulPhaseAnimations.Code(code, "recovery")];
        Check(swing.PlayerKeyFrames[^1].EasingTime == strike + CombatRules.ContactWindow, code + ": contact window retiming");
        Check(JsonConvert.SerializeObject(windup.PlayerKeyFrames[^1].Elements) == JsonConvert.SerializeObject(swing.PlayerKeyFrames[0].Elements), code + ": windup must meet swing");
        Check(JsonConvert.SerializeObject(swing.PlayerKeyFrames[^1].Elements) == JsonConvert.SerializeObject(recovery.PlayerKeyFrames[0].Elements), code + ": swing must meet recovery");
        Check(recovery.PlayerKeyFrames[^1].EasingTime == 1000, code + ": normalized recovery");
        Check(recovery.Hold && recovery.PlayerKeyFrames.Length == 2, code + ": smooth held return without chamber replay");
    }
    foreach (var (phase, json) in split)
    {
        Check(json.CallbackFrames.Length == 0 && json.SoundFrames.Length == 0, phase + ": native attack callbacks are disabled");
        Check(json.PlayerKeyFrames.All(f => f.FOVMultiplier == 1 && f.BobbingAmplitude == 1), phase + ": no camera kick");
        Check(json.ToAnimation().TotalDuration.TotalMilliseconds > 0, phase + ": accepted by the installed CO library");
        for (int index = 1; index < json.PlayerKeyFrames.Length; index++)
        {
            var previous = json.PlayerKeyFrames[index - 1]; var current = json.PlayerKeyFrames[index];
            Check(current.EasingTime >= previous.EasingTime, phase + ": ordered keyframes");
            foreach (var (bone, values) in current.Elements)
                if (previous.Elements.TryGetValue(bone, out var before))
                    for (int axis = 3; axis < Math.Min(values.Length, before.Length); axis++)
                        if (values[axis].HasValue && before[axis].HasValue)
                            Check(Math.Abs(values[axis]!.Value - before[axis]!.Value) <= 180.01, phase + ": no whole wrist rotations");
        }
    }
}
Console.WriteLine($"PASS: {checks} Overhaul checks on {source.Count} native/Reanimated animation sources");
