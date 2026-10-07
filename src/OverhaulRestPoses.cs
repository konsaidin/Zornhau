using CombatOverhaul.Animations;

namespace WarbandCombat;

// Native TP idle variants lower some one-handed weapons almost to the seams.
// With locomotion removed that becomes the permanent armed pose. Use the
// weapon's authored FP idle grip in TP as well, preserving idle timing.
internal static class OverhaulRestPoses
{
    internal static IEnumerable<(string Code, AnimationJson Pose)> Replacements(IReadOnlyDictionary<string, AnimationJson> sources)
    {
        foreach (string code in sources.Keys)
        {
            if (!code.EndsWith("-idle-tp", StringComparison.Ordinal) || code.Contains("-2h", StringComparison.Ordinal)
                || !sources.TryGetValue(code[..^3], out var idle) || idle.PlayerKeyFrames.Length == 0) continue;
            string family = code[(code.IndexOf(':') + 1)..].Split('-')[0];
            if (family is not ("sword" or "axe" or "knife" or "club" or "mace" or "spear" or "dagger"
                or "sabre" or "hatchet" or "javelin")) continue;
            // A clip driving the left hand is a joint grip/offhand pose, even
            // when its code omits an explicit 2h suffix.
            if (!idle.PlayerKeyFrames.Any(f => f.Elements.ContainsKey("UpperArmR"))
                || idle.PlayerKeyFrames.Any(f => f.Elements.ContainsKey("UpperArmL") || f.Elements.ContainsKey("LowerArmL"))) continue;
            yield return (code, idle);
        }
    }
}
