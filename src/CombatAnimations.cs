using Vintagestory.API.Common;

namespace WarbandCombat;

internal static class CombatAnimations
{
    internal static readonly string[] Codes =
        (from action in new[] { "windup", "swing", "block", "recovery" }
         from direction in Enum.GetValues<Direction>()
         select $"warband-{action}-{direction.ToString().ToLowerInvariant()}").ToArray();

    internal static void Update(EntityPlayer player, Fighter fighter, long now)
    {
        string? action = fighter.Phase switch
        {
            Phase.Windup => "windup",
            Phase.Swing => now < fighter.HitAt - fighter.Timing.Strike ? "windup" : "swing",
            Phase.Block => "block",
            Phase.Recovery when fighter.RecoveryAttackPose => "recovery",
            _ => null
        };
        string? desired = action == null ? null : $"warband-{action}-{fighter.Direction.ToString().ToLowerInvariant()}";
        foreach (string code in Codes)
            if (code != desired && player.AnimManager.IsAnimationActive(code)) player.AnimManager.StopAnimation(code);
        if (desired != null && !player.AnimManager.IsAnimationActive(desired)) player.AnimManager.StartAnimation(desired);
    }
}
