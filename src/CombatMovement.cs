using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace Zornhau;

// Check at the physics speed calculation too: an incoming control update must
// not re-enable sprint between the combat system's ticks.
internal static class CombatMovement
{
    private static readonly ConditionalWeakTable<EntityPlayer, Fighter> fighters = new();

    internal static void Bind(EntityPlayer player, Fighter fighter)
    {
        if (fighters.TryGetValue(player, out var current) && ReferenceEquals(current, fighter)) return;
        fighters.Remove(player); fighters.Add(player, fighter);
    }

    internal static void Patch(Harmony harmony)
        => harmony.Patch(AccessTools.Method(typeof(EntityAgent), nameof(EntityAgent.GetWalkSpeedMultiplier)),
            prefix: new HarmonyMethod(typeof(CombatMovement), nameof(BeforeWalkSpeed)));

    private static void BeforeWalkSpeed(EntityAgent __instance)
    {
        if (__instance is EntityPlayer player && fighters.TryGetValue(player, out var fighter))
            Enforce(player, fighter, player.World.ElapsedMilliseconds);
    }

    internal static void Enforce(EntityPlayer player, Fighter fighter, long now)
    {
        if (!fighter.SprintLocked(now)) return;
        player.Controls.Sprint = false;
        player.ServerControls.Sprint = false;
        player.CurrentControls &= ~EnumEntityActivity.SprintMode;
    }
}
