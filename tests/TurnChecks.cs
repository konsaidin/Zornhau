using WarbandCombat;

internal static class TurnChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        foreach (bool physical in new[] { false, true })
        foreach (int strike in WeaponTiming.StrikeDurations)
        {
            var timing = new WeaponTiming(330, strike, 480) { PhysicalContacts = physical };
            var fighter = new Fighter();
            Check(!fighter.TurnLimited(0), "Idle look is free");
            fighter.Apply(Command.Begin, Direction.Left, 1000, timing);
            Check(!fighter.TurnLimited(1020), "Held windup look is free");
            fighter.Apply(Command.Release, Direction.Left, 1020, timing);
            Check(!fighter.TurnLimited(1329), "A quick tap's remaining windup keeps free look");
            Check(fighter.TurnLimited(1330) && fighter.TurnLimited(fighter.ContactEnds),
                "Limit starts at the cut and covers the 50ms contact tail");
            Check(!fighter.TurnLimited(fighter.ContactEnds + 1), "Miss deadline releases look even before the next combat tick");
            fighter.TakeHit(fighter.HitAt);
            Check(!fighter.TurnLimited(fighter.HitAt), "Confirmed contact releases look immediately");
            Check(!fighter.TurnLimited(fighter.Ends), "Recovery look is free");
            foreach (int fps in new[] { 10, 30, 60, 144, 240 })
            {
                double total = 0, duration = (strike + CombatRules.ContactWindow) / 1000d;
                for (double at = 0; at < duration - 1e-10;)
                {
                    double dt = Math.Min(1d / fps, duration - at);
                    var (x, y) = CombatRules.LimitSwingTurn(10000, 0, dt);
                    total += Math.Abs(x); at += dt;
                    Check(y == 0 && Math.Abs(x) <= Math.PI * dt + 1e-12, "Rapid movement obeys the angular speed cap");
                }
                Check(Math.Abs(total - Math.PI * duration) < 1e-9 && total < Math.PI * 2,
                    "Every weapon prevents a full spin with the same speed cap at every FPS");
            }
        }
        var small = CombatRules.LimitSwingTurn(.001, -.002, 1d / 60);
        Check(Math.Abs(small.X - .00035) < 1e-12 && Math.Abs(small.Y + .0007) < 1e-12,
            "Small horizontal/vertical corrections retain sign and are slowed by 65 percent");
        var diagonal = CombatRules.LimitSwingTurn(500, -500, .02);
        Check(Math.Abs(Math.Sqrt(diagonal.X * diagonal.X + diagonal.Y * diagonal.Y) - Math.PI * .02) < 1e-12
            && diagonal.X > 0 && diagonal.Y < 0, "Diagonal turns share one speed budget");
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Check(CombatRules.LimitSwingTurn(invalid, 1, .02) == (0, 0), "Invalid input stays finite");
        Console.WriteLine($"PASS: {checks} strike-only turn limits and FPS checks");
    }
}
