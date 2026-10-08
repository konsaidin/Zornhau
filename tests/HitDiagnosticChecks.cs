using Zornhau;

internal static class HitDiagnosticChecks
{
    internal static void Run()
    {
        int count = 0;
        void Check(bool value, string text) { if (!value) throw new Exception(text); count++; }
        var d = new HitDiagnostics();
        Check(!d.Finish(Phase.Idle), "An idle player has no diagnostic attack");
        d.Begin(10);
        Check(!d.Finish(Phase.Windup) && !d.Finish(Phase.Swing), "Holding and cutting are not completed misses");
        d.Sample(false, false, false);
        Check(d.Finish(Phase.Recovery) && d.Result == "no-contact", "A sampled empty swing misses");
        Check(!d.Finish(Phase.Idle), "Only one finish is logged");
        d.Begin(11); d.Sample(true, false, false); d.Finish(Phase.Recovery);
        Check(d.Result == "miss-with-terrain", "Touching terrain alone does not prove it blocked a target");
        d.Begin(12); d.Sample(true, false, true); d.Sample(false, false, false); d.Finish(Phase.Recovery);
        Check(d.Result == "terrain-blocked-target" && d.Frames == 2, "A proven terrain rejection remains in the swing summary");
        d.Begin(13); d.Sample(false, true, false); d.Finish(Phase.Recovery);
        Check(d.Result == "client-no-packet", "Model intersection without damage packet is a distinct result");
        d.Begin(14); d.Sample(true, false, true); d.Sample(false, true, false); d.ContactSent(1000); d.Finish(Phase.Recovery);
        Check(d.Result == "waiting", "Later contact can succeed after a blocked early sample; sending is not server acceptance");
        Check(!d.Timeout(2999) && d.Timeout(3000), "No-reply status waits two seconds");
        Check(!d.Timeout(4000), "A missing reply is logged once");
        Check(d.ServerResult(14, "blocked") && d.Result == "blocked", "An authoritative late block supersedes timeout");
        d.Begin(15);
        Check(!d.ServerResult(14, "hit") && d.Result == "windup", "An old server reply cannot replace a newer swing");
        d.ContactSent(4000); d.ServerResult(15, "co-terrain-obstructed"); d.Finish(Phase.Recovery);
        Check(d.Result == "co-terrain-obstructed" && !d.Timeout(9000), "A rejection is preserved, with no false timeout");
        d.Begin(16); d.Finish(Phase.Recovery);
        Check(d.Result == "not-sampled", "Cancelled or unsampled attacks do not claim geometric misses");
        d.Reset();
        Check(d.AttackId == 0 && !d.ServerResult(16, "hit"), "Leaving world discards old attack identity");
        var f = new Fighter();
        f.Apply(Command.Begin, Direction.Left, 100, WeaponTiming.Overhaul("greatsword"));
        f.Apply(Command.Release, Direction.Left, 600, f.Timing);
        var state = (f.Phase, f.Started, f.HitAt, f.ContactEnds, f.Ends, f.SprintUntil, f.HitDelivered);
        d.Begin(17); d.Sample(true, true, true); d.ContactSent(600); d.ServerResult(17, "hit"); d.Finish(f.Phase); d.Timeout(9000);
        Check(state == (f.Phase, f.Started, f.HitAt, f.ContactEnds, f.Ends, f.SprintUntil, f.HitDelivered), "Observations never consume or advance the real attack");
        Console.WriteLine($"PASS: {count} hit diagnostic checks");
    }
}
