using Vintagestory.API.Common;

namespace WarbandCombat;

internal static class CombatFeedback
{
    internal static AssetLocation SwingSound => new("game:sounds/effect/swoosh");

    internal static AssetLocation ContactSound(ContactKind kind, CollectibleObject? guard, bool heavy)
    {
        if (kind == ContactKind.Hit) return new("game:sounds/player/hackingspearhit");
        if (kind == ContactKind.ShieldBreak) return new("game:sounds/effect/toolbreak");
        string strength = heavy ? "Heavy" : "Light";
        string? configured = kind == ContactKind.Shield
            ? guard?.Attributes?["shield"]?["blockSound" + strength].AsString() : null;
        if (!string.IsNullOrEmpty(configured))
        {
            var location = new AssetLocation(configured);
            if (!location.Path.StartsWith("sounds/")) location.Path = "sounds/" + location.Path;
            return location;
        }
        string code = guard?.Code?.Path ?? "";
        bool wood = kind == ContactKind.Shield && (code.Contains("wood") || code.Contains("crude")
            || code.Contains("wicker"));
        return new("game:sounds/held/shieldblock-" + (wood ? "wood" : "metal") + "-" + strength.ToLowerInvariant());
    }

    internal static float Impulse(ContactKind kind, bool attacker) => kind switch
    {
        ContactKind.Hit => attacker ? .025f : 0, // The victim already gets the real hurt effect.
        ContactKind.Parry => attacker ? .045f : .025f,
        ContactKind.Shield => attacker ? .05f : .035f,
        ContactKind.ShieldBreak => .07f,
        _ => 0
    };
}
