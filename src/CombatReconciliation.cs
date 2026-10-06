namespace WarbandCombat;

// Snapshots acknowledge input, not its arrival on the client. Replay commands
// still in flight instead of discarding authority whenever a button is held.
internal sealed class CombatReconciliation
{
    private readonly List<Input> pending = new();
    private long revision;
    private int acknowledged;
    private sealed record Input(int Sequence, Command Command, Direction Direction, long At,
        WeaponTiming Timing, bool Allowed);
    internal int PendingCount => pending.Count;

    internal void Track(int sequence, Command command, Direction direction, long at,
        WeaponTiming timing, bool allowed)
    {
        pending.Add(new(sequence, command, direction, at, timing, allowed));
        if (pending.Count > 128) pending.RemoveAt(0);
    }

    internal bool Apply(StatePacket packet, Fighter fighter, long now, ref int attackId)
    {
        if (packet.Revision <= revision || packet.Sequence < acknowledged
            || !Enum.IsDefined((Phase)packet.Phase) || !Enum.IsDefined((Direction)packet.Direction)) return false;
        revision = packet.Revision; acknowledged = packet.Sequence;
        pending.RemoveAll(input => input.Sequence <= acknowledged);
        int age = CombatPresentation.SnapshotAge(now, packet.EchoSentAt, packet.InputReceivedAt, packet.ServerSentAt);
        // An unsolicited stun starts at the server event, rather than at the
        // old input. Only its downlink travel may consume the penalty.
        if (packet.AuthoritativeClock) age /= 2;
        long anchor = now - age;
        int consumedAttack = fighter.HitDelivered && fighter.Phase is Phase.Swing or Phase.Recovery ? attackId : -1;
        bool consumed = consumedAttack == packet.AttackId;
        Phase phase = (Phase)packet.Phase;
        if (phase == Phase.Swing && (packet.ContactConsumed || consumed)) phase = Phase.Recovery;
        fighter.Restore(phase, (Direction)packet.Direction, anchor,
            packet.Elapsed, packet.HitIn, packet.EndsIn, packet.Charge,
            packet.RecoveryAttackPose || phase == Phase.Recovery && consumed,
            packet.ContactEndsIn, packet.SprintIn);
        attackId = packet.AttackId;
        foreach (var input in pending)
        {
            long at = Math.Clamp(input.At, anchor, now);
            fighter.Advance(at);
            if (input.Allowed && fighter.Apply(input.Command, input.Direction, at, input.Timing)
                && input.Command == Command.Begin) attackId = input.Sequence;
        }
        if (attackId == consumedAttack && fighter.Phase == Phase.Swing)
            fighter.Restore(Phase.Recovery, fighter.Direction, now, (int)(now - fighter.Started),
                (int)(fighter.HitAt - now), (int)(fighter.Ends - now), fighter.Charge, true,
                (int)(fighter.ContactEnds - now), (int)Math.Max(0, fighter.SprintUntil - now));
        fighter.Advance(now);
        return true;
    }

    internal void Reset() { pending.Clear(); revision = 0; acknowledged = 0; }
}
