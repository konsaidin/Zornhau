using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Zornhau;

internal static class CombatAim
{
    internal static float LookPitch(float pitch)
        => Math.Clamp(pitch - GameMath.PI, -85 * GameMath.DEG2RAD, 85 * GameMath.DEG2RAD);

    // Rotate in the shoulder's parent space, rather than adding a local wrist
    // angle. Both shoulders share an X/Y pivot, preserving the two-handed grip.
    // Head, neck, Eyes attachment and player input are deliberately untouched.
    internal static void RotateArm(ElementPose pose, float radians)
    {
        var bone = pose.ForElement;
        if (bone == null || Math.Abs(radians) < .00001f) return;
        Span<float> rotation = stackalloc float[16];
        rotation.Clear();
        rotation[0] = rotation[5] = rotation[10] = rotation[15] = 1;
        Mat4f.RotateByXYZ(rotation,
            (float)(bone.RotationX + pose.degX + pose.degOffX) * GameMath.DEG2RAD,
            (float)(bone.RotationY + pose.degY + pose.degOffY) * GameMath.DEG2RAD,
            (float)(bone.RotationZ + pose.degZ + pose.degOffZ) * GameMath.DEG2RAD);
        float cos = MathF.Cos(radians), sin = MathF.Sin(radians);
        for (int column = 0; column < 3; column++)
        {
            int i = column * 4;
            float x = rotation[i], y = rotation[i + 1];
            rotation[i] = cos * x - sin * y;
            rotation[i + 1] = sin * x + cos * y;
        }
        float angleY = MathF.Asin(Math.Clamp(rotation[8], -1, 1));
        float angleX, angleZ;
        if (Math.Abs(MathF.Cos(angleY)) > .00001f)
        {
            angleX = MathF.Atan2(-rotation[9], rotation[10]);
            angleZ = MathF.Atan2(-rotation[4], rotation[0]);
        }
        else
        {
            angleX = MathF.Atan2(rotation[6], rotation[5]);
            angleZ = 0;
        }
        pose.degX = angleX * GameMath.RAD2DEG - (float)bone.RotationX - pose.degOffX;
        pose.degY = angleY * GameMath.RAD2DEG - (float)bone.RotationY - pose.degOffY;
        pose.degZ = angleZ * GameMath.RAD2DEG - (float)bone.RotationZ - pose.degOffZ;
    }
}
