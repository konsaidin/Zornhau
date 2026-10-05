using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace WarbandCombat;

internal static class FatalityMesh
{
    internal static HashSet<int> HeadJoints(EntityPlayer player)
    {
        var ids = new HashSet<int>();
        var animator = player.TpAnimManager?.Animator ?? player.AnimManager?.Animator;
        var pose = animator?.GetPosebyName("Head", StringComparison.OrdinalIgnoreCase);
        if (pose == null) return ids;
        void Add(ElementPose p)
        {
            if (p.ForElement != null) ids.Add(p.ForElement.JointId);
            if (p.ChildElementPoses != null) foreach (var child in p.ChildElementPoses) Add(child);
        }
        Add(pose); return ids;
    }

    internal static MeshData Filter(MeshData source, System.Func<int, bool> face)
    { var result = source.EmptyClone(); result.AddMeshData(source, i => face(i)); return result; }

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

    internal static TextureAtlasPosition[] HelmetRegions(ICoreClientAPI api, EntityPlayer player, string prefix)
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
