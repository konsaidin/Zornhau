using System.Collections;
using System.Runtime.CompilerServices;
using CombatOverhaul.Animations;
using CombatOverhaul.Implementations;
using CombatOverhaul.Inputs;
using CombatOverhaul.RangedSystems;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Zornhau;

internal static class OverhaulUseInterrupt
{
    private sealed class State
    {
        internal long Until;
        internal bool AwaitRelease;
    }
    private static readonly ConditionalWeakTable<EntityPlayer, State> states = new();
    private static readonly AccessTools.FieldRef<BowServer, Dictionary<long, (InventoryBase, int)>> arrows
        = AccessTools.FieldRefAccess<BowServer, Dictionary<long, (InventoryBase, int)>>("ArrowSlots");

    internal static void Patch(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(ActionsManagerPlayerBehavior), "HandleActionEvent"),
            prefix: new HarmonyMethod(typeof(OverhaulUseInterrupt), nameof(BeforeAction)));
        harmony.Patch(AccessTools.Method(typeof(BowServer), nameof(BowServer.Reload)),
            prefix: new HarmonyMethod(typeof(OverhaulUseInterrupt), nameof(BeforeReload)));
    }

    internal static void Cancel(EntityPlayer player)
    {
        var state = states.GetOrCreateValue(player);
        state.Until = player.World.ElapsedMilliseconds + CombatRules.HitStun;
        state.AwaitRelease = player.Api?.Side == EnumAppSide.Client && player.Controls.RightMouseDown;
        CancelHand(player, true); CancelHand(player, false);
    }

    private static void CancelHand(EntityPlayer player, bool mainHand)
    {
        var slot = mainHand ? player.RightHandItemSlot : player.LeftHandItemSlot;
        if (slot.Itemstack?.Collectible is not BowItem bow) return;
        if (player.Api?.Side == EnumAppSide.Server)
        {
            if (bow.ServerLogic != null) arrows(bow.ServerLogic).Remove(player.EntityId);
            return;
        }
        var behavior = player.GetBehavior<ActionsManagerPlayerBehavior>();
        if (bow.ClientLogic == null || behavior == null) return;
        int state = behavior.GetState(mainHand);
        bow.ClientLogic.OnDeselected(player, mainHand, ref state);
        behavior.SetState((int)BowState.Unloaded, mainHand);
        string category = mainHand ? "main" : "mainOffhand";
        var fp = player.GetBehavior<FirstPersonAnimationsBehavior>();
        fp?.Stop(category); fp?.PlayReadyAnimation(mainHand);
        var tp = player.GetBehavior<ThirdPersonAnimationsBehavior>();
        tp?.Stop(category); tp?.PlayReadyAnimation(mainHand);
    }

    private static bool BeforeAction(ActionsManagerPlayerBehavior __instance, ActionEventData eventData,
        int itemId, ref bool __result)
    {
        var player = (EntityPlayer)__instance.entity;
        if (CombatShields.Stowed(player) && player.LeftHandItemSlot.Itemstack?.Collectible.Id == itemId)
        { __result = false; return false; }
        if (!states.TryGetValue(player, out var state)
            || !(player.RightHandItemSlot.Itemstack?.Collectible is BowItem
                || player.LeftHandItemSlot.Itemstack?.Collectible is BowItem)) return true;
        if (eventData.Action.Action == EnumEntityAction.RightMouseDown
            && eventData.Action.State == ActionState.Released && state.AwaitRelease)
        {
            state.AwaitRelease = false;
            __result = false; return false;
        }
        if (state.AwaitRelease || player.World.ElapsedMilliseconds < state.Until)
        { __result = false; return false; }
        return true;
    }

    private static bool BeforeReload(IServerPlayer player, ref bool __result)
    {
        if (!states.TryGetValue(player.Entity, out var state)
            || player.Entity.World.ElapsedMilliseconds >= state.Until) return true;
        __result = false; return false;
    }
}
