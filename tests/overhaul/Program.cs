using CombatOverhaul.Animations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CombatOverhaul.Implementations;
using WarbandCombat;

int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var source = JsonConvert.DeserializeObject<Dictionary<string, AnimationJson>>(
    File.ReadAllText(Path.Combine(root, "third-party/reanimated/clips.json")))!;
var bundled = source.Keys.ToHashSet();
foreach (var (package, domain) in new[] { ("combatoverhaulfork_0.18.8", "combatoverhaul"), ("armoryfork_1.11.3", "armory") })
foreach (var file in Directory.GetFiles(Path.Combine(root, $".artifacts/compat/unpacked/{package}/assets/{domain}/config/animations"), "*.json", SearchOption.AllDirectories))
    foreach (var (code, clip) in JsonConvert.DeserializeObject<Dictionary<string, AnimationJson>>(File.ReadAllText(file))!)
        source.TryAdd(code.Contains(':') ? code : domain + ":" + code, clip);
int twoHandedStances = 0;
var coveredWeapons = new HashSet<string>();
var items = Directory.GetFiles(Path.Combine(root,
    ".artifacts/compat/unpacked/armoryfork_1.11.3/assets/armory/itemtypes/weapons"), "*.json")
    .Concat(Directory.GetFiles(Path.Combine(root,
        ".artifacts/compat/unpacked/combatoverhaulfork_0.18.8/assets/game/itemtypes"), "*.json", SearchOption.AllDirectories));
foreach (string file in items)
{
    var item = JObject.Parse(File.ReadAllText(file));
    foreach (var property in item.Descendants().OfType<JProperty>().Where(p => p.Name == "TwoHandedStance"))
    {
        var stance = property.Value.ToObject<StanceStats>()!;
        if (!stance.CanAttack) continue;
        // Resolve the wildcard animation property normally expanded by the game.
        if (property.Value["AttackAnimationByType"] is JObject variants)
            stance.AttackAnimation = variants.Properties().First().Value.ToObject<Dictionary<string, string[]>>()!;
        twoHandedStances++;
        coveredWeapons.Add(Path.GetFileNameWithoutExtension(file));
        foreach (Direction direction in Enum.GetValues<Direction>())
        foreach (bool block in new[] { false, true })
        {
            string? code = OverhaulAnimationResolver.TwoHanded(stance, direction, block,
                Path.GetFileNameWithoutExtension(file), source.ContainsKey);
            Check(code != null && !code.StartsWith("warbandcombat:"), file + ": native two-handed coverage " + direction);
            var frames = source[code!].PlayerKeyFrames;
            Check(frames.Any(f => f.Elements.ContainsKey("UpperArmL") || f.Elements.ContainsKey("LowerArmL")),
                code + ": chosen two-handed animation must drive the left arm");
            string? minimal = OverhaulAnimationResolver.TwoHanded(stance, direction, block,
                Path.GetFileNameWithoutExtension(file), c => bundled.Contains(c)
                    || c.StartsWith("combatoverhaul:") && source.ContainsKey(c));
            Check(minimal != null, file + ": CO-only fallback remains two-handed");
        }
    }
}
foreach (string weapon in new[] { "sword-great", "sword-long", "pike", "quarterstaff", "poleaxe", "halberd", "axe-long", "club" })
    Check(coveredWeapons.Contains(weapon), "Check the installed two-handed definition: " + weapon);
Console.WriteLine($"Covered {twoHandedStances} CO/Armory two-handed weapon/mode stances, four attacks and four guards each");
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
