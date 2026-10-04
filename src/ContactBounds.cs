using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace WarbandCombat;

// A conservative plausibility bound, not a second hit detector. The client must
// still report a real CO model intersection inside the authoritative strike window.
internal readonly record struct ContactEnvelope(double Radius, double LowerY, double UpperY)
{
    internal bool Contains(Vec3d relative, double size, double margin)
        => relative.X * relative.X + relative.Z * relative.Z <= Math.Pow(Radius * size + margin, 2)
            && relative.Y >= LowerY * size - margin && relative.Y <= UpperY * size + margin;

    internal static ContactEnvelope? FromShape(Shape shape, ISet<string> colliders)
    {
        double radius = 0, lower = double.PositiveInfinity, upper = double.NegativeInfinity;
        var pose = new ElementPose();
        void Walk(ShapeElement[]? elements, float[] parent)
        {
            if (elements == null) return;
            foreach (var element in elements)
            {
                if (element.From?.Length != 3 || element.To?.Length != 3) continue;
                var matrix = Mat4f.Create();
                Mat4f.Mul(matrix, parent, element.GetLocalTransformMatrix(0, Mat4f.Create(), pose));
                if (element.Name != null && colliders.Contains(element.Name))
                {
                    for (int corner = 0; corner < 8; corner++)
                    {
                        double x = (corner & 1) == 0 ? 0 : (element.To[0] - element.From[0]) / 16;
                        double y = (corner & 2) == 0 ? 0 : (element.To[1] - element.From[1]) / 16;
                        double z = (corner & 4) == 0 ? 0 : (element.To[2] - element.From[2]) / 16;
                        double px = matrix[0] * x + matrix[4] * y + matrix[8] * z + matrix[12] - .5;
                        double py = matrix[1] * x + matrix[5] * y + matrix[9] * z + matrix[13];
                        double pz = matrix[2] * x + matrix[6] * y + matrix[10] * z + matrix[14] - .5;
                        radius = Math.Max(radius, Math.Sqrt(px * px + pz * pz));
                        lower = Math.Min(lower, py); upper = Math.Max(upper, py);
                    }
                }
                Walk(element.Children, matrix);
            }
        }
        Walk(shape.Elements, Mat4f.Create());
        return double.IsFinite(radius) && double.IsFinite(lower) && double.IsFinite(upper)
            ? new ContactEnvelope(radius, lower, upper) : null;
    }
}

internal sealed class ContactPositionHistory
{
    internal const int MaxLookback = 350;
    internal const double MaxDisplacement = 2;
    private readonly Dictionary<long, List<Sample>> histories = new();
    internal readonly record struct Sample(long Time, int Dimension, double X, double Y, double Z);

    internal void Record(long id, long now, int dimension, Vec3d pos)
    {
        if (!double.IsFinite(pos.X) || !double.IsFinite(pos.Y) || !double.IsFinite(pos.Z)) return;
        if (!histories.TryGetValue(id, out var samples)) histories[id] = samples = new();
        if (samples.Count > 0)
        {
            var last = samples[^1];
            double distance = Math.Pow(last.X - pos.X, 2) + Math.Pow(last.Y - pos.Y, 2) + Math.Pow(last.Z - pos.Z, 2);
            // Never carry hits across teleports, dimension changes or tracking gaps.
            if (last.Dimension != dimension || now - last.Time > 125 || now < last.Time || distance > MaxDisplacement * MaxDisplacement)
                samples.Clear();
            else if (now == last.Time) return;
        }
        samples.RemoveAll(p => now - p.Time > MaxLookback);
        samples.Add(new Sample(now, dimension, pos.X, pos.Y, pos.Z));
    }

    internal IEnumerable<Sample> Recent(long id, long now, int dimension, Vec3d current, int lookback)
    {
        if (!histories.TryGetValue(id, out var samples) || samples.Count == 0 || now - samples[^1].Time > 125) yield break;
        foreach (var sample in samples)
        {
            long age = now - sample.Time;
            if (sample.Dimension != dimension || age < 0 || age > Math.Clamp(lookback, 0, MaxLookback)) continue;
            double distance = Math.Pow(sample.X - current.X, 2) + Math.Pow(sample.Y - current.Y, 2) + Math.Pow(sample.Z - current.Z, 2);
            if (distance <= MaxDisplacement * MaxDisplacement) yield return sample;
        }
    }

    internal void Prune(long now)
    {
        foreach (long id in histories.Where(h => h.Value.Count == 0 || now - h.Value[^1].Time > MaxLookback).Select(h => h.Key).ToArray())
            histories.Remove(id);
    }
    internal void Clear() => histories.Clear();
    internal static int Lookback(double pingSeconds)
        => double.IsFinite(pingSeconds) ? (int)Math.Clamp(150 + Math.Max(0, pingSeconds) * 500, 150, MaxLookback) : 150;
}
