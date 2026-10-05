using Vintagestory.API.Common;
using System.Runtime.CompilerServices;

namespace WarbandCombat;

internal static class CombatAnimations
{
    private static readonly ConditionalWeakTable<EntityPlayer, CombatClipSelection> selections = new();
    [ThreadStatic] private static int ownedStartDepth;
    [ThreadStatic] private static float? ownedSpeed;

    internal static bool OnLocalAnimationStart(ref AnimationMetaData animation, ref EnumHandling handling)
    {
        if (ownedStartDepth > 0 && ownedSpeed.HasValue && animation.Code?.StartsWith("warband-", StringComparison.Ordinal) == true)
        {
            animation = animation.Clone(); animation.AnimationSpeed = ownedSpeed.Value;
        }
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

    private static void StartRetimedAnimation(EntityPlayer player, string code, float speed)
    {
        if (player.Properties?.Client?.AnimationsByMetaCode?.TryGetValue(code, out var meta) != true)
        { StartOwnedAnimation(player.AnimManager, code); return; }
        meta = meta!.Clone(); meta.AnimationSpeed = speed;
        float? previous = ownedSpeed; ownedSpeed = speed; ownedStartDepth++;
        try { player.AnimManager.StartAnimation(meta); }
        finally { ownedStartDepth--; ownedSpeed = previous; }
    }
    internal static readonly string[] Codes =
        (from prefix in new[] { "warband", "warband-body" }
         from action in new[] { "windup", "swing", "block", "recovery" }
         from direction in Enum.GetValues<Direction>()
         select $"{prefix}-{action}-{direction.ToString().ToLowerInvariant()}").Append("raiseshield-left").ToArray();

    internal static void Update(EntityPlayer player, Fighter fighter, long now, CombatPresentation? presentation = null,
        ICombatIntegration? integration = null, ICombatAnimation? standalone = null)
    {
        string? action = presentation != null ? presentation.Action(fighter, now)
            : CombatPresentation.CombatAnimationsAction(fighter, now);
        bool native = integration?.Animate(player, fighter, now, action) == true
            || standalone?.Animate(player, fighter, now, action) == true;
        string prefix = native ? "warband-body" : "warband";
        string? desired = !native && action == "block" && fighter.ShieldGuard ? "raiseshield-left"
            : action == null ? null : $"{prefix}-{action}-{fighter.Direction.ToString().ToLowerInvariant()}";
        if (!selections.GetOrCreateValue(player).Change(desired)) return;
        foreach (string code in Codes)
            if (code != desired && player.AnimManager.IsAnimationActive(code)) player.AnimManager.StopAnimation(code);
        // One-shot clips may finish before server recovery ends. Do not replay
        // them just because the animator no longer reports them as active.
        if (desired != null)
        {
            if (!native && desired.StartsWith("warband-", StringComparison.Ordinal) && fighter.Timing.PhysicalContacts)
            {
                double remaining = action switch
                {
                    "windup" => Math.Max(40, fighter.Started + fighter.Timing.Windup - now),
                    "swing" => Math.Max(40, fighter.ContactEnds - now),
                    "recovery" => Math.Max(40, fighter.Ends - now),
                    _ => CombatRules.GuardRaise
                };
                int frames = player.AnimManager.Animator?.GetAnimationState(desired)?.Animation.QuantityFrames ?? 15;
                StartRetimedAnimation(player, desired, (float)(frames / 30d * 1000 / remaining));
            }
            else StartOwnedAnimation(player.AnimManager, desired);
        }
    }
}
