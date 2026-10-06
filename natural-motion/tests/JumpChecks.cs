using NaturalMotion;
using Vintagestory.API.Common;

internal static class JumpChecks
{
    internal static int Run(Shape shape)
    {
        int checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
        var source = new StockPoses(shape.Animations);
        var config = new MotionConfig();
        foreach (int fps in new[] { 30, 60, 144 })
        foreach (bool groundFrame in new[] { false, true })
        {
            var state = new MotionState(); long at = 0;
            void Frame(double vy, bool ground = false)
            {
                at += (long)Math.Round(1000d / fps);
                state.Update(new(at, 0, 0, 0, 0, 0, vy, 0, 0, ground, false, false, false, false), config);
            }
            Frame(0, true);
            for (int hop = 1; hop <= 6; hop++)
            {
                // Include ascent, a real apex and descent. Never reset the
                // entity/animator between hops, just like held-space jumping.
                var beforeRebound = source.Air("UpperFootR", state);
                Frame(4);
                Check(state.JumpCount == hop, "each rebound starts a new jump, including a missed ground frame");
                Check(state.JumpPulse < .0001, "jump envelope begins without a pose discontinuity");
                if (hop > 1) Check(Math.Abs(source.Air("UpperFootR", state).Z - beforeRebound.Z) < 16,
                    "a rebound cannot instantly swap the 45-degree knee difference");
                float oldSide = state.AirSide;
                Frame(3.8);
                Check(state.JumpPulse > .05, "each new push-off animates even when air blend stayed active");
                Check(Math.Abs(state.AirSide - oldSide) < .3, "alternating lead leg crossfades instead of flipping");
                var push = source.Air("UpperFootR", state);
                Check(push != source.Sample("creativefly", "UpperFootR", state.FlightCycle), "takeoff has its own movement instead of persistent flight");
                for (int i = 0; i < fps / 2; i++) Frame(2);
                Check(state.JumpCount == hop, "continued ascent does not restart the jump");
                Check(state.JumpPulse < .0001, "takeoff finishes and returns to a flight pose");
                Check(Math.Abs(state.AirSide - (hop - 1) % 2) < .01, "successive hops alternate the leading leg");
                var right = source.Air("UpperFootR", state);
                var left = source.Air("UpperFootL", state);
                Check(hop % 2 == 1 ? right.Z < left.Z : left.Z < right.Z, "the visible folded knee alternates on each hop");
                Frame(0); Frame(-2);
                Check(state.JumpCount == hop && state.AirBlend > .9, "apex and descent retain one jump");
                if (groundFrame) Frame(0, true);
            }
            Frame(0, true); Frame(0, true);
            Check(state.LandingPulse > .1, "landing has a distinct compression phase");
            Check(StockPoses.Landing("LowerFootR", state).Z > 0, "landing bends knees rather than retaining flight");
            for (int i = 0; i < fps; i++) Frame(0, true);
            Check(!state.Active && state.AirBlend < .001 && state.LandingPulse < .001, "standing after a hop releases every jump and landing offset");
        }
        var falling = new MotionState();
        falling.Update(new(0, 0, 0, 0, 0, 0, -3, 0, 0, false, false, false, false, false), config);
        Check(falling.JumpCount == 0, "walking off a ledge is not a push-off");
        var disabled = new MotionState();
        var noAir = new MotionConfig { AirbornePose = false };
        disabled.Update(new(0, 0, 0, 0, 0, 0, 4, 0, 0, false, false, false, false, false), noAir);
        Check(disabled.AirBlend == 0 && disabled.JumpPulse == 0 && disabled.LandingPulse == 0, "airborne setting disables new jump corrections too");
        return checks;
    }
}
