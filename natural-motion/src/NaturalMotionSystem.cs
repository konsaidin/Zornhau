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
    private readonly ConditionalWeakTable<IAnimator, Failure> failures = new();
    private readonly List<(WeakReference<EntityPlayer> Player, Action Handler)> footsteps = new();
    private sealed class Failure;
    private static NaturalMotionSystem? active;
    [ThreadStatic] private static Context? context;
    private sealed record Context(MotionState State, StockPoses Source, NativeLocomotion Native, IAnimator Animator,
        bool Body, bool EmptyHands, bool Walk, bool Air, bool Immersive);
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Begin(AnimationManager __instance, out object? __state)
    {
        __state = context; context = null;
        if (active is not { } mod) return;
        try { mod.Prepare(__instance); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            context = null;
            mod.Report(__instance.Animator, "prepare", error);
        }
    }

    private void Prepare(AnimationManager manager)
    {
        var __instance = manager;
        var mod = this; var api = this.api;
        if (api == null || !mod.config.Enabled || api.IsGamePaused || __instance.RunWhilePaused
            || owner(__instance) is not EntityPlayer player || !player.Alive || __instance.Animator == null) return;
        var animator = __instance.Animator;
        if (mod.failures.TryGetValue(animator, out _) || api.World == null || player.Controls == null
            || player.WatchedAttributes == null || player.Pos?.Motion == null) return;
        // Player data and inventories can disappear before the rendered entity
        // is removed. EntityPlayer's slot getters dereference InventoryManager.
        var account = player.Player;
        if (account != null && (account.WorldData == null || account.InventoryManager == null)) return;
        if (account == null && !string.IsNullOrEmpty(player.PlayerUID)) return;
        bool firstPerson = ReferenceEquals(__instance, player.SelfFpAnimManager);
        if (firstPerson && api.World.Player?.ImmersiveFpMode != true) return;
        var animations = animator.Animations;
        if (animations == null || animations.Length == 0) return;
        var library = mod.sources.GetValue(animator, _ => new StockPoses(animations.Where(a => a?.Animation != null).Select(a => a.Animation)));
        if (!library.Available) return;
        bool combat = player.WatchedAttributes.GetInt("warbandPhase") is >= 1 and <= 4
            || __instance.ActiveAnimationsByAnimCode.Keys.Any(code => code.StartsWith("warband-", StringComparison.Ordinal));
        bool excluded = player.MountedOn != null || player.Swimming || player.FeetInLiquid
            || player.Controls.IsClimbing || player.Controls.Gliding || player.Controls.FloorSitting
            || account?.WorldData?.FreeMove == true;
        var pos = player.Pos;
        bool sneak = player.Controls.Sneak || player.ServerControls?.Sneak == true;
        bool sprint = IsSprinting(player);
        var state = mod.states.GetValue(player, mod.CreateState);
        state.Update(new(api.World.ElapsedMilliseconds, pos.X, pos.Y, pos.Z, pos.Dimension,
            pos.Motion.X * 60, pos.Motion.Y * 60, pos.Motion.Z * 60, player.BodyYaw,
            player.OnGround, sneak, sprint, combat, excluded), mod.config);
        bool fadingNative = animations.Any(a => a?.Animation != null && a.Running
            && NativeLocomotion.Owns(a.Animation.Code, mod.config.DirectionalSteps, mod.config.AirbornePose));
        if (!excluded && (state.Active || fadingNative))
            context = new(state, library, mod.native.GetOrCreateValue(animator), animator, true,
                player.RightHandItemSlot?.Empty != false && player.LeftHandItemSlot?.Empty != false && !combat,
                mod.config.DirectionalSteps, mod.config.AirbornePose, firstPerson);
    }

    private static Exception? End(Exception? __exception, object? __state)
    { context = (Context?)__state; return __exception; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Pose(ShapeElement __instance, ElementPose? tf)
    {
        if (context is not { } current || tf == null) return;
        try { PoseCore(current, __instance, tf); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            context = null;
            active?.Report(current.Animator, "pose", error);
        }
    }

    private static void PoseCore(Context current, ShapeElement __instance, ElementPose tf)
    {
        if (__instance.Name is not { } bone) return;
        // This method also bakes animation keyframes. Only touch the live
        // animator pose, never the cached frame being generated.
        if (current.Animator is AnimatorBase engine && !ReferenceEquals(engine.GetPosebyName(bone), tf)) return;
        bool leg = bone is "UpperFootL" or "UpperFootR" or "LowerFootL" or "LowerFootR";
        bool torso = bone is "LowerTorso" or "UpperTorso" or "Neck" or "Head";
        bool arm = bone is "UpperArmR" or "UpperArmL" or "LowerArmR" or "LowerArmL";
        if (arm && current.Immersive) return;
        if (!leg && (!current.Body || !torso && !(arm && current.EmptyHands))) return;
        var contribution = current.Native.Contribution(current.Animator, bone, current.Walk, current.Air);
        Apply(current.State, current.Source, bone, tf, current.Body, current.EmptyHands, contribution, current.Immersive);
    }

    internal static void Apply(MotionState state, StockPoses source, string? bone, ElementPose pose, bool body, bool emptyHands,
        BonePose locomotion = default, bool immersive = false)
    {
        if (bone is "UpperFootL" or "UpperFootR" or "LowerFootL" or "LowerFootR")
        {
            (BonePose.From(pose) - locomotion + source.Walk(bone, state) * (state.GroundBlend * (1 - state.AirBlend))
                + source.Air(bone, state) * state.AirBlend + StockPoses.Landing(bone, state)).Apply(pose);
        }
        if (!body) return;
        if (bone is "LowerTorso" or "UpperTorso" or "Neck" or "Head")
        {
            var ground = source.Walk(bone, state);
            var air = source.Air(bone, state);
            // Keep the combat torso/head impact; only synchronize the pelvis
            // height. Walking sway cannot continue through an airborne attack.
            if (state.Combat)
            {
                ground = bone == "LowerTorso" ? new(0, 0, 0, 0, ground.TY, 0) : default;
                air = default;
            }
            else if (immersive && bone == "UpperTorso")
            {
                // One gait drives the pelvis in immersive view too. Counter
                // its rotation at the chest so head/weapon heading stays put.
                var pelvis = source.Walk("LowerTorso", state);
                var airbornePelvis = source.Air("LowerTorso", state);
                ground = ground with { X = -pelvis.X, Y = -pelvis.Y, Z = -pelvis.Z };
                air = air with { X = -airbornePelvis.X, Y = -airbornePelvis.Y, Z = -airbornePelvis.Z };
            }
            if (immersive && bone is ("Head" or "Neck")) ground = air = default;
            (BonePose.From(pose) - locomotion + ground * (state.GroundBlend * (1 - state.AirBlend))
                + air * state.AirBlend + StockPoses.Landing(bone, state)).Apply(pose);
        }
        float turn = (float)(state.Turn * 180 / Math.PI);
        if (bone == "LowerTorso") pose.degY += turn;
        if (bone == "UpperTorso") pose.degY -= turn * (emptyHands && !immersive ? .55f : 1);
        if (bone == "Head" && emptyHands && !immersive) pose.degY -= turn * .45f;
        if (emptyHands && !state.Combat && !immersive && bone is ("UpperArmR" or "UpperArmL" or "LowerArmR" or "LowerArmL"))
            (BonePose.From(pose) - locomotion + source.Walk(bone, state) * (state.GroundBlend * (1 - state.AirBlend))
                + source.AirArms(bone, state) * state.AirBlend).Apply(pose);
    }

    internal static bool IsSprinting(EntityPlayer player) => !player.Controls.Sneak
        && player.ServerControls?.Sneak != true && (player.Controls.Sprint || player.ServerControls?.Sprint == true);

    private void Report(IAnimator? animator, string stage, Exception error)
    {
        if (animator != null)
        {
            if (failures.TryGetValue(animator, out _)) return;
            failures.Add(animator, new());
        }
        api?.Logger.Warning("[Natural Motion] Correction disabled for animator {0} during {1}; native rendering continues. {2}",
            animator?.GetType().FullName ?? "<missing>", stage, error);
    }

    private MotionState CreateState(EntityPlayer player)
    {
        var state = new MotionState();
        var weakState = new WeakReference<MotionState>(state);
        var weakPlayer = new WeakReference<EntityPlayer>(player);
        Action handler = () =>
        {
            if (active != this || api?.World == null || !config.Enabled || !config.SyncFootsteps
                || !weakState.TryGetTarget(out var motion) || !weakPlayer.TryGetTarget(out var actor)
                || !actor.Alive || !actor.OnGround || actor.Swimming || actor.FeetInLiquid || actor.MountedOn != null
                || actor.Controls == null || actor.Controls.Sneak || actor.Controls.IsClimbing || actor.Controls.Gliding) return;
            motion.ObserveFootstep(api.World.ElapsedMilliseconds, IsSprinting(actor), config);
        };
        player.OnFootStep += handler;
        if (footsteps.Count > 0 && footsteps.Count % 64 == 0) footsteps.RemoveAll(h => !h.Player.TryGetTarget(out _));
        footsteps.Add((weakPlayer, handler));
        return state;
    }

    private void Clear()
    {
        foreach (var hook in footsteps)
            if (hook.Player.TryGetTarget(out var player)) player.OnFootStep -= hook.Handler;
        footsteps.Clear(); states.Clear(); sources.Clear(); native.Clear(); failures.Clear(); context = null;
    }
    public override void Dispose()
    {
        if (api != null) api.Event.LeaveWorld -= Clear;
        harmony?.UnpatchAll(harmony.Id); Clear();
        if (active == this) active = null;
    }
}
