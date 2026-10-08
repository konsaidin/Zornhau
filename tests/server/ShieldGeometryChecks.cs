using Newtonsoft.Json;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Zornhau;

internal static class ShieldGeometryChecks
{
    private static string Game => Environment.GetEnvironmentVariable("VSPath") ?? Environment.GetEnvironmentVariable("VS_GAME_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "vintagestory");
    internal static MeshData Mesh(string path)
    {
        var shape = JsonConvert.DeserializeObject<Shape>(File.ReadAllText(path))!;
        foreach (var element in shape.Elements) element.TrimTextureNamesAndResolveFaces();
        // Actual engine tesselation, including nested rotations, authored offsets,
        // degenerate roots, selected faces and shield handles. No GPU needed.
        var tesselator = new ShapeTesselator(null!, null!, null!, null!);
        tesselator.TesselateShape("shield regression", shape, out var mesh, new Textures());
        return mesh;
    }

    internal static AttachmentPoint Back()
    {
        var shape = JsonConvert.DeserializeObject<Shape>(File.ReadAllText(
            Game + "/assets/game/shapes/entity/humanoid/seraph-faceless.json"))!;
        ShapeElement Torso(ShapeElement[] elements) => elements.SelectMany(e => new[] { e }
            .Concat(e.Children == null ? [] : Flatten(e.Children))).First(e => e.Name == "UpperTorso");
        static IEnumerable<ShapeElement> Flatten(ShapeElement[] elements) => elements.SelectMany(e => new[] { e }
            .Concat(e.Children == null ? [] : Flatten(e.Children)));
        return Torso(shape.Elements).AttachmentPoints.Single(p => p.Code == "Back");
    }

    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); checks++; }
        var back = Back();
        var paths = Directory.GetFiles(Game + "/assets/survival/shapes/item/tool/shield", "*.json")
            .Concat(Directory.GetFiles(".artifacts/compat/unpacked/armoryfork_1.11.3/assets/armory/shapes/shields", "*.json"));
        foreach (string path in paths)
        {
            var mesh = Mesh(path);
            Check(mesh.VerticesCount > 0, "Actual shield mesh must be populated: " + path);
            foreach (var scale in new[] { new FastVec3f(1, 1, 1), new FastVec3f(.8f, .8f, .8f), new FastVec3f(.8f, .9f, .7f) })
            {
                var native = new ModelTransform { Origin = new(.5f, 0, .5f), ScaleXYZ = scale,
                    Translation = new(.4f, -.3f, .1f), Rotation = new(99, -90, 0) }.EnsureDefaultValues();
                foreach (bool rotatedAnchor in new[] { false, true })
                {
                    var ap = new AttachmentPoint { PosX = back.PosX, PosY = back.PosY, PosZ = back.PosZ,
                        RotationX = rotatedAnchor ? 10 : 0, RotationY = rotatedAnchor ? -20 : 0, RotationZ = rotatedAnchor ? 30 : 0 };
                    var fit = ShieldBackRenderer.Transform(mesh, ap, native);
                    // Native RenderItem's exact order: scale, attachment +
                    // translation, XYZ rotations, then negative origin.
                    var matrix = new Matrixf().Scale(fit.ScaleXYZ.X, fit.ScaleXYZ.Y, fit.ScaleXYZ.Z)
                        .Translate(ap.PosX / 16 + fit.Translation.X, ap.PosY / 16 + fit.Translation.Y, ap.PosZ / 16 + fit.Translation.Z)
                        .Rotate((float)(ap.RotationX + fit.Rotation.X) * GameMath.DEG2RAD,
                            (float)(ap.RotationY + fit.Rotation.Y) * GameMath.DEG2RAD,
                            (float)(ap.RotationZ + fit.Rotation.Z) * GameMath.DEG2RAD);
                    var points = Enumerable.Range(0, mesh.VerticesCount).Select(i => BladeGeometry.Transform(matrix.Values,
                        new(mesh.xyz[i * 3], mesh.xyz[i * 3 + 1], mesh.xyz[i * 3 + 2]), Vector3d.Zero)).ToArray();
                    double[] min = Enumerable.Range(0, 3).Select(a => points.Min(p => p[a])).ToArray();
                    double[] max = Enumerable.Range(0, 3).Select(a => points.Max(p => p[a])).ToArray();
                    Check(Math.Abs(min[0] - (back.PosX / 16 + .055)) < .0001,
                        "Shield stays close to +X back surface, including its handle: " + path);
                    Check(Math.Abs((min[2] + max[2]) / 2 - back.PosZ / 16) < .0001,
                        "Shield is centered across the spine instead of floating sideways: " + path);
                    Check(max[1] <= 4.5 / 16 + .0001, "Top stays below the neck: " + path);
                    Check(max[0] - min[0] < max[1] - min[1] && max[0] - min[0] < max[2] - min[2],
                        "Shield face is upright in YZ rather than horizontal through the neck: " + path);
                    Check(fit.ScaleXYZ.X == scale.X && fit.ScaleXYZ.Y == scale.Y && fit.ScaleXYZ.Z == scale.Z,
                        "Back fitting preserves native shield size: " + path);
                }
            }
            Console.WriteLine("Shield geometry: " + Path.GetFileName(path));
        }
        Console.WriteLine($"PASS: {checks} actual vanilla/Armory shield mesh placement checks");
    }

    private sealed class Textures : ITexPositionSource
    {
        public Size2i AtlasSize => new(1024, 1024);
        public TextureAtlasPosition this[string code] => new() { x1 = 0, y1 = 0, x2 = 1, y2 = 1 };
    }
}
