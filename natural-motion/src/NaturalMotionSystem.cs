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
    private static NaturalMotionSystem? active;
    [ThreadStatic] private static Context? context;
    private sealed record Context(MotionState State, StockPoses Source, bool Body, bool EmptyHands);
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
        if (!excluded && state.Active)
            context = new(state, library, !firstPerson,
                player.RightHandItemSlot?.Empty != false && player.LeftHandItemSlot?.Empty != false && !combat);
    }

    private static Exception? End(Exception? __exception, object? __state)
    { context = (Context?)__state; return __exception; }

    private static void Pose(ShapeElement __instance, ElementPose? tf)
    {
        if (context is not { } current || tf == null) return;
        Apply(current.State, current.Source, __instance.Name, tf, current.Body, current.EmptyHands);
    }

    internal static void Apply(MotionState state, StockPoses source, string? bone, ElementPose pose, bool body, bool emptyHands)
    {
        if (bone is "UpperFootL" or "UpperFootR" or "LowerFootL" or "LowerFootR")
        {
            var result = BonePose.Blend(BonePose.From(pose), source.Walk(bone, state), state.GroundBlend);
            BonePose.Blend(result, source.Sample("creativefly", bone, state.FlightCycle), state.AirBlend).Apply(pose);
        }
        if (!body) return; // Keep immersive camera / first-person weapon anchors untouched.
        float turn = (float)(state.Turn * 180 / Math.PI);
        if (bone == "LowerTorso") pose.degY += turn;
        if (bone == "UpperTorso") pose.degY -= turn * (emptyHands ? .55f : 1);
        if (bone == "Head" && emptyHands) pose.degY -= turn * .45f;
        if (emptyHands && bone is ("UpperArmR" or "UpperArmL" or "LowerArmR" or "LowerArmL"))
            BonePose.Blend(BonePose.From(pose), source.Sample("creativefly", bone, state.FlightCycle), state.AirBlend * .65f).Apply(pose);
    }

    private void Clear() { states.Clear(); sources.Clear(); context = null; }
    public override void Dispose()
    {
        if (api != null) api.Event.LeaveWorld -= Clear;
        harmony?.UnpatchAll(harmony.Id); Clear();
        if (active == this) active = null;
    }
}
