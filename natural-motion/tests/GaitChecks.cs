using NaturalMotion;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

internal static class GaitChecks
{
    internal static int Run(Shape shape)
    {
        int checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        var source = new StockPoses(shape.Animations);
        var actor = new EntityPlayer();
        actor.ServerControls.Sprint = true;
        Check(NaturalMotionSystem.IsSprinting(actor), "replicated sprint selects running even when input controls are false");
        actor.Controls.Sneak = true;
        Check(!NaturalMotionSystem.IsSprinting(actor), "crouching does not substitute a running pose");
        var config = new MotionConfig { WalkStepScale = float.NaN, JumpLegHeight = 9 };
        config.Sanitize();
        Check(config.WalkStepScale == 1.35f && config.JumpLegHeight == 1, "new pose settings remain finite and bounded");

        foreach (string direction in new[] { "forward", "back", "left", "right" })
        {
            MotionState Motion(float scale, float run) => new() { WalkStepScale = scale, SprintBlend = run,
                ForwardWeight = direction == "forward" ? 1 : 0, BackWeight = direction == "back" ? 1 : 0,
                LeftWeight = direction == "left" ? 1 : 0, RightWeight = direction == "right" ? 1 : 0 };
            var walking = Motion(1.35f, 0); var oldWalking = Motion(1, 0); var running = Motion(1.35f, 1);
            double spread = 0, oldSpread = 0, difference = 0;
            for (int i = 0; i < 64; i++)
            {
                walking.Cycle = oldWalking.Cycle = running.Cycle = i / 64d;
                var a = Feet(walking); var b = Feet(oldWalking);
                spread = Math.Max(spread, Math.Sqrt(Math.Pow(a.R.X - a.L.X, 2) + Math.Pow(a.R.Z - a.L.Z, 2)));
                oldSpread = Math.Max(oldSpread, Math.Sqrt(Math.Pow(b.R.X - b.L.X, 2) + Math.Pow(b.R.Z - b.L.Z, 2)));
                var walk = source.Walk("UpperFootR", walking); var run = source.Walk("UpperFootR", running);
                difference += Math.Pow(walk.X - run.X, 2) + Math.Pow(walk.Z - run.Z, 2);
                if (direction == "forward")
                {
                    var stock = source.Sample("sprint", "UpperFootR", running.Cycle, true);
                    Check(Math.Abs(run.X - stock.X) + Math.Abs(run.Y - stock.Y) + Math.Abs(run.Z - stock.Z)
                        + Math.Abs(run.TX - stock.TX) + Math.Abs(run.TY - stock.TY) + Math.Abs(run.TZ - stock.TZ) < .0001,
                        "forward running uses the actual sprint clip, not a sped-up walk");
                }
                foreach (string bone in new[] { "UpperArmR", "UpperArmL", "LowerArmR", "LowerArmL" })
                {
                    var pose = new ElementPose();
                    running.GroundBlend = 1;
                    NaturalMotionSystem.Apply(running, source, bone, pose, true, true);
                    Check(BonePose.From(pose) == source.Walk(bone, running), "grounded running preserves authored arm swings");
                    running.AirBlend = 1;
                    pose = new ElementPose();
                    NaturalMotionSystem.Apply(running, source, bone, pose, true, true);
                    var held = source.AirArms(bone, running);
                    Check(BonePose.From(pose) == held, "free arms hold the raised pose during an airborne run");
                    running.AirBlend = 0;
                }
            }
            Check(spread > oldSpread * 1.1, "wider walking increases actual horizontal foot separation on the game skeleton: " + direction);
            Check(Math.Sqrt(difference / 64) > 4, "running has distinct leg motion in each direction: " + direction);
            foreach (var state in new[] { walking, running })
            foreach (bool right in new[] { false, true })
            {
                double phase = right ? 0 : .5;
                state.Cycle = phase; var contact = Feet(state);
                state.Cycle = phase - .1; var before = Feet(state);
                state.Cycle = phase + .1; var after = Feet(state);
                double y = right ? contact.R.Y : contact.L.Y;
                Check(y <= (right ? before.R.Y : before.L.Y) + .003 && y <= (right ? after.R.Y : after.L.Y) + .003,
                    "widened walk and directional run preserve audio contact phases: " + direction);
            }
        }
        var air = new MotionState { AirBlend = 1, JumpLegHeight = .55f };
        foreach (string bone in new[] { "UpperFootR", "UpperFootL", "LowerFootR", "LowerFootL" })
        {
            var pose = source.Air(bone, air);
            Check(Math.Abs(pose.Z) <= 38, "relaxed flight legs remain well below the old 67.5-degree knee lift");
        }
        return checks;

        (Vec3d R, Vec3d L) Feet(MotionState state)
        {
            var right = new Vec3d(); var left = new Vec3d();
            void Visit(ShapeElement[] elements, float[] parent)
            {
                foreach (var element in elements)
                {
                    var pose = new ElementPose(); source.Walk(element.Name!, state).Apply(pose);
                    var local = element.GetLocalTransformMatrix(0, Mat4f.Create(), pose);
                    var world = Mat4f.Mul(Mat4f.Create(), parent, local);
                    if (element.Name is "LowerFootR" or "LowerFootL")
                    {
                        var foot = element.Name == "LowerFootR" ? right : left;
                        var to = element.To!; var from = element.From!;
                        double x = (to[0] - from[0]) / 32, z = (to[2] - from[2]) / 32;
                        foot.Set(world[0] * x + world[8] * z + world[12], world[1] * x + world[9] * z + world[13],
                            world[2] * x + world[10] * z + world[14]);
                        foot.Y = double.PositiveInfinity;
                        for (int ix = 0; ix < 2; ix++) for (int iy = 0; iy < 2; iy++) for (int iz = 0; iz < 2; iz++)
                            foot.Y = Math.Min(foot.Y, world[1] * (to[0] - from[0]) * ix / 16
                                + world[5] * (to[1] - from[1]) * iy / 16
                                + world[9] * (to[2] - from[2]) * iz / 16 + world[13]);
                    }
                    if (element.Children != null) Visit(element.Children, world);
                }
            }
            Visit(shape.Elements, Mat4f.Create()); return (right, left);
        }
    }
}
