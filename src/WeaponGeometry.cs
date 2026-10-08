using OpenTK.Mathematics;
using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace Zornhau;

internal sealed class WeaponGeometry(ICoreAPI api)
{
    private readonly Dictionary<int, BladePiece[]> profiles = new();
    internal BladePiece[] For(CollectibleObject item)
    {
        if (profiles.TryGetValue(item.Id, out var cached)) return cached;
        var composite = (item as Item)?.Shape ?? (item as Block)?.Shape;
        var location = composite?.Base?.CopyWithPath("shapes/" + composite.Base.Path + ".json");
        var shape = location == null ? null : api.Assets.TryGet(location)?.ToObject<Shape>();
        var pieces = shape == null ? Array.Empty<BladePiece>() : FromShape(shape, composite!);
        profiles[item.Id] = pieces;
        return pieces;
    }

    internal static BladePiece[] FromShape(Shape shape, CompositeShape composite)
    {
        foreach (var element in shape.Elements ?? Array.Empty<ShapeElement>()) element.TrimTextureNamesAndResolveFaces();
        // Same centered composite transform as item tessellation, before the held-item transform.
        var compositeMatrix = new Matrixf().Identity().Translate(.5 + composite.offsetX, .5 + composite.offsetY, .5 + composite.offsetZ)
            .RotateX(composite.rotateX * GameMath.DEG2RAD).RotateY(composite.rotateY * GameMath.DEG2RAD)
            .RotateZ(composite.rotateZ * GameMath.DEG2RAD).Scale(composite.Scale, composite.Scale, composite.Scale).Translate(-.5, -.5, -.5);
        var all = BladeGeometry.Elements(shape.Elements, compositeMatrix.Values).Where(e => BladeGeometry.Visible(e.element)).ToArray();
        var damaging = all.Where(e => Damaging(e.element)).ToArray();
        // Models with generic cube names still work: exclude obvious handles and accessories.
        if (damaging.Length == 0) damaging = all.Where(e => !Accessory(e.element.Name ?? "")).ToArray();
        return damaging.Take(96).Select(e => Piece(e.element, e.matrix)).ToArray();
    }

    private static bool Accessory(string name)
        => new[] { "handle", "haft", "shaft", "guard", "socket", "tang", "string", "strap", "rivet", "nail", "pin", "pommel", "cloth", "medallion", "wing", "reinforce" }
            .Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));
    private static bool Damaging(ShapeElement element)
    {
        string name = element.Name ?? "";
        if (Accessory(name)) return false;
        if (new[] { "blade", "edge", "razor", "segment", "point", "head", "spike", "cross", "center" }
            .Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) return true;
        return element.FacesResolved?.Any(f => f?.Enabled == true && f.Texture != null
            && !new[] { "handle", "wood", "string", "cloth", "leather", "guard" }
                .Any(s => f.Texture.Contains(s, StringComparison.OrdinalIgnoreCase))) == true;
    }

    private static BladePiece Piece(ShapeElement element, float[] matrix)
    {
        var extent = new Vector3d((element.To![0] - element.From![0]) / 16,
            (element.To[1] - element.From[1]) / 16, (element.To[2] - element.From[2]) / 16);
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        var tail = extent / 2; var tip = tail; tail[axis] = 0; tip[axis] = extent[axis];
        double radius = Math.Sqrt(Enumerable.Range(0, 3).Where(i => i != axis).Sum(i => extent[i] * extent[i])) / 2;
        return new(new(BladeGeometry.Transform(matrix, tail, Vector3d.Zero), BladeGeometry.Transform(matrix, tip, Vector3d.Zero)),
            radius * BladeGeometry.Scale(matrix));
    }

    internal static double HandScale(CollectibleObject item)
        => Math.Max(TransformScale(item.TpHandTransform), TransformScale(item.FpHandTransform));
    private static double TransformScale(ModelTransform? transform)
    {
        if (transform == null) return 1;
        transform = transform.Clone().EnsureDefaultValues();
        return Math.Max(Math.Abs(transform.ScaleXYZ.X), Math.Max(Math.Abs(transform.ScaleXYZ.Y), Math.Abs(transform.ScaleXYZ.Z)));
    }
    internal static double Reach(CollectibleObject item, BladePiece[] pieces)
        => Math.Clamp((pieces.Length == 0 ? 0 : pieces.Max(p => Math.Max((p.Segment.Tail - new Vector3d(.5)).Length,
            (p.Segment.Tip - new Vector3d(.5)).Length) + p.Radius)) * HandScale(item) + 1.6, 1.5, 6);
    internal void Clear() => profiles.Clear();
}
