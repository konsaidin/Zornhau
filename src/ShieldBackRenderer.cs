using HarmonyLib;
using System.Runtime.CompilerServices;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Zornhau;

// The shield stays in its real inventory slot. Rendering and combat derive
// stowage from the same server-owned grip, so no hidden inventory or copying.
internal static class ShieldBackRenderer
{
    private delegate void RenderItem(EntityShapeRenderer renderer, float dt, bool shadow,
        ItemStack stack, AttachmentPointAndPose pose, ItemRenderInfo info);
    private static readonly RenderItem render = AccessTools.MethodDelegate<RenderItem>(
        AccessTools.Method(typeof(EntityShapeRenderer), "RenderItem"));

    internal static void Patch(Harmony harmony)
        => harmony.Patch(AccessTools.Method(typeof(EntityShapeRenderer), "RenderHeldItem"),
            prefix: new HarmonyMethod(typeof(ShieldBackRenderer), nameof(BeforeHeld)) { priority = Priority.First });

    private sealed record Fitting(FastVec3f Rotation, Vector3d Min, Vector3d Max);
    private static readonly ConditionalWeakTable<MultiTextureMeshRef, Fitting> fittings = new();

    private static Fitting Fit(MeshData mesh)
    {
        var (min, max) = Bounds(mesh, Mat4f.Create());
        var size = max - min;
        // Seraph's Back is on the +X face of UpperTorso, not +Z.
        // Vanilla shields have their front on +Z; Armory shields on +Y,
        // with the top toward -Z. Preserve that top (kite/scutum included).
        var rotation = size.Y < size.X && size.Y < size.Z ? new FastVec3f(90, 0, -90)
            : size.Z < size.X ? new FastVec3f(0, 90, 0) : new FastVec3f(0, 0, 0);
        var matrix = new Matrixf().Rotate(rotation.X * GameMath.DEG2RAD,
            rotation.Y * GameMath.DEG2RAD, rotation.Z * GameMath.DEG2RAD);
        (min, max) = Bounds(mesh, matrix.Values);
        return new(rotation, min, max);
    }

    private static (Vector3d min, Vector3d max) Bounds(MeshData mesh, float[] matrix)
    {
        var min = new Vector3d(double.PositiveInfinity);
        var max = new Vector3d(double.NegativeInfinity);
        for (int i = 0; i < mesh.VerticesCount; i++)
        {
            var p = BladeGeometry.Transform(matrix, new(mesh.xyz[i * 3], mesh.xyz[i * 3 + 1], mesh.xyz[i * 3 + 2]), Vector3d.Zero);
            min = Vector3d.ComponentMin(min, p); max = Vector3d.ComponentMax(max, p);
        }
        return (min, max);
    }

    internal static ModelTransform Transform(MeshData mesh, AttachmentPoint back, ModelTransform native)
        => Transform(Fit(mesh), back, native);

    private static ModelTransform Transform(Fitting fit, AttachmentPoint back, ModelTransform native)
    {
        var scale = native.EnsureDefaultValues().ScaleXYZ;
        // Center across the spine and leave less than one voxel for armor.
        // The top stops half a voxel below UpperTorso's shoulder line (Y=5).
        // Larger shields extend down toward the hips, never up into the neck.
        double centerY = Math.Min(back.PosY / 16, 4.5 / 16 - (fit.Max.Y - fit.Min.Y) * scale.Y / 2);
        return new ModelTransform
        {
            Origin = new(0, 0, 0), ScaleXYZ = new(scale.X, scale.Y, scale.Z),
            Translation = new(
                (float)((back.PosX / 16 + .055) / scale.X - fit.Min.X - back.PosX / 16),
                (float)(centerY / scale.Y - (fit.Min.Y + fit.Max.Y) / 2 - back.PosY / 16),
                (float)(back.PosZ / 16 / scale.Z - (fit.Min.Z + fit.Max.Z) / 2 - back.PosZ / 16)),
            Rotation = new(fit.Rotation.X - (float)back.RotationX, fit.Rotation.Y - (float)back.RotationY,
                fit.Rotation.Z - (float)back.RotationZ)
        }.EnsureDefaultValues();
    }

    private static bool BeforeHeld(EntityShapeRenderer __instance, float dt, bool isShadowPass, bool right)
    {
        if (right || __instance.entity is not EntityPlayer player || !CombatShields.Stowed(player)) return true;
        var back = player.AnimManager?.Animator?.GetAttachmentPointPose("Back");
        if (back == null) return false;
        var slot = player.LeftHandItemSlot;
        var info = __instance.capi.Render.GetItemStackRenderInfo(slot, EnumItemRenderTarget.HandTpOff, dt);
        var original = info.Transform;
        if (info.ModelRef == null || original == null) return false;
        if (!fittings.TryGetValue(info.ModelRef, out var fit))
        {
            // Use the same per-stack mesh source as the native held renderer;
            // attribute-based vanilla shields and Armory texture behaviors
            // implement this interface; fixed vanilla shields use TesselateItem.
            // Cache by GPU mesh, not by item code.
            var source = slot.Itemstack!.Collectible.GetCollectibleInterface<IContainedMeshSource>();
            MeshData mesh;
            if (source != null) mesh = source.GenMesh(slot, __instance.capi.ItemTextureAtlas, null!);
            else __instance.capi.Tesselator.TesselateItem(slot.Itemstack.Item, out mesh);
            if (mesh == null || mesh.VerticesCount == 0) return false;
            fit = Fit(mesh);
            fittings.Add(info.ModelRef, fit);
        }
        try
        {
            info.Transform = Transform(fit, back.AttachPoint, original);
            render(__instance, dt, isShadowPass, slot.Itemstack!, back, info);
        }
        finally { info.Transform = original; }
        return false;
    }
}
