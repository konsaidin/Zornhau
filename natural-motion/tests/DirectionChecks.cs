using NaturalMotion;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

internal static class DirectionChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
        var config = new MotionConfig { BodyTurnDegrees = 0 };
        foreach (int fps in new[] { 30, 60, 144 })
        foreach (float yaw in new[] { 0f, MathF.PI / 2, MathF.PI, -MathF.PI / 2, 1.3f, -2.4f })
        foreach (bool sprint in new[] { false, true })
        foreach (var (name, forward, back, left, right, angle) in new[] {
            ("W", true, false, false, false, 0d), ("S", false, true, false, false, Math.PI),
            ("A", false, false, true, false, -Math.PI / 2), ("D", false, false, false, true, Math.PI / 2),
            ("W+A", true, false, true, false, -Math.PI / 4), ("W+D", true, false, false, true, Math.PI / 4),
            ("S+A", false, true, true, false, -3 * Math.PI / 4), ("S+D", false, true, false, true, 3 * Math.PI / 4)
        })
        {
            // Generate world motion with the actual installed game's input
            // implementation, not an inverse of our own direction formula.
            var controls = new EntityControls { Forward = forward, Backward = back, Left = left, Right = right,
                Sprint = sprint, MovespeedMultiplier = 1 };
            controls.CalcMovementVectors(new EntityPos { Yaw = yaw }, 1f / 60);
            var velocity = controls.WalkVector.Clone().Normalize().Mul(sprint ? 3 : 2);
            var state = new MotionState();
            for (int frame = 0; frame <= fps * 2; frame++)
                state.Update(new((long)Math.Round(frame * 1000d / fps), 0, 0, 0, 0, velocity.X, 0, velocity.Z,
                    yaw, true, false, sprint, false, false), config);
            Check(Math.Abs(Math.Cos(state.RelativeDirection) - Math.Cos(angle)) < .00001
                && Math.Abs(Math.Sin(state.RelativeDirection) - Math.Sin(angle)) < .00001,
                "native " + name + " input selects the same model-relative direction at every yaw/FPS");
            if (name == "W") Check(state.ForwardWeight > .999 && state.BackWeight < .001, "W selects forward walk/run");
            if (name == "S") Check(state.BackWeight > .999 && state.ForwardWeight < .001, "S selects backward walk/run");
            if (name == "A") Check(state.LeftWeight > .999 && state.RightWeight < .001, "A selects left walk/run");
            if (name == "D") Check(state.RightWeight > .999 && state.LeftWeight < .001, "D selects right walk/run");
            if (name is "A" or "D")
            {
                var turnConfig = new MotionConfig();
                for (int frame = 1; frame <= fps; frame++)
                    state.Update(new(2000 + (long)Math.Round(frame * 1000d / fps), 0, 0, 0, 0, velocity.X, 0,
                        velocity.Z, yaw, true, false, sprint, false, false), turnConfig);
                // Seraph faces local -X. Its renderer rotates by yaw+90°.
                // A pelvis turn must move that facing toward native strafe.
                float[] matrix = Mat4f.Create();
                Mat4f.RotateY(matrix, matrix, yaw + MathF.PI / 2 + (float)state.Turn);
                double turnedDot = -matrix[0] * velocity.X - matrix[2] * velocity.Z;
                Check(turnedDot > .1, "pelvis faces toward native " + name + " movement, not its opposite");
            }
        }
        return checks;
    }
}
