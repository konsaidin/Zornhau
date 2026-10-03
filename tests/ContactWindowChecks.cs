using WarbandCombat;

internal static class ContactWindowChecks
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
            new WeaponTiming(420, 170, 480), new WeaponTiming(180, 100, 260) };
        foreach (var timing in timings)
        foreach (Direction direction in Enum.GetValues<Direction>())
        {
            Fighter Swing()
            {
                var f = new Fighter();
                f.Apply(Command.Begin, direction, 0, timing);
                f.Apply(Command.Release, direction, 1, timing);
                return f;
            }
            var early = Swing();
            long opens = timing.Windup + timing.Strike;
            long closes = opens + 50;
            Check(early.HitAt == opens && early.ContactEnds == closes, "Contact must not begin before the existing windup/strike deadline");
            Check(!early.CanHit(opens - 1) && !early.TakeHit(opens - 1), "No early contact while raising or moving the weapon");
            Check(early.TakeHit(opens) && early.Phase == Phase.Recovery && early.RecoveryAttackPose,
                "The first contact must immediately end the swing");
            Check(early.Ends == opens + timing.Recovery, "Recovery starts at actual contact, without waiting for the remaining window");
            Check(!early.CanHit(opens + 20) && !early.TakeHit(opens + 20), "Another entity cannot be hit by the spent swing");

            var turned = Swing();
            foreach (long now in new[] { opens, opens + 20 })
            {
                turned.Advance(now);
                Check(turned.CanHit(now) && !turned.HitDelivered && turned.Phase == Phase.Swing,
                    "A ray missing empty space must leave contact available on the next server tick");
            }
            Check(!turned.Apply(Command.Aim, (Direction)(((int)direction + 1) % 4), opens + 21, timing)
                && turned.Direction == direction, "Camera turning must not switch the committed attack direction");
            Check(turned.TakeHit(opens + 40) && turned.Ends == opens + 40 + timing.Recovery,
                "A turn after an initial miss can connect late within the window");
            turned.Stun(opens + 40, CombatRules.ParryStun);
            Check(!turned.CanHit(opens + 50) && turned.Ends == opens + 40 + CombatRules.ParryStun,
                "A late parry must close the window and preserve its longer stun");

            var edge = Swing();
            Check(edge.CanHit(closes) && edge.TakeHit(closes), "The exact final contact deadline is usable");
            var missed = Swing();
            Check(!missed.CanHit(closes + 1) && !missed.TakeHit(closes + 1), "Lagged ticks must not grant hits outside the window");
            missed.Advance(closes + 1);
            Check(missed.Phase == Phase.Recovery && missed.HitDelivered && missed.RecoveryAttackPose,
                "A full miss must automatically close and enter recovery");
            Check(missed.Ends == closes + timing.Recovery, "Missing keeps the full window plus recovery cost");
            Check(!missed.Apply(Command.Begin, direction, missed.Ends - 1, timing), "Miss recovery prevents attack spam");
            missed.Advance(closes + timing.Recovery);
            Check(missed.Apply(Command.Begin, direction, closes + timing.Recovery, timing), "Miss recovery expires at its fixed deadline");

            var swapped = Swing();
            swapped.Invalidate(opens + 10);
            Check(!swapped.CanHit(opens + 20) && swapped.Ends == closes + timing.Recovery,
                "A mid-window weapon swap cannot connect or shorten committed recovery");
            var delayed = Swing();
            delayed.Advance(closes + timing.Recovery + 100);
            Check(delayed.Phase == Phase.Idle && !delayed.TakeHit(closes + timing.Recovery + 100),
                "A large server delay must not produce a stale hit or leave the fighter stuck");
        }
        var restored = new Fighter();
        restored.Restore(Phase.Swing, Direction.Right, 1000, 350, -20, 390, 1,
            false, 30);
        Check(restored.CanHit(1000) && restored.CanHit(1030) && !restored.CanHit(1031),
            "Network prediction must preserve both edges of an already open window");
        restored.Advance(1031);
        Check(restored.Phase == Phase.Recovery && restored.Ends == 1390,
            "The client must predict miss recovery instead of consuming the swing at its first possible contact");
        restored.Restore(Phase.Recovery, Direction.Right, 1100, 400, -50, 350, 1, true, 0);
        Check(!restored.CanHit(1100) && restored.RecoveryAttackPose && restored.Ends == 1450,
            "A server-confirmed hit must close prediction immediately");
        Console.WriteLine($"PASS: {count} late-contact window checks");
    }
}
