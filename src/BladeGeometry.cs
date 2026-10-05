using OpenTK.Mathematics;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace WarbandCombat;

internal readonly record struct BladeSegment(Vector3d Tail, Vector3d Tip)
{
    internal double Length => (Tip - Tail).Length;
    internal bool Finite => BladeGeometry.Finite(Tail) && BladeGeometry.Finite(Tip);
    internal double[] Pack() => new[] { Tail.X, Tail.Y, Tail.Z, Tip.X, Tip.Y, Tip.Z };
    internal static BladeSegment Unpack(double[] values)
        => new(new(values[0], values[1], values[2]), new(values[3], values[4], values[5]));
    internal static BladeSegment Lerp(BladeSegment a, BladeSegment b, double fraction)
        => new(a.Tail + (b.Tail - a.Tail) * fraction, a.Tip + (b.Tip - a.Tip) * fraction);
}

internal readonly record struct BladePiece(BladeSegment Segment, double Radius);

// An oriented cuboid from a visible shape element in its actual animated pose.
internal readonly record struct PhysicalBox(Vector3d Center, Vector3d X, Vector3d Y, Vector3d Z, Vector3d Half)
{
    internal static PhysicalBox? FromMatrix(float[] matrix, ShapeElement element, Vector3d origin)
    {
        var x = new Vector3d(matrix[0], matrix[1], matrix[2]);
        var y = new Vector3d(matrix[4], matrix[5], matrix[6]);
        var z = new Vector3d(matrix[8], matrix[9], matrix[10]);
        if (x.Length < 1e-8 || y.Length < 1e-8 || z.Length < 1e-8) return null;
        if (element.To?.Length != 3 || element.From?.Length != 3) return null;
        var extent = new Vector3d((element.To[0] - element.From[0]) / 16,
            (element.To[1] - element.From[1]) / 16, (element.To[2] - element.From[2]) / 16);
        var center = BladeGeometry.Transform(matrix, extent / 2, origin);
        return new(center, x.Normalized(), y.Normalized(), z.Normalized(),
            new(Math.Abs(extent.X * x.Length / 2), Math.Abs(extent.Y * y.Length / 2), Math.Abs(extent.Z * z.Length / 2)));
    }

    internal static PhysicalBox FromCuboid(Cuboidf box, Vector3d origin)
        => new(origin + new Vector3d((box.X1 + box.X2) / 2, (box.Y1 + box.Y2) / 2, (box.Z1 + box.Z2) / 2),
            Vector3d.UnitX, Vector3d.UnitY, Vector3d.UnitZ,
            new(Math.Abs(box.X2 - box.X1) / 2, Math.Abs(box.Y2 - box.Y1) / 2, Math.Abs(box.Z2 - box.Z1) / 2));

    internal Vector3d Local(Vector3d world)
    {
        var relative = world - Center;
        return new(Vector3d.Dot(relative, X), Vector3d.Dot(relative, Y), Vector3d.Dot(relative, Z));
    }

    internal bool Contact(BladeSegment blade, double radius, out double parameter, out Vector3d point)
    {
        var start = Local(blade.Tail); var end = Local(blade.Tip);
        for (int i = 0; i < 3; i++)
            if (Math.Min(start[i], end[i]) > Half[i] + radius || Math.Max(start[i], end[i]) < -Half[i] - radius)
            { parameter = 0; point = Vector3d.Zero; return false; }
        bool hit = BladeGeometry.CapsuleBox(start, end, Half, radius, out parameter, out var local);
        point = Center + X * local.X + Y * local.Y + Z * local.Z;
        return hit;
    }
}

internal static class BladeGeometry
{
    internal const double SweepStep = .02;
    internal static bool Finite(Vector3d value)
        => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
    internal static Vector3d Transform(float[] matrix, Vector3d value, Vector3d origin)
        => origin + new Vector3d(matrix[0] * value.X + matrix[4] * value.Y + matrix[8] * value.Z + matrix[12],
            matrix[1] * value.X + matrix[5] * value.Y + matrix[9] * value.Z + matrix[13],
            matrix[2] * value.X + matrix[6] * value.Y + matrix[10] * value.Z + matrix[14]);
    internal static double Scale(float[] matrix)
        => Math.Max(new Vector3d(matrix[0], matrix[1], matrix[2]).Length,
            Math.Max(new Vector3d(matrix[4], matrix[5], matrix[6]).Length, new Vector3d(matrix[8], matrix[9], matrix[10]).Length));
    internal static int SweepSteps(BladeSegment previous, BladeSegment current)
        => Math.Clamp((int)Math.Ceiling(Math.Max((current.Tail - previous.Tail).Length,
            (current.Tip - previous.Tip).Length) / SweepStep), 1, 128);

    // Exact segment-to-box distance on each interval between the six slab crossings.
    // Solve the radius inequality rather than expanding the box (which fattens corners).
    internal static bool CapsuleBox(Vector3d start, Vector3d end, Vector3d half, double radius,
        out double parameter, out Vector3d point)
    {
        parameter = 0; point = Vector3d.Zero;
        if (!Finite(start) || !Finite(end) || !Finite(half) || !double.IsFinite(radius) || radius < 0) return false;
        var direction = end - start;
        Span<double> crossings = stackalloc double[8]; int count = 2;
        crossings[0] = 0; crossings[1] = 1;
        for (int axis = 0; axis < 3; axis++)
        {
            if (Math.Abs(direction[axis]) < 1e-12) continue;
            for (int sign = -1; sign <= 1; sign += 2)
            {
                double t = (sign * half[axis] - start[axis]) / direction[axis];
                if (t > 0 && t < 1) crossings[count++] = t;
            }
        }
        crossings[..count].Sort();
        for (int interval = 0; interval < count - 1; interval++)
        {
            double lo = crossings[interval], hi = crossings[interval + 1], mid = (lo + hi) / 2;
            double a = 0, b = 0, c = -radius * radius;
            for (int axis = 0; axis < 3; axis++)
            {
                double v = start[axis] + direction[axis] * mid;
                if (v >= -half[axis] && v <= half[axis]) continue;
                double offset = start[axis] - Math.CopySign(half[axis], v);
                a += direction[axis] * direction[axis]; b += 2 * offset * direction[axis]; c += offset * offset;
            }
            double t = lo;
            if ((a * lo + b) * lo + c > 1e-12)
            {
                if (a < 1e-16) continue;
                double discriminant = b * b - 4 * a * c;
                if (discriminant < -1e-12) continue;
                t = Math.Max(lo, (-b - Math.Sqrt(Math.Max(0, discriminant))) / (2 * a));
                if (t > hi + 1e-10 || (a * t + b) * t + c > 1e-9) continue;
            }
            parameter = Math.Clamp(t, 0, 1);
            var closest = start + direction * parameter;
            point = new(Math.Clamp(closest.X, -half.X, half.X), Math.Clamp(closest.Y, -half.Y, half.Y), Math.Clamp(closest.Z, -half.Z, half.Z));
            return true;
        }
        return false;
    }

    internal static bool Visible(ShapeElement element)
        => element.From?.Length == 3 && element.To?.Length == 3
            && Enumerable.Range(0, 3).All(i => double.IsFinite(element.From[i]) && double.IsFinite(element.To[i])
                && element.To[i] > element.From[i])
            && element.FacesResolved?.Any(face => face?.Enabled == true) == true;

    internal static bool BodyElement(ShapeElement element)
        => Visible(element) && !new[] { "itemanchor", "weapon", "attachment" }.Any(s => (element.Name ?? "").Contains(s, StringComparison.OrdinalIgnoreCase));

    internal static IEnumerable<(ShapeElement element, float[] matrix)> Elements(ShapeElement[]? elements, float[] parent)
    {
        if (elements == null) yield break;
        foreach (var element in elements)
        {
            if (element.From?.Length != 3 || element.To?.Length != 3) continue;
            var matrix = Mat4f.Create();
            Mat4f.Mul(matrix, parent, element.GetLocalTransformMatrix(0, Mat4f.Create(), new ElementPose()));
            yield return (element, matrix);
            foreach (var child in Elements(element.Children, matrix)) yield return child;
        }
    }
}
