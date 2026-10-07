using System.Reflection;
using System.Runtime.CompilerServices;
using CombatOverhaul.Animations;
using CombatOverhaul.Integration.Transpilers;
using HarmonyLib;
using NaturalMotion;
using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;

// Exercise the real CO matrix transpiler and Armory grip frames, rather than
// only vanilla holding clips. Actual FP/TP behaviors apply the frame, including
// their element-name lookup, renderer guards and animator/player dispatch.
internal static class WeaponOverlayChecks
{
    private static ClientAnimator? target;
    private static ClientAnimator? stationary;
    private static PlayerItemFrame grip;
    private static readonly string[] bones = ["UpperArmR", "LowerArmR", "UpperArmL", "LowerArmL", "ItemAnchor", "ItemAnchorL"];

    internal static int Run(Shape shape)
    {
        int checks = 0;
        var coAssembly = typeof(PlayerItemFrame).Assembly;
        var dispatch = AccessTools.Method(coAssembly.GetType("CombatOverhaul.Integration.AnimationPatches")!, "OnFrameInvoke");
        var matrixPatch = coAssembly.GetType("CombatOverhaul.Integration.Transpilers.CalculateMatricesPatches+ClientAnimatorCalculateMatricesPatch")!;
        var posePatch = coAssembly.GetType("CombatOverhaul.Integration.Transpilers.ElementPosePatches+ClientAnimator_LoadPosesAndAttachmentPoints_Patch")!;
        var animationPatches = dispatch.DeclaringType!;
        var map = Activator.CreateInstance(animationPatches.GetNestedType("AnimatorPlayerMap")!)!;
        AccessTools.Property(animationPatches, "Animators").SetValue(null, map);
        var asset = Path.Combine(AppContext.BaseDirectory, "great-sword.json");
        var stances = JObject.Parse(File.ReadAllText(asset));
        stances.Merge(JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "falx.json"))));
        var restSources = stances.Properties().ToDictionary(p => p.Name, p => p.Value.ToObject<AnimationJson>()!);
        foreach (var (code, pose) in WarbandCombat.OverhaulRestPoses.Replacements(restSources)) stances[code] = JToken.FromObject(pose);
        long now = 0;
        var player = (ViewPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ViewPlayer));
        var world = Proxy.Create<IClientWorldAccessor>((m, a) => m.Name switch {
            "get_ElapsedMilliseconds" => now, "get_Player" => player, _ => Proxy.Default(m.ReturnType)
        });
        var api = Proxy.Create<ICoreClientAPI>((m, a) => m.Name switch {
            "get_World" => world, "get_Logger" => Proxy.Create<ILogger>(), "get_Side" => EnumAppSide.Client,
            "get_Event" => Proxy.Create<IClientEventAPI>(), _ => Proxy.Default(m.ReturnType)
        });
        var firstPersonManager = AccessTools.Field(typeof(EntityPlayer), "selfFpAnimManager");
        var overlays = new Harmony("naturalmotion.tests.weaponoverlay");
        var mod = new NaturalMotionSystem();
        try
        {
            foreach (bool co in new[] { true, false })
            {
                if (co)
                {
                    // Identical injection point and dispatcher to the shipped CO fork.
                    overlays.Patch(AccessTools.Method(typeof(ClientAnimator), "LoadPosesAndAttachmentPoints"),
                        transpiler: new HarmonyMethod(posePatch, "Transpiler"));
                    overlays.Patch(AccessTools.Method(typeof(ClientAnimator), "calculateMatrices", [typeof(int), typeof(float),
                        typeof(List<ElementPose>), typeof(ShapeElementWeights[][]), typeof(float[]),
                        typeof(List<ElementPose>[]), typeof(List<ElementPose>[]), typeof(int)]),
                        transpiler: new HarmonyMethod(matrixPatch, "Transpiler"));
                }
                else
                    // StandaloneAim likewise applies its grip as a default-priority
                    // matrix prefix, after native blending but before rendering.
                    overlays.Patch(AccessTools.Method(typeof(ShapeElement), nameof(ShapeElement.GetLocalTransformMatrix)),
                        prefix: new HarmonyMethod(typeof(WeaponOverlayChecks), nameof(StandaloneFrame)));
                bool started = false;
                foreach (int view in new[] { 0, 1, 2 })
                foreach (string stance in new[] { "sword-great-idle-2h", "sword-great-ready-2h", "sword-falx-idle-tp" })
                foreach (string gait in new[] { "walk", "sprint" })
                {
                    grip = stances[stance]!.ToObject<AnimationJson>()!.ToAnimation()
                        .Interpolate(PlayerItemFrame.Zero, TimeSpan.FromSeconds(1));
                    player.ImmersiveFpMode = view == 2;
                    player.CameraMode = view == 0 ? EnumCameraMode.ThirdPerson : EnumCameraMode.FirstPerson;
                    var actor = new ArmedActor { Api = api, World = world, EntityId = ++checks, IsRendered = true, OnGround = true };
                    player.Entity = actor;
                    actor.Right.Itemstack = new ItemStack(new Item { Code = new(stance.Contains("falx")
                        ? "game:blade-falx-silver" : "armory:sword-great-plain-steel") });
                    actor.ServerControls.Sprint = gait == "sprint";
                    target = new ClientAnimator(() => 1, shape.Animations, shape.Elements, shape.JointsById);
                    stationary = new ClientAnimator(() => 1, shape.Animations, shape.Elements, shape.JointsById);
                    if (co)
                    {
                        AccessTools.Method(map.GetType(), "Add").Invoke(map, [target, actor]);
                        AccessTools.Method(map.GetType(), "Add").Invoke(map, [stationary, actor]);
                        var tp = (ThirdPersonAnimationsBehavior)RuntimeHelpers.GetUninitializedObject(typeof(ThirdPersonAnimationsBehavior));
                        AccessTools.Field(typeof(EntityBehavior), "entity").SetValue(tp, actor);
                        AccessTools.Field(tp.GetType(), "_player").SetValue(tp, actor);
                        AccessTools.Field(tp.GetType(), "_ownerEntityId").SetValue(tp, actor.EntityId);
                        AccessTools.Field(tp.GetType(), "_composer").SetValue(tp, new Composer(null, null, actor));
                        tp.FrameOverride = grip;
                        ((Dictionary<long, ThirdPersonAnimationsBehavior>)AccessTools.Property(animationPatches, "AnimationBehaviors").GetValue(null)!)[actor.EntityId] = tp;
                        var fp = (FirstPersonAnimationsBehavior)RuntimeHelpers.GetUninitializedObject(typeof(FirstPersonAnimationsBehavior));
                        AccessTools.Field(typeof(EntityBehavior), "entity").SetValue(fp, actor);
                        AccessTools.Field(fp.GetType(), "_player").SetValue(fp, actor);
                        AccessTools.Field(fp.GetType(), "_ownerEntityId").SetValue(fp, actor.EntityId);
                        AccessTools.Field(fp.GetType(), "_previousHeadBobbingAmplitudeFactor").SetValue(fp, 1f);
                        AccessTools.Field(fp.GetType(), "_composer").SetValue(fp, new Composer(null, null, actor));
                        fp.FrameOverride = grip;
                        AccessTools.Property(animationPatches, "OwnerEntityId").SetValue(null, actor.EntityId);
                        AccessTools.Property(animationPatches, "FirstPersonAnimationBehavior").SetValue(null, fp);
                    }
                    var manager = new PlayerAnimationManager { Animator = target, UseFpAnmations = false }; manager.Init(api, actor);
                    if (view > 0) firstPersonManager.SetValue(actor, manager);
                    string suffix = view > 0 ? "-fp" : "";
                    manager.StartAnimation(Meta(gait + suffix));
                    if (!started)
                    {
                        // A client animator can already be compiled when Natural
                        // Motion starts. CO marks its dispatcher AggressiveInlining.
                        for (int warmup = 0; warmup < 20; warmup++) manager.OnClientFrame(.017f);
                        mod.StartClientSide(api); started = true;
                    }
                    for (int frame = 0; frame < 240; frame++)
                    {
                        now += 17;
                        bool air = frame is >= 90 and < 150;
                        actor.OnGround = !air;
                        actor.Pos.Motion.Set(0, air ? 4d / 60 : 0, frame < 200 ? 2d / 60 : 0);
                        if (frame == 90)
                            foreach (string clip in new[] { "jump", "newjump", "creativefly" }) manager.StartAnimation(Meta(clip));
                        if (frame == 150)
                            foreach (string clip in new[] { "jump", "newjump", "creativefly" }) manager.StopAnimation(clip);
                        if (frame == 200) manager.StopAnimation(gait + suffix);
                        if (frame == 220) actor.WatchedAttributes.SetInt("warbandPhase", 2);
                        manager.OnClientFrame(.017f);
                        stationary.OnFrame(new(), .017f);
                        if (frame < 3) continue; // Animator startup can defer its first matrix calculation.
                        foreach (string bone in bones)
                        {
                            // During combat a one-handed frame does not own the
                            // free arm; its native action pose remains untouched.
                            if (frame >= 220 && grip.Player.LeftHand == null && bone.EndsWith('L')) continue;
                            var expected = new ElementPose(); ApplyGrip(expected, bone);
                            var actual = BonePose.From(target.GetPosebyName(bone));
                            var reference = BonePose.From(expected);
                            if (Math.Abs(actual.X - reference.X) + Math.Abs(actual.Y - reference.Y)
                                + Math.Abs(actual.Z - reference.Z) + Math.Abs(actual.TX - reference.TX)
                                + Math.Abs(actual.TY - reference.TY) + Math.Abs(actual.TZ - reference.TZ) > .002)
                                throw new Exception($"weapon overlay retains authored grip: CO={co} view={view} {stance} {gait} frame={frame} {bone}: {actual} expected {reference}");
                            checks++;
                            // A locally correct grip can still swing through its
                            // animated pelvis/chest parents. Compare final model
                            // directions against the same grip at rest, allowing
                            // normal step height and world movement translations.
                            var matrix = target.GetPosebyName(bone).AnimModelMatrix;
                            var rest = stationary.GetPosebyName(bone).AnimModelMatrix;
                            for (int column = 0; frame < 220 && column < 3; column++)
                                for (int row = 0; row < 3; row++)
                                {
                                    int at = column * 4 + row;
                                    if (Math.Abs(matrix[at] - rest[at]) > .0001)
                                        throw new Exception($"weapon grip model direction follows torso gait: CO={co} view={view} {stance} {gait} frame={frame} {bone} matrix[{at}]={matrix[at]} rest={rest[at]}");
                                    checks++;
                                }
                        }
                    }
                }
                mod.Dispose(); overlays.UnpatchAll(overlays.Id);
            }
        }
        finally
        {
            target = stationary = null; mod.Dispose(); overlays.UnpatchAll(overlays.Id);
            ((Dictionary<long, ThirdPersonAnimationsBehavior>)AccessTools.Property(animationPatches, "AnimationBehaviors").GetValue(null)!).Clear();
            AccessTools.Property(animationPatches, "FirstPersonAnimationBehavior").SetValue(null, null);
            AccessTools.Property(animationPatches, "Animators").SetValue(null, null);
        }
        return checks;
    }

    private static AnimationMetaData Meta(string code) => new AnimationMetaData {
        Code = code, Animation = code, BlendMode = EnumAnimationBlendMode.Average
    }.Init();
    private static void ApplyGrip(ElementPose pose, string? bone)
    {
        if (bone != null && bones.Contains(bone)) grip.Apply(pose, Enum.Parse<EnumAnimatedElement>(bone), Vector3.Zero, 0);
    }
    private static void StandaloneFrame(ShapeElement __instance, ElementPose? tf)
    {
        if (tf != null && (target != null && ReferenceEquals(target.GetPosebyName(__instance.Name), tf)
            || stationary != null && ReferenceEquals(stationary.GetPosebyName(__instance.Name), tf))) ApplyGrip(tf, __instance.Name);
    }
    private sealed class ArmedActor : EntityPlayer
    {
        internal readonly ItemSlot Right = new(null), Left = new(null);
        public override ItemSlot RightHandItemSlot => Right;
        public override ItemSlot LeftHandItemSlot => Left;
    }
    private sealed class ViewPlayer() : ClientPlayer(null!), IClientPlayer
    {
        public new bool ImmersiveFpMode { get; set; }
        public new EnumCameraMode CameraMode { get; set; }
        public new EntityPlayer Entity { get; set; } = null!;
    }
}
