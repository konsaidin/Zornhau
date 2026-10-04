namespace WarbandCombat;

// Network corrections must not replay a committed swing on the owning client.
// A new accepted Begin is the only way to open another attack presentation.
internal sealed class CombatPresentation
{
    private int attackStage;
    internal void Begin() => attackStage = 0;
    internal void Reset() => attackStage = 0;

    internal string? Action(Fighter fighter, long now)
    {
        string? action = CombatAnimationsAction(fighter, now);
        if (action == "block") return action;
        int stage = action switch { "windup" => 1, "swing" => 2, "recovery" => 3, _ => 4 };
        if (stage < attackStage)
            return attackStage switch { 2 => "swing", 3 => "recovery", _ => null };
        attackStage = stage;
        return action;
    }

    internal static string? CombatAnimationsAction(Fighter fighter, long now) => fighter.Phase switch
    {
        Phase.Windup => "windup",
        Phase.Swing => now < fighter.StrikeStarts ? "windup" : "swing",
        Phase.Block => "block",
        Phase.Recovery when fighter.RecoveryAttackPose => "recovery",
        _ => null
    };

    // Project server deadlines onto the time at which this input was sent.
    // Contact packets then traverse the same uplink as the initiating input.
    // Client timestamps are echoed only; the server never uses them for damage.
    internal static int SnapshotAge(long now, long clientSentAt, long serverReceivedAt, long serverSentAt)
        => clientSentAt <= 0 ? 0 : (int)Math.Clamp(now - clientSentAt - (serverSentAt - serverReceivedAt), 0, 2000);
}

internal sealed class CombatClipSelection
{
    private string? current;
    internal bool Change(string? desired)
    {
        if (current == desired) return false;
        current = desired;
        return true;
    }
}
