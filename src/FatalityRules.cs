using OpenTK.Mathematics;
using Vintagestory.API.Common;

namespace Zornhau;

public enum FatalityKind { None, Decapitation, HeadCrush }

internal static class FatalityRules
{
    internal const string Attribute = "zornhauFatality";
    internal const double HeadRadius = .18, FragmentRadius = .07;
    internal const int LifetimeMs = 30000, CameraMs = 6500, MaxEffects = 32;
    internal static FatalityKind Choose(bool head, EnumDamageType type, Direction direction)
        => !head ? FatalityKind.None
            : type == EnumDamageType.SlashingAttack && direction is Direction.Left or Direction.Right
                ? FatalityKind.Decapitation
            : type == EnumDamageType.BluntAttack ? FatalityKind.HeadCrush : FatalityKind.None;

    internal static bool HeadElement(string? element)
        => element != null && (element.Equals("head", StringComparison.OrdinalIgnoreCase)
            || element.Equals("skull", StringComparison.OrdinalIgnoreCase)
            || element.StartsWith("head-", StringComparison.OrdinalIgnoreCase));

    // A claimed bone name alone cannot turn a leg contact into a head effect.
    // This is a conservative humanoid plausibility check around the live eye,
    // in addition to the existing blade/target/terrain validation.
    internal static bool HeadPoint(Vector3d relative, Vector3d eye, double size)
        => BladeGeometry.Finite(relative) && BladeGeometry.Finite(eye)
            && double.IsFinite(size) && size > 0 && size <= 3
            && Math.Abs(relative.X - eye.X) <= .42 * size
            && Math.Abs(relative.Z - eye.Z) <= .42 * size
            && relative.Y >= eye.Y - .30 * size && relative.Y <= eye.Y + .30 * size;

    internal static Vector3d Impulse(Direction direction, double yaw, bool fragment)
    {
        var forward = new Vector3d(-Math.Sin(yaw), 0, -Math.Cos(yaw));
        var right = new Vector3d(Math.Cos(yaw), 0, -Math.Sin(yaw));
        var side = direction == Direction.Left ? -right : direction == Direction.Right ? right : forward;
        return side * (fragment ? 3.0 : 2.0) + forward * .8 + Vector3d.UnitY * (fragment ? 2.8 : 1.7);
    }
}

// Server-owned cosmetic rigid bodies. Fixed substeps, finite state, bounded
// travel, terrain collision, rebound, rolling friction and eventual rest.
internal sealed class FatalityBody(Vector3d position, Vector3d velocity, double radius)
{
    internal Vector3d Position = position, Velocity = velocity, Rotation, Spin = new(2.5, 1.7, 3.1);
    internal readonly double Radius = radius;
    internal bool Resting;
    internal int Impact;
    internal float ImpactSpeed;
    internal Vector3d ImpactPosition;
    private double remainder, quiet;

    internal void Advance(double seconds, System.Func<Vector3d, double, bool> solid)
    {
        if (Resting || !double.IsFinite(seconds) || seconds <= 0) return;
        remainder += Math.Min(seconds, .2);
        while (remainder >= .01)
        {
            remainder -= .01;
            Velocity.Y -= 9.81 * .01;
            bool floor = false;
            for (int axis = 0; axis < 3; axis++)
            {
                double distance = Velocity[axis] * .01;
                int slices = Math.Clamp((int)Math.Ceiling(Math.Abs(distance) / Math.Max(.02, Radius * .4)), 1, 128);
                for (int slice = 0; slice < slices; slice++)
                {
                    var next = Position; next[axis] += distance / slices;
                    if (!solid(next, Radius)) { Position = next; continue; }
                    // Report the first substantial contact, rather than the tiny
                    // repeated floor contacts while settling. It is cosmetic only.
                    if (Impact == 0 && Math.Abs(Velocity[axis]) >= 1.2)
                    {
                        Impact = 1; ImpactSpeed = (float)Math.Min(20, Math.Abs(Velocity[axis]));
                        ImpactPosition = Position;
                        ImpactPosition[axis] += Math.Sign(Velocity[axis]) * Radius;
                    }
                    if (axis == 1 && Velocity.Y < 0) floor = true;
                    Velocity[axis] *= -.22;
                    break;
                }
            }
            if (floor) { Velocity.X *= .93; Velocity.Z *= .93; Spin *= .94; }
            Rotation += Spin * .01;
            quiet = floor && Velocity.Length < .20 ? quiet + .01 : 0;
            if (quiet >= .35) { Velocity = Spin = Vector3d.Zero; Resting = true; break; }
        }
    }
}
