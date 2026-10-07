using System.Runtime.CompilerServices;
using System.Reflection.Emit;
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
    private static Action<ClientAnimator, ElementPose>? weaponFrame;
    [ThreadStatic] private static Context? context;
    private sealed record Context(MotionState State, StockPoses Source, NativeLocomotion Native, IAnimator Animator,
        bool Body, bool EmptyHands, bool Weapon, bool Walk, bool Air, bool Immersive, bool FirstPerson)
    {
        public int CorrectedBones;
    }
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
            prefix: new HarmonyMethod(typeof(NaturalMotionSystem), nameof(PoseArms)) { priority = Priority.First });
        harmony.Patch(AccessTools.Method(typeof(ShapeElement), nameof(ShapeElement.GetLocalTransformMatrix)),
            prefix: new HarmonyMethod(typeof(NaturalMotionSystem), nameof(Pose)) { priority = Priority.Last });
        // CO's matrix transpiler dispatches its weapon frame BEFORE matrix
        // prefixes. Strip/retime native arms at that earlier point, while the
        // pose still contains native locomotion. No assembly dependency on CO.
        var weaponDispatch = AccessTools.TypeByName("CombatOverhaul.Integration.AnimationPatches");
        if (weaponDispatch != null && AccessTools.Method(weaponDispatch, "OnFrameInvoke",
            [typeof(ClientAnimator), typeof(ElementPose)]) is { } beforeWeapon)
        {
            weaponFrame = beforeWeapon.CreateDelegate<Action<ClientAnimator, ElementPose>>();
            // Patch the caller: CO's AggressiveInlining dispatcher may already
            // have been compiled into it before our client system starts.
            harmony.Patch(AccessTools.Method(typeof(ClientAnimator), "calculateMatrices", [typeof(int), typeof(float),
                typeof(List<ElementPose>), typeof(ShapeElementWeights[][]), typeof(float[]),
                typeof(List<ElementPose>[]), typeof(List<ElementPose>[]), typeof(int)]),
                transpiler: new HarmonyMethod(typeof(NaturalMotionSystem), nameof(WeaponFrames)) {
                    priority = Priority.Last, after = ["OverhaulLib:Transpilers"] });
        }
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
        bool immersive = firstPerson && api.World.Player?.ImmersiveFpMode == true;
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
        var right = player.RightHandItemSlot?.Itemstack?.Collectible;
        var left = player.LeftHandItemSlot?.Itemstack?.Collectible;
        bool weapon = HeldWeapons.IsWeapon(right) || HeldWeapons.IsWeapon(left);
        if (!excluded && (state.Active || fadingNative))
            context = new(state, library, mod.native.GetOrCreateValue(animator), animator, !firstPerson || immersive || weapon && !combat,
                right == null && left == null && !combat, weapon,
                mod.config.DirectionalSteps, mod.config.AirbornePose, immersive, firstPerson);
    }

    private static Exception? End(Exception? __exception, object? __state)
    { context = (Context?)__state; return __exception; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Pose(ShapeElement __instance, ElementPose? tf)
        => CorrectPose(__instance, tf, false);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PoseArms(ShapeElement __instance, ElementPose? tf)
        => CorrectPose(__instance, tf, true);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BeforeWeaponPose(IAnimator? __0, ElementPose? __1)
    {
        if (context is { } current && ReferenceEquals(current.Animator, __0) && __1?.ForElement is { } element)
        {
            CorrectPose(element, __1, true);
            // Weapon frames can also own chest/head poses. Correct their native
            // gait before the frame, rather than subtracting from its final pose.
            if (current.Weapon && !current.State.Combat && element.Name is "LowerTorso" or "UpperTorso" or "Neck" or "Head")
                CorrectPose(element, __1, false);
        }
    }

    private static IEnumerable<CodeInstruction> WeaponFrames(IEnumerable<CodeInstruction> instructions)
    {
        var wrapper = AccessTools.Method(typeof(NaturalMotionSystem), nameof(WeaponFrame));
        foreach (var instruction in instructions)
        {
            if (instruction.operand is System.Reflection.MethodInfo method && method.Name == "OnFrameInvoke"
                && method.DeclaringType?.FullName == "CombatOverhaul.Integration.AnimationPatches")
            { instruction.opcode = OpCodes.Call; instruction.operand = wrapper; }
            yield return instruction;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WeaponFrame(ClientAnimator animator, ElementPose pose)
    {
        BeforeWeaponPose(animator, pose);
        weaponFrame?.Invoke(animator, pose);
    }

    private static void CorrectPose(ShapeElement element, ElementPose? tf, bool arms)
    {
        if (context is not { } current || tf == null) return;
        try { PoseCore(current, element, tf, arms); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            context = null;
            active?.Report(current.Animator, "pose", error);
        }
    }

    private static void PoseCore(Context current, ShapeElement __instance, ElementPose tf, bool arms)
    {
        if (__instance.Name is not { } bone) return;
        bool leg = bone is "UpperFootL" or "UpperFootR" or "LowerFootL" or "LowerFootR";
        bool torso = bone is "LowerTorso" or "UpperTorso" or "Neck" or "Head";
        int armBit = bone switch { "UpperArmR" => 1, "UpperArmL" => 2, "LowerArmR" => 4, "LowerArmL" => 8, _ => 0 };
        int bit = armBit != 0 ? armBit : bone switch { "LowerTorso" => 16, "UpperTorso" => 32, "Neck" => 64, "Head" => 128, _ => 0 };
        bool arm = armBit != 0;
        if (arm != arms || bit != 0 && (current.CorrectedBones & bit) != 0) return;
        if (!leg && !torso && !arm || !current.Body && !arm || arm && current.State.Combat) return;
        // This method also bakes animation keyframes. Only touch the live
        // animator pose, never the cached frame being generated.
        if (current.Animator is AnimatorBase engine && !ReferenceEquals(engine.GetPosebyName(bone), tf)) return;
        bool retimeArm = arm && (current.Weapon || !current.EmptyHands || current.FirstPerson);
        var contribution = current.Native.Contribution(current.Animator, bone, current.Walk, current.Air);
        BonePose? armGait = retimeArm
            ? current.Weapon ? default(BonePose)
                : current.Native.Contribution(current.Animator, bone, current.Walk, false, current.State) : null;
        Apply(current.State, current.Source, bone, tf, current.Body, current.EmptyHands, contribution, current.Immersive, armGait, current.Weapon);
        // CO dispatch and the vanilla/Standalone matrix prefix can both run
        // for this bone. Never subtract locomotion again from a weapon frame.
        current.CorrectedBones |= bit;
    }

    internal static void Apply(MotionState state, StockPoses source, string? bone, ElementPose pose, bool body, bool emptyHands,
        BonePose locomotion = default, bool immersive = false, BonePose? armGait = null, bool weapon = false)
    {
        if (bone is "UpperFootL" or "UpperFootR" or "LowerFootL" or "LowerFootR")
        {
            (BonePose.From(pose) - locomotion + source.Walk(bone, state) * (state.GroundBlend * (1 - state.AirBlend))
                + source.Air(bone, state) * state.AirBlend + StockPoses.Landing(bone, state)).Apply(pose);
        }
        if (!state.Combat && bone is ("UpperArmR" or "UpperArmL" or "LowerArmR" or "LowerArmL"))
        {
            if (armGait is { } gait)
                // Items follow the ground clock, then hold their grip in air.
                // Weapons contribute no locomotion on either arm. Item holds
                // and first-person look offsets remain separate contributors.
                (BonePose.From(pose) - locomotion + gait * (state.GroundBlend * (1 - state.AirBlend))).Apply(pose);
            else if (emptyHands && !immersive)
                (BonePose.From(pose) - locomotion + source.Walk(bone, state) * (state.GroundBlend * (1 - state.AirBlend))
                    + source.AirArms(bone, state) * state.AirBlend).Apply(pose);
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
            else if (weapon)
            {
                // A steady local grip still swings if its pelvis/chest parents
                // rotate with the gait. Keep the pelvis step height, but leave
                // the armed upper body upright. Look offsets remain untouched.
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
        if (active == this) { active = null; weaponFrame = null; }
    }
}
