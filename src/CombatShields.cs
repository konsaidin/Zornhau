using Vintagestory.API.Common;

namespace WarbandCombat;

internal static class CombatShields
{
    internal static bool IsShield(CollectibleObject? item) => item != null
        && (item.Tool == EnumTool.Shield || item.Attributes?.KeyExists("shield") == true
            || item.GetType().Name is "ItemShield" or "VanillaShield");

    internal static bool Equipped(EntityPlayer player)
        => IsShield(player.LeftHandItemSlot.Itemstack?.Collectible);
}
