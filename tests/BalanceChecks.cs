using Zornhau;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

internal static class BalanceChecks
{
    internal static void Run()
    {
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            count++;
        }
        var timings = new[] { WeaponTiming.Sword, new WeaponTiming(360, 140, 420),
            new WeaponTiming(420, 170, 480), new WeaponTiming(180, 100, 260),
            WeaponTiming.Overhaul("sword-great-plain-steel"), WeaponTiming.Overhaul("pike-plain-steel"),
            WeaponTiming.Overhaul("dagger-plain-steel"), WeaponTiming.Overhaul("mace-plain-steel") };
        foreach (var timing in timings)
        foreach (Direction initial in Enum.GetValues<Direction>())
        foreach (Direction final in Enum.GetValues<Direction>())
        {
            var aimed = new Fighter();
            aimed.Apply(Command.Begin, initial, 0, timing);
            aimed.Apply(Command.KeepAlive, initial, 600, timing);
            aimed.Apply(Command.Aim, final, 700, timing);
            aimed.Apply(Command.Release, final, 701, timing);
            long expected = 701 + (timing.PhysicalContacts ? 0 : timing.Strike);
            Check(aimed.Direction == initial && aimed.Started == 0, "Aim and release packets preserve the pressed direction");
            Check(aimed.HitAt == expected, "Held input cannot change direction or reset windup");
            Check(!aimed.TakeHit(expected - 1) && aimed.TakeHit(expected), "Contact must respect the original windup exactly");

            var released = new Fighter();
            released.Apply(Command.Begin, initial, 0, timing);
            released.Apply(Command.KeepAlive, initial, 600, timing);
            released.Apply(Command.Release, final, 701, timing);
            Check(released.HitAt == 701 + (timing.PhysicalContacts ? 0 : timing.Strike),
                "Release itself cannot smuggle a changed direction past windup");
            long ends = released.Ends;
            foreach (Command command in new[] { Command.Aim, Command.Begin, Command.Block, Command.Cancel })
                Check(!released.Apply(command, initial, 702, timing), "A committed strike cannot change side, cancel or block");
            Check(released.Ends == ends && released.Direction == initial, "Rejected inputs must preserve committed recovery and direction");
        }
        var sword = WeaponTiming.Sword;
        var repeated = new Fighter();
        repeated.Apply(Command.Begin, Direction.Left, 0, sword);
        repeated.Apply(Command.Aim, Direction.Right, 100, sword);
        repeated.Apply(Command.Aim, Direction.Overhead, 200, sword);
        repeated.Apply(Command.Aim, Direction.Right, 250, sword);
        repeated.Apply(Command.Release, Direction.Right, 251, sword);
        Check(repeated.HitAt == sword.Windup + sword.Strike, "Mouse motion cannot change a held chamber");

        foreach (Direction initial in Enum.GetValues<Direction>())
        foreach (Direction final in Enum.GetValues<Direction>())
        {
            var guard = new Fighter();
            guard.Apply(Command.Block, initial, 0, sword);
            Check(!guard.CanGuard(99) && guard.CanGuard(100), "A new guard needs 100ms to become effective");
            guard.Apply(Command.Block, initial, 150, sword);
            Check(guard.CanGuard(150) && guard.Started == 0, "Repeated guard commands must not lower a ready guard");
            guard.Apply(Command.Aim, final, 201, sword);
            Check(guard.CanGuard(201) && guard.Direction == initial, "Mouse motion cannot change a held guard");
            Check(guard.CanGuard(301), "Held guard remains ready");
            guard.Apply(Command.Cancel, final, 350, sword);
            Check(!guard.CanGuard(350) && !guard.RecoveryAttackPose, "Dropping guard is not a strike or an effective guard");
            Check(!guard.Apply(Command.Begin, final, 429, sword), "Guard release cannot instantly launch a counter");
            Check(guard.Apply(Command.Begin, final, 430, sword), "Counterattack becomes available after 80ms");
        }
        foreach (Phase phase in new[] { Phase.Windup, Phase.Block })
        {
            var cancel = new Fighter();
            cancel.Apply(phase == Phase.Windup ? Command.Begin : Command.Block, Direction.Left, 0, sword);
            cancel.Invalidate(100);
            int delay = phase == Phase.Windup ? CombatRules.FeintRecovery : CombatRules.GuardRelease;
            Check(!cancel.Apply(Command.Begin, Direction.Right, 100 + delay - 1, sword),
                "Weapon swap and mode cancellation cannot skip feint/guard recovery");
            Check(cancel.Apply(Command.Begin, Direction.Right, 100 + delay, sword), "Short recovery expires on schedule");
        }
        var feint = new Fighter();
        feint.Apply(Command.Begin, Direction.Left, 0, sword);
        feint.Apply(Command.Cancel, Direction.Right, 50, sword);
        Check(feint.Phase == Phase.Recovery && !feint.RecoveryAttackPose && !feint.TakeHit(500),
            "A feint must not deal damage or play the completed strike pose");
        Check(!feint.Apply(Command.Block, Direction.Right, 169, sword)
            && feint.Apply(Command.Block, Direction.Right, 170, sword), "Feint cancellation needs 120ms before another action");

        var stun = new Fighter();
        stun.Apply(Command.Begin, Direction.Left, 0, sword);
        stun.Apply(Command.Release, Direction.Left, 1, sword);
        long originalEnds = stun.Ends;
        stun.Stun(100, 120);
        Check(stun.Ends == originalEnds, "A shorter stun cannot shorten committed recovery");
        stun.Stun(700, CombatRules.ParryStun);
        Check(stun.Ends == 1180, "A parry extends recovery when needed");
        Check(!stun.Apply(Command.Block, Direction.Right, 1179, sword)
            && stun.Apply(Command.Block, Direction.Right, 1180, sword), "Parry stagger prevents immediate re-guarding");

        var restored = new Fighter();
        restored.Restore(Phase.Block, Direction.Left, 1000, 99, 0, 0, 0);
        Check(!restored.CanGuard(1000) && restored.CanGuard(1001), "Client guard prediction must match restored server timing");
        restored.Restore(Phase.Recovery, Direction.Left, 1000, 0, 0, 80, 0, false);
        Check(!restored.RecoveryAttackPose && !restored.Apply(Command.Begin, Direction.Right, 1079, sword),
            "Restoring a cancelled guard must preserve recovery without a false hit pose");

        foreach (float yaw in new[] { 0f, GameMath.PIHALF, GameMath.PI, -GameMath.PIHALF, .7f })
        {
            var engine = EntityPos.GetViewVector(GameMath.PI, yaw);
            var forward = Fighter.PlayerForward(yaw);
            Check(Math.Abs(forward.X - engine.X) < .00001 && Math.Abs(forward.Z - engine.Z) < .00001,
                "Guard facing must match the engine's player view direction");
            Check(Fighter.Faces(forward.X, forward.Z, engine.X * 3, engine.Z * 3), "Frontal attacks can be parried");
            Check(!Fighter.Faces(forward.X, forward.Z, -engine.X * 3, -engine.Z * 3), "Back attacks cannot be parried");
        }
        foreach (bool pvp in new[] { false, true })
        foreach (bool directed in new[] { false, true })
        foreach (bool player in new[] { false, true })
        foreach (bool melee in new[] { false, true })
            Check(CombatRules.RejectVanillaPvpMelee(pvp, directed, player, melee) == (pvp && !directed && player && melee),
                "Strict PvP must reject vanilla player melee while allowing directional, projectile and PvE damage");
        Console.WriteLine($"PASS: {count} PvP timing and bypass checks");
    }
}
