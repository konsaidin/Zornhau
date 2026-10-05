using CombatOverhaul.Implementations;

namespace WarbandCombat;

internal static class OverhaulAnimationResolver
{
    internal static string Guard(Direction direction, bool twoHanded = false)
        => CombatGuardAnimations.Code(direction, twoHanded);

    internal static string? TwoHanded(StanceStats stance, Direction direction, bool block,
        string weapon, Func<string, bool> available)
    {
        bool Valid(string? code) => code != null && code.Contains("-2h", StringComparison.Ordinal) && available(code);
        if (block)
        {
            if (Valid(stance.ParryAnimation)) return stance.ParryAnimation;
            if (Valid(stance.BlockAnimation)) return stance.BlockAnimation;
        }
        else
        {
            string key = direction switch { Direction.Overhead => "Top", Direction.Thrust => "Bottom", _ => direction.ToString() };
            if (stance.AttackAnimation.TryGetValue(key, out var exact))
                foreach (string clip in exact) if (Valid(clip)) return clip;
            if (direction is Direction.Right or Direction.Left)
                foreach (string diagonal in new[] { "Bottom" + key, "Top" + key })
                    if (stance.AttackAnimation.TryGetValue(diagonal, out var clips))
                        foreach (string clip in clips) if (Valid(clip)) return clip;
        }

        // Infer the animation family from the actual stance, not the item name:
        // hammer/axe modes can deliberately share a poleaxe or long-axe grip.
        var families = new List<string>();
        foreach (string? pose in new[] { stance.ReadyAnimation, stance.IdleAnimation })
            if (pose != null)
                foreach (string marker in new[] { "-ready-2h", "-idle-2h" })
                {
                    int index = pose.IndexOf(marker, StringComparison.Ordinal);
                    if (index > 0) families.Add(pose[..index]);
                }
        string family = weapon switch
        {
            _ when weapon.Contains("great") => "sword-great",
            _ when weapon.Contains("sword") => "sword-long",
            _ when weapon.Contains("quarterstaff") => "quarterstaff",
            _ when weapon.Contains("halberd") => "halberd",
            _ when weapon.Contains("poleaxe") || weapon.Contains("hammer") => "poleaxe",
            _ when weapon.Contains("axe") => "long-axe",
            _ when weapon.Contains("pike") => "pike",
            _ when weapon.Contains("spear") || weapon.Contains("trident") || weapon.Contains("javelin") => "spear",
            _ => "club"
        };
        families.Add("armory:" + family); families.Add("combatoverhaul:" + family);
        // Missing pole directions use a two-handed pole grip. Swords use a sword
        // grip. The bundled Reanimated greatsword covers CO-only installs too.
        bool pole = family is "pike" or "spear" or "poleaxe" or "halberd" or "quarterstaff" or "long-axe";
        families.Add(pole ? "armory:poleaxe" : "armory:sword-great");
        families.Add("armory:sword-great");
        families.Add("combatoverhaul:sword-great");
        foreach (string prefix in families.Distinct())
        {
            string[] endings = block ? new[] { "-parry-2h" } : direction switch
            {
                Direction.Overhead => new[] { "-slash-2h-top", "-strike-2h-top", "-hit-2h-top" },
                Direction.Thrust => new[] { "-thrust-2h-bottom", "-thrust-2h-1" },
                Direction.Right => new[] { "-slash-2h-right", "-strike-2h-right", "-hit-2h-bottomright", "-strike-2h-1", "-slash-2h-1" },
                _ => new[] { "-slash-2h-left", "-strike-2h-left", "-hit-2h-bottomleft", "-strike-2h-2", "-slash-2h-2" }
            };
            foreach (string ending in endings)
                foreach (string suffix in new[] { "", "-1" })
                    if (Valid(prefix + ending + suffix)) return prefix + ending + suffix;
        }
        return null;
    }
}
