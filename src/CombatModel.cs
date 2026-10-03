namespace WarbandCombat;

public enum Direction { Overhead, Right, Thrust, Left }
public enum Phase { Idle, Windup, Swing, Recovery, Block }
public enum Command { Begin, Release, Block, Cancel, Aim, KeepAlive }

public readonly record struct WeaponTiming(int Windup, int Strike, int Recovery)
{
    public static WeaponTiming Sword => new(280, 140, 360);
}

public static class CombatRules
{
    public const int GuardRaise = 100;
    public const int GuardRelease = 80;
    public const int FeintRecovery = 120;
    public const int ParryStun = 480;
    public const int ContactWindow = 50;
    public static bool RejectVanillaPvpMelee(bool pvpEnabled, bool directional, bool playerSource, bool melee)
        => pvpEnabled && !directional && playerSource && melee;

    public static bool UseToolInteraction(bool workTool, bool blockTarget, bool corpseTarget,
        bool livingTarget, bool combatInProgress)
        => workTool && !livingTarget && !combatInProgress && (blockTarget || corpseTarget);
}

// Milliseconds come from the server clock. Clients never submit damage or charge time.
public sealed class Fighter
{
    public Phase Phase { get; private set; }
    public Direction Direction { get; private set; }
    public long Started { get; private set; }
    public long HitAt { get; private set; }
    public long ContactEnds { get; private set; }
    public long Ends { get; private set; }
    public long LastInput { get; private set; }
    public float Charge { get; private set; }
    public bool HitDelivered { get; private set; }
    public bool RecoveryAttackPose { get; private set; }
    public WeaponTiming Timing { get; private set; } = WeaponTiming.Sword;

    public bool Apply(Command command, Direction direction, long now, WeaponTiming timing)
    {
        Advance(now);
        if (!Enum.IsDefined(command) || !Enum.IsDefined(direction)) return false;
        LastInput = now;
        switch (command)
        {
            case Command.Begin when Phase == Phase.Idle:
                Timing = timing; Phase = Phase.Windup; Started = now; Direction = direction;
                HitAt = ContactEnds = Ends = 0; HitDelivered = false; RecoveryAttackPose = false; Charge = 0; return true;
            case Command.Release when Phase == Phase.Windup:
                // Release packets must not sneak in a new side with old charge.
                SetDirection(direction, now);
                Charge = Math.Clamp((now - Started) / (float)Timing.Windup, 0, 1);
                HitAt = Math.Max(now, Started + Timing.Windup) + Timing.Strike;
                ContactEnds = HitAt + CombatRules.ContactWindow;
                Ends = ContactEnds + Timing.Recovery; Phase = Phase.Swing; return true;
            case Command.Block when Phase is Phase.Idle or Phase.Windup or Phase.Block:
                if (Phase != Phase.Block) { Phase = Phase.Block; Started = now; }
                SetDirection(direction, now); RecoveryAttackPose = false; Charge = 0; return true;
            case Command.Cancel when Phase is Phase.Windup or Phase.Block:
                Recover(now, Phase == Phase.Block ? CombatRules.GuardRelease : CombatRules.FeintRecovery);
                return true;
            case Command.Aim when Phase is Phase.Windup or Phase.Block:
                SetDirection(direction, now); return true;
            case Command.KeepAlive:
                return true;
            default:
                return false;
        }
    }

    private void SetDirection(Direction direction, long now)
    {
        if (Direction == direction) return;
        Direction = direction; Started = now; Charge = 0;
    }

    private void Recover(long now, int duration, bool attackPose = false)
    {
        Phase = Phase.Recovery; HitDelivered = true; RecoveryAttackPose = attackPose; Charge = 0;
        Ends = Math.Max(Ends, now + duration);
    }

    public bool CanGuard(long now) => Phase == Phase.Block && now >= Started + CombatRules.GuardRaise;

    public void Advance(long now)
    {
        if (Phase is Phase.Windup or Phase.Block && now - LastInput > 800) Reset();
        if (Phase == Phase.Swing && now > ContactEnds)
        {
            // A miss keeps the swing alive only until its fixed deadline.
            HitDelivered = true; RecoveryAttackPose = true; Phase = Phase.Recovery;
        }
        if (Phase == Phase.Recovery && now >= Ends) Phase = Phase.Idle;
    }

    public bool CanHit(long now) => Phase == Phase.Swing && !HitDelivered && now >= HitAt && now <= ContactEnds;

    public bool TakeHit(long now)
    {
        if (!CanHit(now)) return false;
        HitDelivered = true; RecoveryAttackPose = true; Phase = Phase.Recovery;
        Ends = now + Timing.Recovery;
        return true;
    }

    public void Stun(long now, int duration)
    {
        Recover(now, duration, Phase == Phase.Swing || RecoveryAttackPose);
    }

    public void Reset() { Phase = Phase.Idle; HitDelivered = true; RecoveryAttackPose = false; Charge = 0; Started = HitAt = ContactEnds = Ends = 0; }

    public void Invalidate(long now)
    {
        // Changing weapons cancels contact, but cannot bypass committed attack recovery.
        if (Phase == Phase.Swing) { Phase = Phase.Recovery; HitDelivered = true; RecoveryAttackPose = true; }
        else if (Phase is Phase.Windup or Phase.Block)
            Recover(now, Phase == Phase.Block ? CombatRules.GuardRelease : CombatRules.FeintRecovery);
        else if (Phase != Phase.Recovery) Reset();
    }

    public void Restore(Phase phase, Direction direction, long now, int elapsed, int hitIn, int endsIn, float charge,
        bool recoveryAttackPose = false, int? contactEndsIn = null)
    {
        Phase = phase; Direction = direction; Started = now - elapsed; HitAt = now + hitIn;
        ContactEnds = contactEndsIn.HasValue ? now + contactEndsIn.Value : HitAt + CombatRules.ContactWindow;
        Ends = now + endsIn; Charge = charge; LastInput = now;
        HitDelivered = phase == Phase.Recovery;
        RecoveryAttackPose = recoveryAttackPose;
    }

    public static Direction FromMouse(int dx, int dy, Direction previous, int threshold = 3)
    {
        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < threshold) return previous;
        return Math.Abs(dx) > Math.Abs(dy)
            ? (dx > 0 ? Direction.Right : Direction.Left)
            : (dy > 0 ? Direction.Thrust : Direction.Overhead);
    }

    // Directions are relative to each fighter's screen; horizontal parries are mirrored.
    public static Direction GuardFor(Direction incoming) => incoming switch
    {
        Direction.Left => Direction.Right,
        Direction.Right => Direction.Left,
        _ => incoming
    };

    public static bool Faces(double forwardX, double forwardZ, double dx, double dz, double minDot = .35)
    {
        double length = Math.Sqrt(dx * dx + dz * dz);
        return length > .001 && (forwardX * dx + forwardZ * dz) / length >= minDot;
    }

    // Player pitch PI is neutral in VS. The renderer rotates the model's -X
    // front by yaw + PI/2, giving this horizontal world-space forward vector.
    public static (double X, double Z) PlayerForward(double yaw) => (Math.Sin(yaw), Math.Cos(yaw));
}
