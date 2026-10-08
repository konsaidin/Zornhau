using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace Zornhau;

// Cancel an in-progress use, never its normal completion callback: bows fire
// and healing items apply their effect from Stop, not from Cancel.
internal static class CombatUseInterrupt
{
    private sealed class State
    {
        internal ItemStack? CancelledStack;
        internal long CancelledBegin, Until;
        internal bool AwaitRelease;
    }
    private static readonly ConditionalWeakTable<EntityAgent, State> states = new();
    internal static Action<EntityPlayer>? ServerHit;

    internal static void Patch(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.OnHeldUseStart)),
            prefix: new HarmonyMethod(typeof(CombatUseInterrupt), nameof(BeforeStart)));
        harmony.Patch(AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.OnHeldUseStop)),
            prefix: new HarmonyMethod(typeof(CombatUseInterrupt), nameof(BeforeStop)));
        harmony.Patch(AccessTools.Method(typeof(EntityPlayer), nameof(EntityPlayer.OnHurt)),
            postfix: new HarmonyMethod(typeof(CombatUseInterrupt), nameof(AfterHurt)));
    }

    private static void AfterHurt(Entity __instance, DamageSource __0, float damage)
    {
        if (__instance is EntityPlayer player && player.Api?.Side == EnumAppSide.Server && damage > 0
            && __0.Type is EnumDamageType.SlashingAttack or EnumDamageType.PiercingAttack or EnumDamageType.BluntAttack)
            ServerHit?.Invoke(player);
    }

    internal static bool IsInterruptible(CollectibleObject? item)
        => item is ItemBow or ItemPoultice
            || item?.GetCollectibleInterface<ICanHealCreature>() != null;

    internal static void Cancel(EntityPlayer player)
    {
        var state = states.GetOrCreateValue(player);
        state.Until = player.World.ElapsedMilliseconds + CombatRules.HitStun;
        state.AwaitRelease = player.Api?.Side == EnumAppSide.Client && player.Controls.RightMouseDown;
        var slot = player.RightHandItemSlot;
        if (IsInterruptible(slot?.Itemstack?.Collectible) && player.Controls.HandUse != EnumHandInteract.None)
        {
            state.CancelledStack = slot!.Itemstack;
            state.CancelledBegin = player.Controls.UsingBeginMS;
            // EntityPlayer.TryStopHandAction also invokes Stop afterwards.
            // Call Cancel directly so an almost-complete use cannot heal/fire.
            float elapsed = (player.World.ElapsedMilliseconds - player.Controls.UsingBeginMS) / 1000f;
            slot.Itemstack!.Collectible.OnHeldUseCancel(elapsed, slot, player,
                player.Player?.CurrentBlockSelection, player.Player?.CurrentEntitySelection,
                EnumItemUseCancelReason.MovedAway);
            player.Controls.HandUse = EnumHandInteract.None;
        }
    }

    internal static void Release(EntityPlayer player)
    {
        if (states.TryGetValue(player, out var state)) state.AwaitRelease = false;
    }

    private static bool BeforeStart(ItemSlot slot, EntityAgent byEntity, EnumHandInteract useType,
        ref EnumHandHandling handling)
    {
        if (!IsInterruptible(slot.Itemstack?.Collectible) || !states.TryGetValue(byEntity, out var state)) return true;
        if (byEntity.World.ElapsedMilliseconds < state.Until || state.AwaitRelease)
        { handling = EnumHandHandling.PreventDefault; return false; }
        state.CancelledStack = null;
        return true;
    }

    private static bool BeforeStop(ItemSlot slot, EntityAgent byEntity)
        => !states.TryGetValue(byEntity, out var state)
            || !ReferenceEquals(slot.Itemstack, state.CancelledStack)
            || byEntity.Controls.UsingBeginMS != state.CancelledBegin;
}
