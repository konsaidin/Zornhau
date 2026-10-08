using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Zornhau;

// Aim the same arm pose that is rendered and used by the independent blade engine.
internal static class StandaloneAim
{
    private static ICoreClientAPI? api;
    private static CombatSystem? combat;
    private static readonly AccessTools.FieldRef<AnimationManager, Vintagestory.API.Common.Entities.Entity> owner
        = AccessTools.FieldRefAccess<AnimationManager, Vintagestory.API.Common.Entities.Entity>("entity");
    [ThreadStatic] private static float? pitch;
    [ThreadStatic] private static Native.Animations.PlayerItemFrame? frame;

    internal static void Patch(Harmony harmony, ICoreClientAPI client, CombatSystem system)
    {
        api = client; combat = system;
        harmony.Patch(AccessTools.Method(typeof(AnimationManager), nameof(AnimationManager.OnClientFrame)),
            prefix: new HarmonyMethod(typeof(StandaloneAim), nameof(Begin)),
            finalizer: new HarmonyMethod(typeof(StandaloneAim), nameof(End)));
        harmony.Patch(AccessTools.Method(typeof(ShapeElement), nameof(ShapeElement.GetLocalTransformMatrix)),
            prefix: new HarmonyMethod(typeof(StandaloneAim), nameof(Arm)));
    }

    private static void Begin(AnimationManager __instance, out (float? pitch, Native.Animations.PlayerItemFrame? frame) __state)
    {
        __state = (pitch, frame); pitch = null; frame = null;
        if (api == null || combat == null || owner(__instance) is not EntityPlayer player
            || player.RightHandItemSlot.Itemstack?.Collectible is not { } item
            || !CombatSystem.Supported(item) || combat.Integration?.UsesPhysicalContacts(item) == true) return;
        frame = combat.NativeAnimations?.Frame(player, api.World.ElapsedMilliseconds);
        bool local = player == api.World.Player?.Entity;
        var phase = local ? combat.Controls?.Fighter.Phase ?? Phase.Idle : (Phase)player.WatchedAttributes.GetInt("zornhauPhase");
        if (phase is not (Phase.Windup or Phase.Swing or Phase.Recovery)) return;
        float follow = 0;
        if (local && api.World.Player?.CameraMode == EnumCameraMode.FirstPerson && !api.Settings.Bool["immersiveFpMode"]
            && ReferenceEquals(__instance, player.SelfFpAnimManager))
            follow = player.Controls.IsFlying ? 1 : item.Attributes?["heldItemPitchFollow"].AsFloat(.75f) ?? .75f;
        pitch = CombatAim.LookPitch(player.Pos.Pitch) * (1 - Math.Clamp(follow, 0, 1));
    }

    private static Exception? End(Exception? __exception, (float? pitch, Native.Animations.PlayerItemFrame? frame) __state)
    { pitch = __state.pitch; frame = __state.frame; return __exception; }

    private static void Arm(ShapeElement __instance, ElementPose? tf)
    {
        if (frame.HasValue && tf != null) StandaloneAnimations.Apply(frame.Value, __instance, tf);
        if (pitch.HasValue && tf != null && __instance.Name is "UpperArmR" or "UpperArmL")
            CombatAim.RotateArm(tf, pitch.Value);
    }
    internal static void Clear(CombatSystem system)
    { if (combat == system) { api = null; combat = null; pitch = null; frame = null; } }
}
