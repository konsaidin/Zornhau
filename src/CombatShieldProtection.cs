using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Zornhau;

internal static class CombatShieldProtection
{
    internal static void Patch(Harmony harmony)
        => harmony.Patch(AccessTools.Method(typeof(ModSystemWearableStats), "applyShieldProtection"),
            prefix: new HarmonyMethod(typeof(CombatShieldProtection), nameof(NativeProtection)));

    private static bool NativeProtection(IPlayer player, float damage, ref float __result)
    {
        if (!CombatShields.Stowed(player.Entity)) return true;
        __result = damage; return false;
    }
}
