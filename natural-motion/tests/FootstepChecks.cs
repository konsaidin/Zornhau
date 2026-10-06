using NaturalMotion;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

internal static class FootstepChecks
{
    internal static int Run(Shape shape)
    {
        int checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
        var config = new MotionConfig();
        MotionInput Input(long at, bool sprint = false, bool ground = true, double speed = 2, double vy = 0)
            => new(at, 0, 0, 0, 0, 0, vy, speed, 0, ground, false, sprint, false, false);
        foreach (int fps in new[] { 30, 60, 144 })
        foreach (bool sprint in new[] { false, true })
        {
            long step = sprint ? 340 : 470;
            var state = new MotionState(); state.Update(Input(0, sprint), config);
            long previous = 0;
            for (int stride = 1; stride <= 8; stride++)
            {
                long end = stride * step;
                for (long at = previous + Math.Max(1, 1000 / fps); at < end; at += Math.Max(1, 1000 / fps))
                    state.Update(Input(at, sprint), config);
                state.Update(Input(end, sprint), config);
                double expected = (stride % 2) * .5;
                double error = state.Cycle - expected; error -= Math.Round(error);
                Check(Math.Abs(error) < .00001, "470/340ms audible interval matches alternating half-strides at every FPS");
                double before = state.Cycle;
                state.ObserveFootstep(end, sprint, config);
                Check(state.Cycle == before, "actual sound callback never resets or snaps the rendered pose");
                state.ObserveFootstep(end, sprint, config);
                Check(state.Cycle == before, "duplicate sound callback cannot advance a second foot");
                previous = end;
            }
            double stopped = state.Cycle;
            state.Update(Input(previous + 17, sprint, speed: 0), config);
            Check(state.Cycle == stopped, "stopped actor freezes the step clock");
            state.Update(Input(previous + 34, sprint, ground: false, vy: 4), config);
            Check(state.Cycle == stopped, "jump freezes walking clock instead of continuing footsteps");
        }
        // Real intervals drift with clothing, terrain and engine timestep. The
        // native event slowly pulls animation to that clock, without teleporting feet.
        var withSound = new MotionState(); var withoutSound = new MotionState();
        withSound.Update(Input(0), config); withoutSound.Update(Input(0), config);
        for (long at = 17; at <= 850; at += 17)
        {
            withSound.Update(Input(at), config); withoutSound.Update(Input(at), config);
            if (at == 340) withSound.ObserveFootstep(at, false, config);
            if (at == 680) withSound.ObserveFootstep(at, false, config);
        }
        double difference = withSound.Cycle - withoutSound.Cycle; difference -= Math.Round(difference);
        Check(Math.Abs(difference) > .05, "measured native sounds adjust predicted cadence");
        var limited = new MotionState(); limited.Update(Input(0), config);
        limited.Update(Input(200), config); double baseline = limited.Cycle;
        limited.ObserveFootstep(200, false, config);
        Check(limited.Cycle == baseline, "off-phase sound leaves pose continuous");
        limited.Update(Input(217), config);
        double advance = limited.Cycle - baseline;
        Check(Math.Abs(advance - .017 / .94) <= .25 * .017 + 1e-9,
            "phase correction has a bounded slew rate instead of a frame jump");
        var invalid = new MotionConfig { WalkStepMilliseconds = float.NaN, SprintStepMilliseconds = 0 };
        invalid.Sanitize(); Check(invalid.WalkStepMilliseconds == 470 && invalid.SprintStepMilliseconds == 150,
            "footstep interval settings cannot divide by zero or generate NaN");
        // Validate contacts against real skeleton geometry: both feet should
        // be near their lowest sole position on their canonical contact phase.
        var source = new StockPoses(shape.Animations);
        double Sole(string clip, string foot, double phase)
        {
            double lowest = double.PositiveInfinity;
            void Visit(ShapeElement[] elements, float[] parent)
            {
                foreach (var element in elements)
                {
                    var pose = new ElementPose(); source.Sample(clip, element.Name!, phase, true).Apply(pose);
                    var local = element.GetLocalTransformMatrix(0, Mat4f.Create(), pose);
                    var world = Mat4f.Mul(Mat4f.Create(), parent, local);
                    var from = element.From!; var to = element.To!;
                    if (element.Name == foot)
                        for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
                            lowest = Math.Min(lowest, world[1] * (to[0] - from[0]) * x / 16
                                + world[5] * (to[1] - from[1]) * y / 16
                                + world[9] * (to[2] - from[2]) * z / 16 + world[13]);
                    if (element.Children != null) Visit(element.Children, world);
                }
            }
            Visit(shape.Elements, Mat4f.Create()); return lowest;
        }
        foreach (string clip in new[] { "walk", "sprint", "walkback", "walkleft", "walkright" })
        foreach (var (foot, phase) in new[] { ("LowerFootR", 0d), ("LowerFootL", .5) })
        {
            double contact = Sole(clip, foot, phase);
            Check(contact <= Sole(clip, foot, phase - .1) + .003 && contact <= Sole(clip, foot, phase + .1) + .003,
                "canonical sound contact aligns to a planted sole on the actual game skeleton");
        }
        return checks;
    }
}
