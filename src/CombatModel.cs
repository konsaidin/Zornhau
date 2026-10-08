namespace Zornhau;

public enum Direction { Overhead, Right, Thrust, Left }
public enum Phase { Idle, Windup, Swing, Recovery, Block }
public enum Command { Begin, Release, Block, Cancel, Aim, KeepAlive }

public readonly record struct WeaponTiming(int Windup, int Strike, int Recovery)
{
    public bool PhysicalContacts { get; init; }
    public static WeaponTiming Sword => new WeaponTiming(280, 140, 360);
    // CO poses cover a full articulated cut. Use family timings shared by both
    // sides instead of accelerating them to the standalone prototype's release.
    public static WeaponTiming Overhaul(string code) => (code switch
    {
        _ when code.Contains("great") => new WeaponTiming(500, 380, 650),
        _ when code.Contains("pike") => new WeaponTiming(450, 320, 580),
        _ when code.Contains("poleaxe") || code.Contains("halberd") || code.Contains("quarterstaff") => new WeaponTiming(500, 360, 600),
        _ when code.Contains("dagger") || code.Contains("knife") => new WeaponTiming(220, 180, 320),
        _ when code.Contains("axe") || code.Contains("hatchet") => new WeaponTiming(450, 300, 550),
        _ when code.Contains("mace") || code.Contains("hammer") || code.Contains("club") => new WeaponTiming(430, 300, 520),
        _ when code.Contains("spear") || code.Contains("javelin") || code.Contains("trident") => new WeaponTiming(380, 280, 450),
        _ => new WeaponTiming(330, 260, 480)
    }) with { PhysicalContacts = true };
    public static readonly int[] StrikeDurations = { 100, 140, 170, 180, 260, 280, 300, 320, 360, 380 };
}

public static class CombatRules
{
    public const int GuardRaise = 100;
    public const int GuardRelease = 80;
    public const int FeintRecovery = 120;
    public const int ParryStun = 480;
    public const int HitStun = 240;
    public const int ContactJitterBuffer = 75;
    public const int ContactWindow = 50;
    public const int ShieldRaise = 180;
    public const int ShieldBounce = 180;
    public const int ShieldBreak = 350;
    public const int SprintCooldown = 1500;
    public const float WeaponContactPadding = .03f;
    public const double SwingTurnMultiplier = .35;
    public const double SwingTurnRadiansPerSecond = Math.PI;

    public static (double X, double Y) LimitSwingTurn(double x, double y, double seconds)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(seconds) || seconds <= 0)
            return (0, 0);
        x *= SwingTurnMultiplier; y *= SwingTurnMultiplier;
        double length = Math.Sqrt(x * x + y * y);
        double maximum = SwingTurnRadiansPerSecond * seconds;
        double scale = length > maximum ? maximum / length : 1;
        return (x * scale, y * scale);
    }
    public static bool GuardCovers(bool shield, Direction guard, Direction incoming)
        => shield || guard == Fighter.GuardFor(incoming);
    public static int ShieldWear(float damage)
        => (int)Math.Clamp(Math.Ceiling(damage), 1, 100);
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
    public long StrikeStarts => ContactEnds - Timing.Strike - CombatRules.ContactWindow;
    public long Ends { get; private set; }
    public long LastInput { get; private set; }
    public long SprintUntil { get; private set; }
    public bool SprintLocked(long now) => Phase is Phase.Windup or Phase.Swing || now < SprintUntil;
    public bool TurnLimited(long now) => Phase == Phase.Swing && now >= StrikeStarts && now <= ContactEnds;
    public float Charge { get; private set; }
    public bool HitDelivered { get; private set; }
    public bool RecoveryAttackPose { get; private set; }
    public bool ShieldGuard { get; private set; }
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
                // The side belongs to the initial press, including forged release packets.
                Charge = Math.Clamp((now - Started) / (float)Timing.Windup, 0, 1);
                long strikeStarts = Math.Max(now, Started + Timing.Windup);
                // Native colliders must be active while the blade actually cuts.
                // Standalone ray attacks retain their original late contact rule.
                HitAt = strikeStarts + (Timing.PhysicalContacts ? 0 : Timing.Strike);
                ContactEnds = strikeStarts + Timing.Strike + CombatRules.ContactWindow;
                SprintUntil = Math.Max(SprintUntil, ContactEnds + CombatRules.SprintCooldown);
                Ends = ContactEnds + Timing.Recovery; Phase = Phase.Swing; return true;
            case Command.Block when Phase is Phase.Idle or Phase.Windup or Phase.Block:
                if (Phase != Phase.Block)
                {
                    Phase = Phase.Block; Started = now;
                    Direction = ShieldGuard ? Direction.Overhead : direction;
                }
                RecoveryAttackPose = false; Charge = 0; return true;
            case Command.Cancel when Phase is Phase.Windup or Phase.Block:
                Recover(now, Phase == Phase.Block ? CombatRules.GuardRelease : CombatRules.FeintRecovery);
                return true;
            case Command.Aim when Phase is Phase.Windup or Phase.Block:
                // Heartbeats may refresh the hold, never its locked direction.
                return true;
            case Command.KeepAlive:
                return true;
            default:
                return false;
        }
    }

    private void Recover(long now, int duration, bool attackPose = false)
    {
        Phase = Phase.Recovery; HitDelivered = true; RecoveryAttackPose = attackPose; Charge = 0;
        Ends = Math.Max(Ends, now + duration);
    }

    public bool CanGuard(long now) => Phase == Phase.Block
        && now >= Started + (ShieldGuard ? CombatRules.ShieldRaise : CombatRules.GuardRaise);

    public void SetShield(bool equipped, long now)
    {
        if (ShieldGuard == equipped) return;
        Invalidate(now); ShieldGuard = equipped;
    }

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
        SprintUntil = now + CombatRules.SprintCooldown;
        // A contact ends the active cut immediately, but cannot accelerate the
        // committed attack cadence by skipping the rest of its strike window.
        Ends = Timing.PhysicalContacts ? ContactEnds + Timing.Recovery : now + Timing.Recovery;
        return true;
    }

    public void Stun(long now, int duration)
    {
        Recover(now, duration, Phase == Phase.Swing || RecoveryAttackPose);
    }

    public void Reset(bool clearSprint = false)
    {
        Phase = Phase.Idle; HitDelivered = true; RecoveryAttackPose = false; Charge = 0;
        Started = HitAt = ContactEnds = Ends = 0;
        if (clearSprint) SprintUntil = 0;
    }

    public void Invalidate(long now)
    {
        // Changing weapons cancels contact, but cannot bypass committed attack recovery.
        if (Phase == Phase.Swing) { Phase = Phase.Recovery; HitDelivered = true; RecoveryAttackPose = true; }
        else if (Phase is Phase.Windup or Phase.Block)
            Recover(now, Phase == Phase.Block ? CombatRules.GuardRelease : CombatRules.FeintRecovery);
        else if (Phase != Phase.Recovery) Reset();
    }

    public void Restore(Phase phase, Direction direction, long now, int elapsed, int hitIn, int endsIn, float charge,
        bool recoveryAttackPose = false, int? contactEndsIn = null, int? sprintIn = null)
    {
        Phase = phase; Direction = direction; Started = now - elapsed; HitAt = now + hitIn;
        ContactEnds = contactEndsIn.HasValue ? now + contactEndsIn.Value : HitAt + CombatRules.ContactWindow;
        Ends = now + endsIn; Charge = charge; LastInput = now;
        HitDelivered = phase == Phase.Recovery;
        RecoveryAttackPose = recoveryAttackPose;
        if (sprintIn.HasValue) SprintUntil = now + Math.Max(0, sprintIn.Value);
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
