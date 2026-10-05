using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace WarbandCombat;

// The committed engine audit and policy generate this same data for both
// sides. Never accept damage/timings/class/handedness from a client packet.
internal sealed class WeaponBalanceProfile
{
    public string Mode = "", Stance = "one", Class = "", Family = "";
    public bool CanAttack;
    public int Windup, Strike, Recovery;
    public float Power;
    public float[] SourcePower = [];
    internal WeaponTiming Timing => new(Windup, Strike, Recovery) { PhysicalContacts = true };
}

internal static class CombatBalance
{
    private sealed class Definition(WeaponBalanceProfile[] profiles)
    {
        internal readonly WeaponBalanceProfile[] Profiles = profiles;
        internal readonly string DefaultMode = profiles[0].Mode;
    }
    private static readonly ConditionalWeakTable<CollectibleObject, Definition> definitions = new();

    internal static void Load(ICoreAPI api)
    {
        var all = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, WeaponBalanceProfile[]>>>(
            api.Assets.Get("warbandcombat:config/balance-profiles.json").ToText())!;
        var source = all[api.ModLoader.IsModEnabled("combatoverhaulfork") ? "co" : "vanilla"];
        int count = 0;
        foreach (var item in api.World.Items)
            if (item?.Code != null && source.TryGetValue(item.Code.ToString(), out var profiles))
            { Register(item, profiles); count += profiles.Length; }
        api.Logger.Notification("[Warband Combat] Loaded {0} balanced weapon/mode/grip profiles.", count);
    }

    internal static void Register(CollectibleObject item, WeaponBalanceProfile[] profiles)
    {
        foreach (var profile in profiles)
            if (profile.Windup <= 0 || profile.Strike <= 0 || profile.Recovery <= 0
                || !WeaponTiming.StrikeDurations.Contains(profile.Strike) || !float.IsFinite(profile.Power)
                || profile.Power <= 0 || profile.SourcePower.Length != 4
                || profile.SourcePower.Any(p => !float.IsFinite(p) || p <= 0))
                throw new InvalidDataException("Invalid weapon balance profile: " + item.Code);
        definitions.Remove(item); definitions.Add(item, new(profiles));
    }

    internal static WeaponBalanceProfile? For(EntityPlayer player, string? mode = null)
    {
        if (player.RightHandItemSlot.Itemstack?.Collectible is not { } item
            || !definitions.TryGetValue(item, out var definition)) return null;
        mode ??= player.WatchedAttributes.GetString("warbandWeaponMode", "");
        if (mode.Length == 0) mode = definition.DefaultMode;
        var profiles = definition.Profiles.Where(p => p.Mode == mode).ToArray();
        string offhand = player.LeftHandItemSlot.Itemstack?.Collectible.Code.ToString() ?? "";
        if (offhand.Length == 0 && profiles.FirstOrDefault(p => p.Stance == "two") is { } two) return two;
        if (offhand.Length > 0 && profiles.FirstOrDefault(p => p.Stance.StartsWith("dual:", StringComparison.Ordinal)
            && WildcardUtil.Match(p.Stance[5..], offhand)) is { } dual) return dual;
        return profiles.FirstOrDefault(p => p.Stance == "one");
    }

    internal static WeaponTiming? DefaultTiming(CollectibleObject item)
        => definitions.TryGetValue(item, out var definition)
            ? (definition.Profiles.FirstOrDefault(p => p.Mode == definition.DefaultMode && p.Stance == "one" && p.CanAttack)
                ?? definition.Profiles.FirstOrDefault(p => p.CanAttack))?.Timing : null;

    internal static bool CanAttack(EntityPlayer player, string? mode = null)
    {
        if (player.RightHandItemSlot.Itemstack?.Collectible is not { } item
            || !definitions.TryGetValue(item, out _)) return true;
        return For(player, mode)?.CanAttack == true;
    }

    internal static float Power(EntityPlayer player, Direction direction, float original, string? mode = null)
    {
        var profile = For(player, mode);
        if (profile == null) return original;
        if (!profile.CanAttack) return 0;
        return original * profile.Power / profile.SourcePower[(int)direction];
    }

    internal static float DirectionBonus(EnumTool? tool, Direction direction)
        => direction == Direction.Overhead ? 1.12f : direction == Direction.Thrust && tool == EnumTool.Spear ? 1.15f : 1;
}
