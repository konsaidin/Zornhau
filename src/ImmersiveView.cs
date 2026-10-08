using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Zornhau;

internal static class ImmersiveView
{
    internal static void Patch(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(EntityPlayerShapeRenderer), "determineRenderMode"),
            prefix: new HarmonyMethod(typeof(ImmersiveView), nameof(ResolvePlayer)));
        harmony.Patch(AccessTools.Method(typeof(EntityPlayerShapeRenderer), nameof(EntityPlayerShapeRenderer.BeforeRender)),
            prefix: new HarmonyMethod(typeof(ImmersiveView), nameof(BeforeFrame)),
            postfix: new HarmonyMethod(typeof(ImmersiveView), nameof(AfterFrame)));
    }

    private static bool Local(EntityPlayerShapeRenderer renderer)
        => renderer.entity is EntityPlayer && renderer.capi.World.Player?.Entity is { } local
            && local.EntityId == renderer.entity.EntityId;

    private static void ResolvePlayer(EntityPlayerShapeRenderer __instance, ref IClientPlayer ___player)
    {
        // Vanilla resolves this cache in the base BeforeRender, after both the
        // initial mesh filtering and the derived camera-mode selection.
        if (Local(__instance)) ___player = __instance.capi.World.Player;
    }

    private static void BeforeFrame(EntityPlayerShapeRenderer __instance, RenderMode ___renderMode,
        ref bool ___previfpMode, out RenderMode __state)
    {
        __state = ___renderMode;
        if (Local(__instance) && ___renderMode != RenderMode.ThirdPerson)
            ___previfpMode = ___renderMode == RenderMode.ImmersiveFirstPerson;
    }

    private static void AfterFrame(EntityPlayerShapeRenderer __instance, RenderMode ___renderMode,
        ref bool ___previfpMode, RenderMode __state)
    {
        if (!Local(__instance)) return;
        // Vanilla handles ordinary FP <-> immersive FP, but omits the first
        // entry from ThirdPerson (startup, rejoining, camera switches).
        if (__state == RenderMode.ThirdPerson && ___renderMode == RenderMode.ImmersiveFirstPerson)
        {
            __instance.entity.MarkShapeModified();
            if (__instance.entity.AnimManager is PlayerAnimationManager animations)
                animations.OnIfpModeChanged(false, true);
        }
        if (___renderMode != RenderMode.ThirdPerson)
            ___previfpMode = ___renderMode == RenderMode.ImmersiveFirstPerson;
    }
}
