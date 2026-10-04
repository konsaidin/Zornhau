using WarbandCombat;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

internal static class AimChecks
{
    internal static void Run()
    {
        int count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        foreach (float pitch in new[] { -85f, -60f, -25f, 0f, 25f, 60f, 85f })
        foreach (float x in new[] { -130f, -35f, 0f, 45f, 180f })
        foreach (float y in new[] { -90f, -55f, 0f, 55f, 90f, 180f })
        foreach (float z in new[] { -120f, 0f, 75f })
        {
            var bone = new ShapeElement { Name = "UpperArmR", RotationX = 10, RotationZ = 7,
                RotationOrigin = new[] { 2.0, 4.7, .8 }, From = new[] { 1.0, -2.1, -1.2 } };
            var pose = new ElementPose { ForElement = bone, degX = x - 10, degY = y, degZ = z - 7,
                translateX = .02f, translateY = -.01f, translateZ = .04f };
            float[] before = bone.GetLocalTransformMatrix(0, Mat4f.Create(), pose);
            float angle = CombatAim.LookPitch(GameMath.PI + pitch * GameMath.DEG2RAD);
            float[] rotation = Mat4f.Create();
            Mat4f.Translate(rotation, rotation, 2f / 16, 4.7f / 16, 0);
            Mat4f.RotateZ(rotation, rotation, angle);
            Mat4f.Translate(rotation, rotation, -2f / 16, -4.7f / 16, 0);
            var expected = Mat4f.Mul(Mat4f.Create(), rotation, before);
            CombatAim.RotateArm(pose, angle);
            var actual = bone.GetLocalTransformMatrix(0, Mat4f.Create(), pose);
            Check(actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) < .0006f),
                $"Shoulder aim must match the engine's rigid pitch rotation, including gimbal poses: {pitch}/{x}/{y}/{z}");
        }
        Check(CombatAim.LookPitch(GameMath.PI) == 0, "Neutral look preserves authored weapon pose");
        Check(CombatAim.LookPitch(GameMath.PI + 2) <= 85 * GameMath.DEG2RAD,
            "Extreme look is bounded without changing player pitch");
        Console.WriteLine($"PASS: {count} look-directed arm geometry checks");
    }
}
