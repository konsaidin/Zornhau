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
                Check(state.JumpPulse > .001, "each new push-off animates even when air blend stayed active");
                Check(Math.Abs(state.AirSide - oldSide) < .3, "alternating lead leg crossfades instead of flipping");
                var push = source.Air("UpperFootR", state);
                Check(push != source.Sample("creativefly", "UpperFootR", state.FlightCycle), "takeoff has its own movement instead of persistent flight");
                for (int i = 0; i < fps / 2; i++) Frame(2);
                Check(state.JumpCount == hop, "continued ascent does not restart the jump");
                Check(state.JumpPulse < .0001, "takeoff finishes and returns to a flight pose");
                Check(Math.Abs(state.AirSide - (hop - 1) % 2) < .05, "successive hops alternate the leading leg smoothly");
                var right = source.Air("UpperFootR", state);
                var left = source.Air("UpperFootL", state);
                Check(hop % 2 == 1 ? right.Z < left.Z : left.Z < right.Z, "the visible folded knee alternates on each hop");
                var armR = source.AirArms("UpperArmR", state);
                var armL = source.AirArms("UpperArmL", state);
                Check(hop % 2 == 1 ? armR.Z < armL.Z : armL.Z < armR.Z,
                    "held arms alternate their small lean together with the leading leg");
                Frame(0); Frame(-2);
                Check(state.JumpCount == hop && state.AirBlend > .9, "apex and descent retain one jump");
                if (groundFrame) Frame(0, true);
            }
            Frame(0, true); Frame(0, true);
            Check(state.LandingPulse > .005, "landing has a distinct compression phase with eased endpoints");
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
        var heldState = new MotionState { AirBlend = 1, SprintBlend = 1, AirSide = .3f };
        foreach (string bone in new[] { "UpperArmR", "UpperArmL", "LowerArmR", "LowerArmL" })
        {
            var held = source.AirArms(bone, heldState);
            Check(held.Z < -20, "jump arms are raised rather than resting by the body");
            for (int i = 0; i < 20; i++)
            {
                heldState.Cycle = i / 20d; heldState.FlightCycle = (i * .13) % 1; heldState.JumpPulse = i / 20f;
                Check(source.AirArms(bone, heldState) == held, "arm hold is independent of running, flight and takeoff clocks");
            }
            var neutral = new ElementPose();
            NaturalMotionSystem.Apply(heldState, source, bone, neutral, true, true);
            Check(BonePose.From(neutral) == held, "airborne correction actually applies the held arm pose");
            var occupied = new ElementPose { degX = 12, degZ = -34 };
            var original = BonePose.From(occupied);
            NaturalMotionSystem.Apply(heldState, source, bone, occupied, true, false);
            Check(BonePose.From(occupied) == original, "occupied hands retain their item and combat pose");
            NaturalMotionSystem.Apply(heldState, source, bone, occupied, true, true, immersive: true);
            Check(BonePose.From(occupied) == original, "held-arm correction preserves immersive first-person weapon anchors");
        }
        var first = new MotionState { AirSide = 0 }; var next = new MotionState { AirSide = 1 };
        foreach (var (right, left) in new[] { ("UpperArmR", "UpperArmL"), ("LowerArmR", "LowerArmL") })
        {
            var r = source.AirArms(right, first); var l = source.AirArms(left, next);
            Check(r.X == -l.X && r.Y == -l.Y && r.Z == l.Z && r.TZ == -l.TZ,
                "next-hop arms mirror the previous held pose across the body");
        }
        return checks;
    }
}
