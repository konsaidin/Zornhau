using Vintagestory.API.Common;

namespace NaturalMotion;

internal static class HeldWeapons
{
    internal static bool IsWeapon(CollectibleObject? item)
    {
        if (item == null) return false;
        if (item.Tool is EnumTool.Sword or EnumTool.Spear or EnumTool.Axe or EnumTool.Knife or EnumTool.Shield
            || item.Attributes?["isWeapon"].AsBool() == true || item.Attributes?["isShield"].AsBool() == true
            || item.Attributes?.KeyExists("shield") == true) return true;
        static bool WeaponType(Type? type)
        {
            for (; type != null; type = type.BaseType)
                if (type.FullName is "CombatOverhaul.Implementations.MeleeWeapon" or "CombatOverhaul.Implementations.MeleeWeaponBehavior"
                    || type.Name is "ItemBow" or "ItemCrossbow" or "ItemShield" or "ItemShieldFromAttributes" or "VanillaShield") return true;
            return false;
        }
        if (WeaponType(item.GetType()) || item.CollectibleBehaviors?.Any(b => b != null && WeaponType(b.GetType())) == true) return true;
        // Include modded weapon families without requiring their assemblies.
        string family = (item.Code?.Path ?? "").Split('-')[0];
        return family is "sword" or "sabre" or "claymore" or "falx" or "blade" or "longsword"
            or "spear" or "javelin" or "trident" or "pike" or "poleaxe" or "halberd" or "quarterstaff"
            or "mace" or "club" or "warhammer" or "battleaxe" or "axe" or "hatchet" or "dagger" or "knife"
            or "bow" or "crossbow" or "shield" or "roundshield";
    }
}
