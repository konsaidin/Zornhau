using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Zornhau;

internal static class FatalityMesh
{
    internal static IAnimator? Animator(EntityAgent entity)
        => (entity as EntityPlayer)?.TpAnimManager?.Animator ?? entity.AnimManager?.Animator;

    internal static HashSet<int> HeadJoints(EntityAgent player)
    {
        var ids = new HashSet<int>();
        var animator = Animator(player);
        var pose = animator?.GetPosebyName("Head", StringComparison.OrdinalIgnoreCase);
        if (pose == null) return ids;
        void Add(ElementPose p)
        {
            if (p.ForElement != null) ids.Add(p.ForElement.JointId);
            if (p.ChildElementPoses != null) foreach (var child in p.ChildElementPoses) Add(child);
        }
        Add(pose); return ids;
    }

    internal static HashSet<int> NeckJoints(EntityAgent player)
    {
        var pose = Animator(player)?.GetPosebyName("Neck", StringComparison.OrdinalIgnoreCase);
        return pose?.ForElement == null ? [] : [pose.ForElement.JointId];
    }

    // Sample one seeded spatial field on the original texture's texel grid.
    // Adjacent faces share the field instead of repeating a per-face stamp.
    // Four vertices per pixel preserve the existing bone/helmet face selectors.
    internal static MeshData Blood(MeshData source, HashSet<int> head, HashSet<int> neck, long seed,
        List<int>? surfaces = null, Size2i? atlasSize = null)
    {
        surfaces?.Clear();
        surfaces?.AddRange(Enumerable.Range(0, source.VerticesCount / 4));
        if (source.Rgba == null || source.Uv == null) return source;
        var result = source.Clone();
        // The engine's Clone copies normal data but drops NormalsCount. Saved
        // snapshots can therefore need their buffer restored before appending.
        if (source.Normals != null && source.NormalsCount != source.VerticesCount)
            result.Normals = source.Normals.Take(source.VerticesCount).ToArray();
        for (int face = 0; face < source.VerticesCount / 4; face++)
        {
            bool isNeck = HeadFace(source, neck, face);
            if (!isNeck && !HeadFace(source, head, face)) continue;
            var original = Filter(source, i => i == face);
            var a = new Vec3f(original.xyz[3] - original.xyz[0], original.xyz[4] - original.xyz[1], original.xyz[5] - original.xyz[2]);
            var b = new Vec3f(original.xyz[9] - original.xyz[0], original.xyz[10] - original.xyz[1], original.xyz[11] - original.xyz[2]);
            var normal = a.Cross(b);
            if (normal.Length() < .000001) continue;
            normal.Normalize();
            float Blend(float[] values, int stride, int axis, float u, float v)
                => values[axis] * (1 - u) * (1 - v) + values[stride + axis] * u * (1 - v)
                    + values[2 * stride + axis] * u * v + values[3 * stride + axis] * (1 - u) * v;
            float[] PixelCuts(int corner)
            {
                if (atlasSize == null) return Enumerable.Range(0, 9).Select(i => i / 8f).ToArray();
                float dx = (original.Uv[corner * 2] - original.Uv[0]) * atlasSize.Width;
                float dy = (original.Uv[corner * 2 + 1] - original.Uv[1]) * atlasSize.Height;
                bool horizontal = MathF.Abs(dx) >= MathF.Abs(dy);
                float start = horizontal ? original.Uv[0] * atlasSize.Width : original.Uv[1] * atlasSize.Height;
                float span = horizontal ? dx : dy;
                if (MathF.Abs(span) > 31)
                    return Enumerable.Range(0, 33).Select(i => i / 32f).ToArray();
                var cuts = new List<float> { 0, 1 };
                if (MathF.Abs(span) > .0001f)
                    for (float pixel = MathF.Ceiling(MathF.Min(start, start + span)); pixel < MathF.Max(start, start + span); pixel++)
                    {
                        float t = (pixel - start) / span;
                        // Half-texel face edges and atlas padding produce a
                        // clipped border cell, not a stretched pixel grid.
                        if (t > .0001f && t < .9999f) cuts.Add(t);
                    }
                return cuts.Order().ToArray();
            }
            var uCuts = PixelCuts(1); var vCuts = PixelCuts(3);
            int columns = uCuts.Length - 1, rows = vCuts.Length - 1;
            for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                float u = (uCuts[column] + uCuts[column + 1]) / 2, v = (vCuts[row] + vCuts[row + 1]) / 2;
                var point = new Vec3f(Blend(original.xyz, 3, 0, u, v), Blend(original.xyz, 3, 1, u, v), Blend(original.xyz, 3, 2, u, v));
                float coverage = BloodField(point, seed);
                if (coverage < (isNeck ? .44f : .53f)) continue;
                // A small fixed palette and constant color across each quad
                // give crisp texels without interpolated edges or imposed drips.
                int shade = Math.Clamp((int)((coverage - .44f) * 12), 0, 3);
                byte red = (byte)(shade switch { 0 => 176, 1 => 158, 2 => 139, _ => 119 });
                byte green = (byte)(shade switch { 0 => 18, 1 => 13, 2 => 9, _ => 7 });
                byte blue = (byte)(shade switch { 0 => 24, 1 => 20, 2 => 17, _ => 14 });
                var patch = original.Clone();
                var corners = new[] { (column, row), (column + 1, row), (column + 1, row + 1), (column, row + 1) };
                for (int vertex = 0; vertex < 4; vertex++)
                {
                    float pu = uCuts[corners[vertex].Item1], pv = vCuts[corners[vertex].Item2];
                    for (int axis = 0; axis < 3; axis++)
                        patch.xyz[vertex * 3 + axis] = Blend(original.xyz, 3, axis, pu, pv)
                            + (axis == 0 ? normal.X : axis == 1 ? normal.Y : normal.Z) * .00065f;
                    for (int axis = 0; axis < 2; axis++) patch.Uv[vertex * 2 + axis] = Blend(original.Uv, 2, axis, pu, pv);
                    // Keep the base texture's alpha (e.g. gaps in hair/helmets),
                    // but use opaque blood on solid skin. No new atlas/GPU assets.
                    patch.Rgba[vertex * 4] = red;
                    patch.Rgba[vertex * 4 + 1] = green;
                    patch.Rgba[vertex * 4 + 2] = blue;
                }
                patch.Indices = [0, 1, 2, 0, 2, 3]; patch.IndicesCount = 6;
                result.AddMeshData(patch);
                surfaces?.Add(face);
            }
        }
        if (result.Normals != null) result.NormalsCount = result.VerticesCount;
        return result;
    }

    internal static float BloodField(Vec3f point, long seed)
        => .66f * BloodNoise(point, 9, seed) + .23f * BloodNoise(point, 21, seed ^ 0x51ed270b)
            + .11f * BloodNoise(point, 47, seed ^ 0x68bc21eb);

    private static float BloodNoise(Vec3f point, float frequency, long seed)
    {
        float x = point.X * frequency, y = point.Y * frequency, z = point.Z * frequency;
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y), iz = (int)MathF.Floor(z);
        float Smooth(float t) => t * t * (3 - 2 * t);
        float Lerp(float a, float b, float t) => a + (b - a) * t;
        float Hash(int a, int b, int c)
        {
            // Mix all 64 bits of the victim seed and spatial coordinates so
            // different sides and victims never reuse a central wound template.
            ulong state = unchecked((ulong)seed ^ (ulong)((long)a * 73856093)
                ^ (ulong)((long)b * 19349663) ^ (ulong)((long)c * 83492791));
            state = unchecked((state ^ (state >> 30)) * 0xbf58476d1ce4e5b9UL);
            state = unchecked((state ^ (state >> 27)) * 0x94d049bb133111ebUL);
            state ^= state >> 31;
            return (state & 0xffffff) / 16777216f;
        }
        float tx = Smooth(x - ix), ty = Smooth(y - iy), tz = Smooth(z - iz);
        return Lerp(Lerp(Lerp(Hash(ix, iy, iz), Hash(ix + 1, iy, iz), tx),
                Lerp(Hash(ix, iy + 1, iz), Hash(ix + 1, iy + 1, iz), tx), ty),
            Lerp(Lerp(Hash(ix, iy, iz + 1), Hash(ix + 1, iy, iz + 1), tx),
                Lerp(Hash(ix, iy + 1, iz + 1), Hash(ix + 1, iy + 1, iz + 1), tx), ty), tz);
    }

    internal static MeshData Filter(MeshData source, System.Func<int, bool> face)
    {
        var selected = Enumerable.Range(0, source.VerticesCount / 4).Where(face).ToHashSet();
        var result = source.EmptyClone(); result.AddMeshData(source, selected.Contains);
        if (result.Normals != null) result.NormalsCount = result.VerticesCount;
        // The engine's face selector treats byte InterleaveStride as an element
        // count when copying custom attributes. Copy the selected vertices with
        // the actual number of values instead, preserving their animation bones.
        void Copy(int count, Action<int> add)
        {
            int stride = source.VerticesCount == 0 ? 0 : count / source.VerticesCount;
            foreach (int f in selected.Order())
                for (int vertex = f * 4; vertex < f * 4 + 4; vertex++)
                    for (int component = 0; component < stride; component++) add(vertex * stride + component);
        }
        if (source.CustomInts != null)
        {
            result.CustomInts = source.CustomInts.EmptyClone();
            Copy(source.CustomInts.Count, i => result.CustomInts.Add(source.CustomInts.Values[i]));
        }
        if (source.CustomFloats != null)
        {
            result.CustomFloats = source.CustomFloats.EmptyClone();
            Copy(source.CustomFloats.Count, i => result.CustomFloats.Add(source.CustomFloats.Values[i]));
        }
        if (source.CustomShorts != null)
        {
            result.CustomShorts = source.CustomShorts.EmptyClone();
            Copy(source.CustomShorts.Count, i => result.CustomShorts.Add(source.CustomShorts.Values[i]));
        }
        if (source.CustomBytes != null)
        {
            result.CustomBytes = source.CustomBytes.EmptyClone();
            Copy(source.CustomBytes.Count, i => result.CustomBytes.Add(source.CustomBytes.Values[i]));
        }
        return result;
    }

    internal static bool HeadFace(MeshData source, HashSet<int> ids, int face)
        => source.CustomInts?.Values is { } joints && face * 4 < joints.Length && ids.Contains(joints[face * 4]);

    internal static bool TextureFace(MeshData source, int face, TextureAtlasPosition[] regions)
    {
        if (source.Uv == null || source.TextureIds == null || source.TextureIndices == null) return false;
        int texture = source.TextureIds[source.TextureIndices[face]];
        return regions.Any(region => region.atlasTextureId == texture && Enumerable.Range(0, 4).All(i =>
        {
            int uv = (face * 4 + i) * 2;
            return source.Uv[uv] >= region.x1 - .00001 && source.Uv[uv] <= region.x2 + .00001
                && source.Uv[uv + 1] >= region.y1 - .00001 && source.Uv[uv + 1] <= region.y2 + .00001;
        }));
    }

    // Stains inherit their original surface's ownership. Selecting individual
    // overlay sectors as independent fragments would leave blood floating where
    // the helmet face used to be, or throw it onto a different moving fragment.
    internal static int[] HelmetParts(MeshData mesh, HashSet<int> head, TextureAtlasPosition[] regions, IReadOnlyList<int> surfaces)
    {
        var parts = Enumerable.Repeat(-1, mesh.VerticesCount / 4).ToArray();
        var owners = new Dictionary<int, int>();
        for (int face = 0; face < parts.Length; face++)
        {
            if (!HeadFace(mesh, head, face) || !TextureFace(mesh, face, regions)) continue;
            int surface = surfaces[face];
            if (!owners.TryGetValue(surface, out int part))
                owners.Add(surface, part = FragmentPart(owners.Count));
            parts[face] = part;
        }
        return parts;
    }

    internal static TextureAtlasPosition[] HelmetRegions(ICoreClientAPI api, EntityAgent player, string prefix)
        => prefix.Length == 0 ? [] : player.Properties.Client.Textures
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Value.Baked != null)
            .Select(kv => api.EntityTextureAtlas.Positions[kv.Value.Baked.TextureSubId]).ToArray();

    // Bake the exact animated pose into a static mesh before the corpse's death
    // animation progresses. The shader joint attribute is then removed.
    internal static void Bake(MeshData mesh, float[] joints, float[] model, Vec3d camera, Vec3d pivot)
    {
        for (int vertex = 0; vertex < mesh.VerticesCount; vertex++)
        {
            int joint = mesh.CustomInts?.Values[vertex] ?? 0, offset = joint * 16;
            float x = mesh.xyz[vertex * 3], y = mesh.xyz[vertex * 3 + 1], z = mesh.xyz[vertex * 3 + 2];
            if (offset >= 0 && offset + 15 < joints.Length)
            {
                float ax = joints[offset] * x + joints[offset + 4] * y + joints[offset + 8] * z + joints[offset + 12];
                float ay = joints[offset + 1] * x + joints[offset + 5] * y + joints[offset + 9] * z + joints[offset + 13];
                z = joints[offset + 2] * x + joints[offset + 6] * y + joints[offset + 10] * z + joints[offset + 14]; x = ax; y = ay;
            }
            mesh.xyz[vertex * 3] = (float)(model[0] * x + model[4] * y + model[8] * z + model[12] + camera.X - pivot.X);
            mesh.xyz[vertex * 3 + 1] = (float)(model[1] * x + model[5] * y + model[9] * z + model[13] + camera.Y - pivot.Y);
            mesh.xyz[vertex * 3 + 2] = (float)(model[2] * x + model[6] * y + model[10] * z + model[14] + camera.Z - pivot.Z);
        }
        mesh.CustomInts = null; mesh.CustomFloats = null;
        // Rebuild lighting normals after pose baking and subsequent deformation.
        RecalculateNormals(mesh);
    }

    // Keep most of the dented shell on the corpse; three small groups of
    // actual helmet faces become fragments, without duplicating the shell.
    internal static int FragmentPart(int helmetFace) => helmetFace % 9 < 3 ? helmetFace % 9 : -1;

    internal static void Recenter(MeshData mesh)
    {
        if (mesh.VerticesCount == 0) return;
        for (int axis = 0; axis < 3; axis++)
        {
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (int vertex = 0; vertex < mesh.VerticesCount; vertex++)
            { float value = mesh.xyz[vertex * 3 + axis]; min = Math.Min(min, value); max = Math.Max(max, value); }
            float center = (min + max) / 2;
            for (int vertex = 0; vertex < mesh.VerticesCount; vertex++) mesh.xyz[vertex * 3 + axis] -= center;
        }
    }

    internal static void Crush(MeshData mesh, System.Func<int, bool> face)
    {
        var selected = Enumerable.Range(0, mesh.VerticesCount / 4).Where(f => face(f)).ToArray();
        if (selected.Length == 0) return;
        float bottom = selected.Min(f => Enumerable.Range(0, 4).Min(i => mesh.xyz[(f * 4 + i) * 3 + 1]));
        float top = selected.Max(f => Enumerable.Range(0, 4).Max(i => mesh.xyz[(f * 4 + i) * 3 + 1]));
        float height = Math.Max(.01f, top - bottom);
        foreach (int f in selected)
        {
            for (int i = 0; i < 4; i++)
            {
                int v = (f * 4 + i) * 3;
                // Deform the face, skull and any attached head geometry as one
                // unit. Preserve the neck seam and avoid affecting the torso.
                float amount = Math.Clamp((mesh.xyz[v + 1] - bottom) / height, 0, 1);
                mesh.xyz[v + 1] -= amount * height * .35f;
                mesh.xyz[v] += amount * height * .14f;
                mesh.xyz[v + 2] += amount * height * .10f;
            }
        }
        RecalculateNormals(mesh);
    }

    internal static void RecalculateNormals(MeshData mesh)
    {
        if (mesh.Normals != null)
        {
            if (mesh.Normals.Length < mesh.VerticesCount) mesh.Normals = new int[mesh.VerticesCount];
            mesh.NormalsCount = mesh.VerticesCount;
        }
        for (int face = 0; face < mesh.VerticesCount / 4; face++)
        {
            int i = face * 12;
            var a = new Vec3f(mesh.xyz[i + 3] - mesh.xyz[i], mesh.xyz[i + 4] - mesh.xyz[i + 1], mesh.xyz[i + 5] - mesh.xyz[i + 2]);
            var b = new Vec3f(mesh.xyz[i + 6] - mesh.xyz[i], mesh.xyz[i + 7] - mesh.xyz[i + 1], mesh.xyz[i + 8] - mesh.xyz[i + 2]);
            var normal = a.Cross(b);
            float length = MathF.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
            if (!float.IsFinite(length) || length < .000001f) continue;
            normal.X /= length; normal.Y /= length; normal.Z /= length;
            int packed = VertexFlags.PackNormal(normal.X, normal.Y, normal.Z);
            for (int corner = 0; corner < 4; corner++)
            {
                int vertex = face * 4 + corner;
                if (mesh.Flags != null) mesh.Flags[vertex] = (mesh.Flags[vertex] & ~VertexFlags.NormalBitMask) | packed;
                if (mesh.Normals != null) mesh.Normals[vertex] = NormalUtil.PackNormal(normal.X, normal.Y, normal.Z);
            }
        }
    }
}
