using Vintagestory.API.Common;
using System.Runtime.CompilerServices;

namespace WarbandCombat;

internal static class CombatAnimations
{
    private static readonly ConditionalWeakTable<EntityPlayer, CombatClipSelection> selections = new();
    [ThreadStatic] private static int ownedStartDepth;

    internal static bool OnLocalAnimationStart(ref AnimationMetaData animation, ref EnumHandling handling)
    {
        if (ownedStartDepth == 0 && (animation.Code?.StartsWith("warband-", StringComparison.Ordinal) == true
            || animation.Animation?.StartsWith("warband-", StringComparison.Ordinal) == true))
        {
            handling = EnumHandling.PreventSubsequent;
            return false;
        }
        return true;
    }

    internal static void StartOwnedAnimation(IAnimationManager manager, string code)
    {
        ownedStartDepth++;
        try { manager.StartAnimation(code); }
        finally { ownedStartDepth--; }
    }
    internal static readonly string[] Codes =
        (from prefix in new[] { "warband", "warband-body" }
         from action in new[] { "windup", "swing", "block", "recovery" }
         from direction in Enum.GetValues<Direction>()
         select $"{prefix}-{action}-{direction.ToString().ToLowerInvariant()}").ToArray();

    internal static void Update(EntityPlayer player, Fighter fighter, long now, CombatPresentation? presentation = null,
        ICombatIntegration? integration = null)
    {
        string? action = presentation != null ? presentation.Action(fighter, now)
            : CombatPresentation.CombatAnimationsAction(fighter, now);
        bool native = integration?.Animate(player, fighter, now, action) == true;
        string prefix = native ? "warband-body" : "warband";
        string? desired = action == null ? null : $"{prefix}-{action}-{fighter.Direction.ToString().ToLowerInvariant()}";
        if (!selections.GetOrCreateValue(player).Change(desired)) return;
        foreach (string code in Codes)
            if (code != desired && player.AnimManager.IsAnimationActive(code)) player.AnimManager.StopAnimation(code);
        // One-shot clips may finish before server recovery ends. Do not replay
        // them just because the animator no longer reports them as active.
        if (desired != null) StartOwnedAnimation(player.AnimManager, desired);
    }
}
