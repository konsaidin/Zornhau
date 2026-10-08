using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Zornhau;

internal class ModelContactBounds(ICoreAPI api, System.Func<Entity, ISet<string>>? colliderNames = null)
{
    private sealed record Geometry(Shape? Shape, ContactEnvelope? Envelope);
    private readonly ConditionalWeakTable<Entity, Geometry> geometries = new();
    private readonly Dictionary<AssetLocation, Shape?> assetShapes = new();
    private readonly ContactPositionHistory history = new();
    internal void Record(Entity entity, long now) => history.Record(entity.EntityId, now, entity.Pos.Dimension, entity.Pos.XYZ);
    internal void Prune(long now) => history.Prune(now);
    internal void Clear() { geometries.Clear(); assetShapes.Clear(); history.Clear(); }

    internal IEnumerable<Vec3d> Origins(Entity entity, long now, double pingSeconds)
    {
        yield return entity.Pos.XYZ;
        foreach (var sample in history.Recent(entity.EntityId, now, entity.Pos.Dimension, entity.Pos.XYZ,
            ContactPositionHistory.Lookback(pingSeconds))) yield return new(sample.X, sample.Y, sample.Z);
    }

    private Geometry GeometryFor(Entity target)
    {
        var client = target.Properties.Client;
        var shape = client?.LoadedShapeForEntity ?? client?.LoadedShape;
        if (shape?.Elements == null && client?.Shape?.Base is { } path)
        {
            var location = path.CopyWithPath("shapes/" + path.Path + ".json");
            if (!assetShapes.TryGetValue(location, out shape))
            {
                shape = api.Assets.TryGet(location)?.ToObject<Shape>();
                assetShapes[location] = shape;
            }
        }
        if (geometries.TryGetValue(target, out var cached) && ReferenceEquals(cached.Shape, shape)) return cached;
        foreach (var element in shape?.Elements ?? Array.Empty<ShapeElement>()) element.TrimTextureNamesAndResolveFaces();
        var configured = colliderNames?.Invoke(target) ?? (shape == null ? new HashSet<string>()
            : BladeGeometry.Elements(shape.Elements, Mat4f.Create()).Where(e => BladeGeometry.BodyElement(e.element))
                .Select(e => e.element.Name!).ToHashSet(StringComparer.Ordinal));
        var envelope = shape != null && configured.Count > 0
            ? ContactEnvelope.FromShape(shape, configured) : null;
        var geometry = new Geometry(shape, envelope);
        geometries.Remove(target); geometries.Add(target, geometry);
        return geometry;
    }

    internal bool Validate(Entity target, Vec3d point, long now, double pingSeconds, out string detail)
    {
        var envelope = GeometryFor(target).Envelope;
        float size = target.Properties.Client?.Size ?? 1;
        if (!float.IsFinite(size) || size <= 0) size = 1;
        var position = target.Pos.XYZ;
        if (At(position)) { detail = Describe("current", 0); return true; }
        int lookback = ContactPositionHistory.Lookback(pingSeconds);
        foreach (var sample in history.Recent(target.EntityId, now, target.Pos.Dimension, position, lookback))
        {
            if (!At(new Vec3d(sample.X, sample.Y, sample.Z))) continue;
            detail = Describe("history", now - sample.Time); return true;
        }
        detail = Describe("outside", 0); return false;

        bool At(Vec3d origin)
        {
            var relative = point.SubCopy(origin);
            // Preserve previously accepted box contacts, including animation offsets.
            if (InBox(target.CollisionBox, relative) || InBox(target.SelectionBox, relative)) return true;
            return envelope?.Contains(relative, size, .75) == true;
        }
        string Describe(string source, long age)
            => FormattableString.Invariant($"bounds={source} age={age}ms delta=({point.X-position.X:F3},{point.Y-position.Y:F3},{point.Z-position.Z:F3}) modelRadius={(envelope?.Radius ?? 0)*size:F3} modelY=({(envelope?.LowerY ?? 0)*size:F3},{(envelope?.UpperY ?? 0)*size:F3}) lookback={ContactPositionHistory.Lookback(pingSeconds)}ms");
    }

    private static bool InBox(Cuboidf? box, Vec3d point)
        => box != null && point.X >= box.X1 - .75 && point.X <= box.X2 + .75
            && point.Y >= box.Y1 - .75 && point.Y <= box.Y2 + .75
            && point.Z >= box.Z1 - .75 && point.Z <= box.Z2 + .75;
}
