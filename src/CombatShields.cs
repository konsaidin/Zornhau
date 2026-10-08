using Vintagestory.API.Common;

namespace Zornhau;

internal static class CombatShields
{
    internal static bool IsShield(CollectibleObject? item)
    {
        if (item == null) return false;
        if (item.Tool == EnumTool.Shield || item.Attributes?.KeyExists("shield") == true
            || item.Attributes?["isShield"].AsBool() == true) return true;
        for (Type? type = item.GetType(); type != null; type = type.BaseType)
            if (type.Name is "ItemShield" or "ItemShieldFromAttributes" or "VanillaShield") return true;
        // CO also recognizes these codes after replacing a shield's item class.
        string code = item.Code?.Path ?? "";
        return code is "shield" or "roundshield" || code.StartsWith("shield-", StringComparison.Ordinal)
            || code.StartsWith("roundshield-", StringComparison.Ordinal);
    }

    internal static bool Equipped(EntityPlayer player)
        => IsShield(player.LeftHandItemSlot.Itemstack?.Collectible) && !Stowed(player);

    internal static bool Stowed(EntityPlayer player, string? mode = null)
        => IsShield(player.LeftHandItemSlot.Itemstack?.Collectible) && CombatBalance.RequiresTwoHands(player, mode);

    internal static bool AllowsInput(Command command, bool weapon, bool shield, Phase phase)
        => weapon || shield && (command == Command.Block
            || phase == Phase.Block && command is Command.Aim or Command.KeepAlive or Command.Cancel);
}
