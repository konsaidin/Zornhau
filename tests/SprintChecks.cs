using WarbandCombat;

internal static class SprintChecks
{
    internal static void Run()
    {
        int count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        foreach (var timing in new[] { WeaponTiming.Sword, new WeaponTiming(180, 100, 260),
            new WeaponTiming(420, 170, 480), WeaponTiming.Overhaul("sword-great"),
            WeaponTiming.Overhaul("pike"), WeaponTiming.Overhaul("dagger") })
        foreach (bool hit in new[] { true, false })
        {
            var fighter = new Fighter();
            Check(!fighter.SprintLocked(0), "Idle allows sprint");
            fighter.Apply(Command.Begin, Direction.Left, 0, timing);
            Check(fighter.SprintLocked(0), "Holding windup prevents sprint immediately");
            fighter.Apply(Command.Release, Direction.Right, 1, timing);
            Check(fighter.SprintLocked(1), "Released swing prevents sprint before contact opens");
            long completed = hit ? fighter.HitAt : fighter.ContactEnds;
            if (hit) Check(fighter.TakeHit(completed), "Contact starts sprint cooldown");
            else fighter.Advance(completed + 1);
            Check(fighter.SprintUntil == completed + 1500, "Sprint cooldown starts at contact or the end of a miss");
            fighter.Invalidate(completed + 100);
            fighter.Apply(Command.Cancel, Direction.Right, completed + 110, timing);
            fighter.Advance(fighter.Ends);
            Check(fighter.Phase == Phase.Idle && fighter.SprintLocked(completed + 1499),
                "Weapon swap, cancelled input and recovery expiry cannot bypass sprint cooldown");
            Check(!fighter.SprintLocked(completed + 1500), "Sprint resumes exactly 1.5 seconds after completion");
            fighter.Reset();
            Check(fighter.SprintUntil == completed + 1500, "Ordinary combat reset preserves movement cooldown");
            fighter.Reset(clearSprint: true);
            Check(!fighter.SprintLocked(0), "Leaving the world clears the prior clock's movement cooldown");
        }
        var feint = new Fighter();
        feint.Apply(Command.Begin, Direction.Left, 0, WeaponTiming.Sword);
        feint.Apply(Command.Cancel, Direction.Right, 100, WeaponTiming.Sword);
        Check(!feint.SprintLocked(100), "An unreleased feint creates no post-strike sprint cooldown");
        var heldGuard = new Fighter();
        heldGuard.Apply(Command.Block, Direction.Left, 0, WeaponTiming.Sword);
        heldGuard.Apply(Command.Block, Direction.Right, 180, WeaponTiming.Sword);
        Check(heldGuard.Direction == Direction.Left && heldGuard.Started == 0,
            "Repeated block packets cannot replace a held guard");
        Check(!heldGuard.SprintLocked(180), "Guard alone does not impose the attack sprint restriction");
        var prediction = new Fighter();
        prediction.Restore(Phase.Recovery, Direction.Left, 1000, 300, -10, 500, 1, true, -10, 1450);
        Check(prediction.SprintLocked(2449) && !prediction.SprintLocked(2450),
            "Authoritative cooldown restores against the local clock including snapshot age");
        prediction.Restore(Phase.Idle, Direction.Left, 2450, 0, 0, 0, 0, false, 0, 0);
        Check(!prediction.SprintLocked(2450), "Server expiry clears the predicted restriction");
        Console.WriteLine($"PASS: {count} locked-direction and sprint checks");
    }
}
