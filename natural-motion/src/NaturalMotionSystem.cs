using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace NaturalMotion;

public sealed class NaturalMotionSystem : ModSystem
{
    private ICoreClientAPI? api;
    private Harmony? harmony;
    private MotionConfig config = new();
    private readonly ConditionalWeakTable<EntityPlayer, MotionState> states = new();
    private readonly ConditionalWeakTable<IAnimator, StockPoses> sources = new();
    private readonly ConditionalWeakTable<IAnimator, NativeLocomotion> native = new();
    private static NaturalMotionSystem? active;
    [ThreadStatic] private static Context? context;
    private sealed record Context(MotionState State, StockPoses Source, NativeLocomotion Native, IAnimator Animator,
        bool Body, bool EmptyHands, bool Walk, bool Air);
    private static readonly AccessTools.FieldRef<AnimationManager, Entity> owner = AccessTools.FieldRefAccess<AnimationManager, Entity>("entity");

    public override void StartClientSide(ICoreClientAPI client)
    {
        api = client; active = this;
        config = client.LoadModConfig<MotionConfig>("natural-motion.json") ?? new(); config.Sanitize();
        client.StoreModConfig(config, "natural-motion.json");
        harmony = new Harmony("naturalmotion.poses");
        harmony.Patch(AccessTools.Method(typeof(AnimationManager), nameof(AnimationManager.OnClientFrame)),
            prefix: new HarmonyMethod(typeof(NaturalMotionSystem), nameof(Begin)),
            finalizer: new HarmonyMethod(typeof(NaturalMotionSystem), nameof(End)));
        harmony.Patch(AccessTools.Method(typeof(ShapeElement), nameof(ShapeElement.GetLocalTransformMatrix)),
            prefix: new HarmonyMethod(typeof(NaturalMotionSystem), nameof(Pose)) { priority = Priority.Last });
        client.Event.LeaveWorld += Clear;
        client.Logger.Notification("[Natural Motion] Directional steps and airborne poses ready; client visuals only.");
    }

    private static void Begin(AnimationManager __instance, out object? __state)
    {
        __state = context; context = null;
        var mod = active; var api = mod?.api;
        if (mod == null || api == null || !mod.config.Enabled || api.IsGamePaused || __instance.RunWhilePaused
            || owner(__instance) is not EntityPlayer player || !player.Alive || __instance.Animator == null) return;
        bool firstPerson = ReferenceEquals(__instance, player.SelfFpAnimManager);
        if (firstPerson && api.World.Player?.ImmersiveFpMode != true) return;
        var library = mod.sources.GetValue(__instance.Animator, animator => new StockPoses(animator.Animations.Select(a => a.Animation)));
        if (!library.Available) return;
        bool combat = player.WatchedAttributes.GetInt("warbandPhase") is >= 1 and <= 4
            || __instance.ActiveAnimationsByAnimCode.Keys.Any(code => code.StartsWith("warband-", StringComparison.Ordinal));
        bool excluded = player.MountedOn != null || player.Swimming || player.FeetInLiquid
            || player.Controls.IsClimbing || player.Controls.Gliding || player.Controls.FloorSitting
            || player.Player?.WorldData.FreeMove == true;
        var pos = player.Pos;
        var state = mod.states.GetOrCreateValue(player);
        state.Update(new(api.World.ElapsedMilliseconds, pos.X, pos.Y, pos.Z, pos.Dimension,
            pos.Motion.X * 60, pos.Motion.Y * 60, pos.Motion.Z * 60, player.BodyYaw,
            player.OnGround, player.Controls.Sneak, player.Controls.Sprint, combat, excluded), mod.config);
        bool fadingNative = __instance.Animator.Animations.Any(a => a.Running
            && NativeLocomotion.Owns(a.Animation.Code, mod.config.DirectionalSteps, mod.config.AirbornePose));
        if (!excluded && (state.Active || fadingNative))
            context = new(state, library, mod.native.GetOrCreateValue(__instance.Animator), __instance.Animator, !firstPerson,
                player.RightHandItemSlot?.Empty != false && player.LeftHandItemSlot?.Empty != false && !combat,
                mod.config.DirectionalSteps, mod.config.AirbornePose);
    }

    private static Exception? End(Exception? __exception, object? __state)
    { context = (Context?)__state; return __exception; }

    private static void Pose(ShapeElement __instance, ElementPose? tf)
    {
        if (context is not { } current || tf == null) return;
        if (__instance.Name is not { } bone) return;
        // This method also bakes animation keyframes. Only touch the live
        // animator pose, never the cached frame being generated.
        if (current.Animator is AnimatorBase engine && !ReferenceEquals(engine.GetPosebyName(bone), tf)) return;
        bool leg = bone is "UpperFootL" or "UpperFootR" or "LowerFootL" or "LowerFootR";
        bool torso = bone is "LowerTorso" or "UpperTorso" or "Neck" or "Head";
        bool arm = bone is "UpperArmR" or "UpperArmL" or "LowerArmR" or "LowerArmL";
        if (!leg && (!current.Body || !torso && !(arm && current.EmptyHands))) return;
        var contribution = current.Native.Contribution(current.Animator, bone, current.Walk, current.Air);
        Apply(current.State, current.Source, bone, tf, current.Body, current.EmptyHands, contribution);
    }

    internal static void Apply(MotionState state, StockPoses source, string? bone, ElementPose pose, bool body, bool emptyHands,
        BonePose locomotion = default)
    {
        if (bone is "UpperFootL" or "UpperFootR" or "LowerFootL" or "LowerFootR")
        {
            (BonePose.From(pose) - locomotion + source.Walk(bone, state) * (state.GroundBlend * (1 - state.AirBlend))
                + source.Sample("creativefly", bone, state.FlightCycle) * state.AirBlend).Apply(pose);
        }
        if (!body) return; // Keep immersive camera / first-person weapon anchors untouched.
        if (bone is "LowerTorso" or "UpperTorso" or "Neck" or "Head")
        {
            var ground = source.Walk(bone, state);
            var air = source.Sample("creativefly", bone, state.FlightCycle);
            // Keep the combat torso/head impact; only synchronize the pelvis
            // height. Walking sway cannot continue through an airborne attack.
            if (state.Combat)
            {
                ground = bone == "LowerTorso" ? new(0, 0, 0, 0, ground.TY, 0) : default;
                air = default;
            }
            (BonePose.From(pose) - locomotion + ground * (state.GroundBlend * (1 - state.AirBlend)) + air * state.AirBlend).Apply(pose);
        }
        float turn = (float)(state.Turn * 180 / Math.PI);
        if (bone == "LowerTorso") pose.degY += turn;
        if (bone == "UpperTorso") pose.degY -= turn * (emptyHands ? .55f : 1);
        if (bone == "Head" && emptyHands) pose.degY -= turn * .45f;
        if (emptyHands && bone is ("UpperArmR" or "UpperArmL" or "LowerArmR" or "LowerArmL"))
            (BonePose.From(pose) - locomotion + source.Walk(bone, state) * (state.GroundBlend * (1 - state.AirBlend))
                + source.Sample("creativefly", bone, state.FlightCycle) * (state.AirBlend * .65f)).Apply(pose);
    }

    private void Clear() { states.Clear(); sources.Clear(); native.Clear(); context = null; }
    public override void Dispose()
    {
        if (api != null) api.Event.LeaveWorld -= Clear;
        harmony?.UnpatchAll(harmony.Id); Clear();
        if (active == this) active = null;
    }
}
