using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace WarbandCombat;

internal interface IWarbandDamage
{
    Direction AttackDirection { get; }
    bool Blocked { get; set; }
}

internal readonly record struct AttackProfile(float Power, float Reach, int Tier,
    EnumDamageType Type, float Knockback, int Durability, int ArmorPiercing = 0);

internal interface ICombatIntegration : IDisposable
{
    bool Animate(EntityPlayer player, Fighter fighter, long now, string? action);
    bool UsesPhysicalContacts(CollectibleObject item);
    void Frame(EntityPlayer player, Fighter fighter, long now);
    void RenderDiagnostics(EntityPlayer player, long now);
    string WeaponMode(EntityPlayer player);
    IWarbandDamage? GetValidatedDamage(DamageSource source);
    void AssetsReady();
    void ClearNativeBlock(EntityPlayer player);
}

internal static class OverhaulCompat
{
    internal static bool IsMelee(CollectibleObject? item)
    {
        for (Type? type = item?.GetType(); type != null; type = type.BaseType)
            if (type.FullName == "CombatOverhaul.Implementations.MeleeWeapon") return true;
        return item?.CollectibleBehaviors?.Any(b =>
        {
            for (Type? type = b.GetType(); type != null; type = type.BaseType)
                if (type.FullName == "CombatOverhaul.Implementations.MeleeWeaponBehavior") return true;
            return false;
        }) == true;
    }

    internal static ICombatIntegration? Create(ICoreAPI api)
    {
        if (!api.ModLoader.IsModEnabled("combatoverhaulfork")) return null;
        // Defer loading types that reference Overhaul until its optional assembly is present.
        var type = typeof(OverhaulCompat).Assembly.GetType("WarbandCombat.OverhaulBridge", true)!;
        try
        {
            return (ICombatIntegration)type.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, new object[] { api })!;
        }
        catch (TargetInvocationException exception)
        {
            throw new InvalidOperationException("Warband Combat: cannot initialize the Combat Overhaul adapter.",
                exception.InnerException ?? exception);
        }
    }
}
