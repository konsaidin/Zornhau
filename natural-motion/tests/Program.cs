using System.Reflection;
using NaturalMotion;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
void Near(double actual, double expected, double tolerance, string name) => Check(Math.Abs(actual - expected) <= tolerance, $"{name}: {actual} expected {expected}");
var config = new MotionConfig();
MotionInput Input(long at, double vx = 0, double vy = 0, double vz = -2, bool ground = true, float yaw = 0)
    => new(at, 0, 0, 0, 0, vx, vy, vz, yaw, ground, false, false, false, false);
MotionState Settle(MotionInput input, int fps = 60)
{
    var state = new MotionState();
    for (int i = 0; i <= fps * 2; i++) state.Update(input with { At = (long)Math.Round(i * 1000d / fps) }, config);
    return state;
}
foreach (int fps in new[] { 30, 60, 144 })
foreach (float yaw in new[] { 0f, 1.2f, -2.7f, 6.28f })
foreach (double direction in new[] { 0d, Math.PI / 4, Math.PI / 2, -Math.PI / 2, Math.PI })
{
    double forward = Math.Cos(direction) * 2, right = Math.Sin(direction) * 2;
    var s = Settle(Input(0, -Math.Sin(yaw) * forward + Math.Cos(yaw) * right, 0,
        -Math.Cos(yaw) * forward - Math.Sin(yaw) * right, yaw: yaw), fps);
    Near(Math.Cos(s.RelativeDirection), Math.Cos(direction), 1e-6, "yaw-relative gait forward");
    Near(Math.Sin(s.RelativeDirection), Math.Sin(direction), 1e-6, "yaw-relative gait side");
    Check(s.GroundBlend > .999 && s.AirBlend == 0, "ground gait stabilizes");
    Check(Math.Abs(s.Turn) <= config.BodyTurnDegrees * Math.PI / 180 + 1e-6, "body turn bounded");
    if (right > .1) Check(s.Turn < 0, "rightward travel rotates pelvis toward world-right");
    if (right < -.1) Check(s.Turn > 0, "leftward travel rotates pelvis toward world-left");
    var stopped = inputStop(s, fps);
    Check(stopped.Speed < .0001 && stopped.GroundBlend < .0001 && Math.Abs(stopped.Turn) < .0001,
        "blocked movement/stopping removes walking and twist");
}
MotionState inputStop(MotionState state, int fps)
{
    for (int i = 1; i <= fps * 2; i++) state.Update(Input(2000 + (long)Math.Round(i * 1000d / fps), vz: 0), config);
    return state;
}
foreach (int fps in new[] { 30, 60, 144 })
{
    var s = new MotionState();
    for (int i = 0; i <= fps; i++)
    {
        double t = i / (double)fps;
        s.Update(Input((long)Math.Round(t * 1000), vy: 4 - 8 * t, ground: false), config);
        if (t >= .2) Check(s.AirBlend > .9, "ascent/apex/descent keeps flight pose with a softer entry");
    }
    for (int i = 1; i <= fps / 2; i++) s.Update(Input(1000 + (long)Math.Round(i * 1000d / fps)), config);
    Check(s.AirBlend < .003 && s.GroundBlend > .99, "landing returns smoothly to walking");
}
var excluded = Settle(Input(0, vx: 2));
excluded.Update(Input(2017) with { Excluded = true }, config);
Check(!excluded.Active, "swimming/climbing/mount/creative exclusion clears all offsets");
var combat = Settle(Input(0, vx: 2) with { Combat = true });
Near(combat.Turn, 0, 1e-7, "combat does not turn weapon carrier");
var sneaking = Settle(Input(0, vx: 2) with { Sneak = true });
Check(sneaking.GroundBlend == 0 && Math.Abs(sneaking.Turn) < config.BodyTurnDegrees * Math.PI / 360 + 1e-6,
    "sneak preserves native steps with half turn");
var stale = Settle(Input(0, vy: -1, ground: false));
for (int i = 1; i <= 60; i++) stale.Update(Input(2000 + i * 17, vy: 0, ground: false), config);
Check(stale.AirBlend < .001, "stale remote OnGround cannot retain flight indefinitely");
foreach (var invalid in new[] { Input(2017, vx: double.NaN), Input(2017, vy: double.PositiveInfinity),
    Input(2017) with { X = double.NaN }, Input(2017) with { BodyYaw = float.NaN } })
{
    var state = Settle(Input(0)); state.Update(invalid, config); Check(!state.Active, "invalid values clear state");
}
foreach (var discontinuity in new[] { Input(2017) with { X = 3 }, Input(2017) with { Dimension = 1 }, Input(9000) })
{
    var state = Settle(Input(0, vx: 2)); state.Update(discontinuity, config);
    Check(Math.Abs(state.Turn) < .01 && state.Cycle < .01, "teleport/dimension/time gap reset previous gait");
}
var badConfig = new MotionConfig { BodyTurnDegrees = float.NaN, WalkStride = 0, SprintStride = 99, Response = float.PositiveInfinity };
badConfig.Sanitize(); Check(badConfig.BodyTurnDegrees == 28 && badConfig.WalkStride == .6f && badConfig.SprintStride == 4 && badConfig.Response == 12,
    "invalid configuration remains bounded");

string game = Environment.GetEnvironmentVariable("VSPath") ?? "/home/dead/Documents/vintagestory";
string text = File.ReadAllText(Path.Combine(game, "assets/game/shapes/entity/humanoid/seraph-faceless.json"));
var shape = JsonConvert.DeserializeObject<Shape>(text)!;
var library = new StockPoses(shape.Animations);
Check(library.Available, "installed game's actual directional and flight clips exist");
Near(library.Sample("creativefly", "UpperFootR", 0).Z, -67.5, .0001, "native flight knee pose");
Near(library.Sample("creativefly", "UpperFootR", 0).TX, -1.5 / 16, .0001, "offsets convert to metres");
var raw = new StockPoses(new[] { new Animation { Code = "test", QuantityFrames = 20, KeyFrames = new[] {
    new AnimationKeyFrame { Frame = 0, Elements = new() { ["bone"] = new() { RotationY = 170, OffsetX = 16 } } },
    new AnimationKeyFrame { Frame = 5, Elements = new() { ["bone"] = new() { RotationX = 40 } } },
    new AnimationKeyFrame { Frame = 10, Elements = new() { ["bone"] = new() { RotationY = -170, OffsetX = 32 } } }
} } });
Near(raw.Sample("test", "bone", .25).Y, 180, .001, "sparse rotation takes shortest path");
Near(raw.Sample("test", "bone", .25).TX, 1.5, .001, "sparse translation interpolates independent keys");
Near(raw.Sample("test", "bone", .75).TX, 1.5, .001, "loop endpoint interpolates to first key");
Near(raw.Sample("test", "bone", 1.25).TX, 1.5, .001, "phase wraps");
foreach (string bone in new[] { "UpperFootL", "LowerFootL", "UpperFootR", "LowerFootR" })
foreach (string clip in new[] { "walk", "walkleft", "walkright", "walkback", "sprint", "creativefly" })
for (int frame = 0; frame < 200; frame++)
{
    var pose = library.Sample(clip, bone, frame / 200d);
    Check(float.IsFinite(pose.X + pose.Y + pose.Z + pose.TX + pose.TY + pose.TZ), "all actual leg curves remain finite");
}
Check(library.Sample("walkleft", "UpperFootR", 0) != library.Sample("walkright", "UpperFootR", 0), "side steps differ");
// A lateral stride takes 26 frames, not both strides of the 52-frame asset.
Near(library.Sample("walkright", "UpperFootL", .5, true).Z,
    10 * (26 - 15.8) / 13, .001, "side gait closes one 26-frame stride at the common contact phase");
var reversal = Settle(Input(0, vx: 2, vz: 0));
var oldWeight = reversal.RightWeight;
reversal.Update(Input(2017, vx: -2, vz: 0), config);
Check(reversal.RightWeight > oldWeight * .7 && reversal.LeftWeight < .2,
    "direction reversal crossfades instead of instantly replacing the leg pose");
Near(reversal.ForwardWeight + reversal.BackWeight + reversal.LeftWeight + reversal.RightWeight, 1, .0001,
    "direction weights remain normalized during reversal");
var moving = Settle(Input(0, vx: 2));
var backwardSprint = Settle(Input(0, vz: 3) with { Sprint = true });
Check(backwardSprint.BackWeight > .999 && backwardSprint.ForwardWeight < .001,
    "backward sprint retains backward gait rather than forward running");
Check(Math.Abs(library.Walk("UpperFootR", backwardSprint).Z
    - library.Sample("walkback", "UpperFootR", backwardSprint.Cycle, true).Z) > 1,
    "backward running has a distinct stride instead of only accelerated walking");
double backwardPhase = backwardSprint.Cycle;
backwardSprint.Update(Input(2017, vz: 3) with { Sprint = true }, config);
Near((backwardSprint.Cycle - backwardPhase + 1) % 1, .017 / (2 * config.SprintStepMilliseconds / 1000), .00001,
    "backward sprint keeps the running step cadence while retaining the backwards clip");
foreach (string name in new[] { "walk-fp", "walk-ifp", "walk-skirt", "sprint-fp", "sprint-ifp", "sprint-skirt", "sprint-holdskirt", "sprint-skirt-ifp" })
    Check(NativeLocomotion.Owns(name, true, true), "native clothing and first-person gait variants must be masked");
foreach (string name in new[] { "rideelk-walk", "sneakwalk-fp", "warband-swing-right", "walkattack" })
    Check(!NativeLocomotion.Owns(name, true, true), "unrelated movement and combat clips are retained");
var airborneView = Settle(Input(0, vy: 2, ground: false));
var viewPelvis = new ElementPose(); var viewChest = new ElementPose(); var viewHead = new ElementPose { degOffY = 13 };
NaturalMotionSystem.Apply(airborneView, library, "LowerTorso", viewPelvis, true, false, immersive: true);
NaturalMotionSystem.Apply(airborneView, library, "UpperTorso", viewChest, true, false, immersive: true);
NaturalMotionSystem.Apply(airborneView, library, "Head", viewHead, true, false, immersive: true);
Check(viewPelvis.degZ > 12 && viewPelvis.translateY == 0, "immersive jump torso follows flight, not walking");
Near(viewPelvis.degZ + viewChest.degZ, 0, .0001, "immersive airborne chest cancels pelvis pitch for head and weapons");
Check(viewHead.degOffY == 13 && viewHead.degY == 0 && viewHead.degZ == 0, "immersive correction preserves look offsets");
var arm = new ElementPose { degZ = 23, degY = 12 }; var baseline = BonePose.From(arm);
NaturalMotionSystem.Apply(moving, library, "UpperArmR", arm, true, false);
Check(BonePose.From(arm) == baseline, "occupied hand keeps its authored pose");
var torso = new ElementPose { degY = 13, translateY = .4f };
NaturalMotionSystem.Apply(moving, library, "LowerTorso", torso, false, true);
Check(torso.degY == 13 && torso.translateY == .4f, "immersive first-person camera anchors unchanged");
var pelvis = new ElementPose(); var chest = new ElementPose();
NaturalMotionSystem.Apply(moving, library, "LowerTorso", pelvis, true, false);
NaturalMotionSystem.Apply(moving, library, "UpperTorso", chest, true, false);
Near(pelvis.degY + chest.degY, (library.Walk("LowerTorso", moving).Y + library.Walk("UpperTorso", moving).Y) * moving.GroundBlend,
    .0001, "occupied-hand torso adds no extra yaw beyond the shared gait");
Check(File.ReadAllText(Path.Combine(game, "assets/game/shapes/entity/humanoid/seraph-faceless.json")) == text, "game asset not rewritten");

// Exercise the actual Harmony scope and engine matrix method, not only the math helpers.
long now = 0;
var logger = Proxy.Create<ILogger>();
var events = Proxy.Create<IClientEventAPI>();
var world = Proxy.Create<IClientWorldAccessor>((method, args) => method.Name == "get_ElapsedMilliseconds" ? now : Proxy.Default(method.ReturnType));
var api = Proxy.Create<ICoreClientAPI>((method, args) => method.Name switch {
    "get_World" => world, "get_Event" => events, "get_Logger" => logger,
    _ => Proxy.Default(method.ReturnType)
});
var mod = new NaturalMotionSystem(); mod.StartClientSide(api);
var entity = new EntityPlayer { Api = api, World = world, OnGround = true, IsRendered = true };
entity.Pos.Motion.Set(2d / 60, 0, 0); entity.BodyYaw = 0;
var manager = new AnimationManager(); manager.Init(api, entity);
var element = new ShapeElement { Name = "UpperFootR", From = new double[3], To = new double[3], RotationOrigin = new double[3] };
var generated = new ElementPose { ForElement = element };
var matrix = Mat4f.Create(); bool fail = false;
var runs = shape.Animations.Select(a => new RunningAnimation { Animation = a }).ToArray();
manager.Animator = Proxy.Create<IAnimator>((method, args) => {
    if (method.Name == "get_Animations") return runs;
    if (method.Name == "OnFrame") {
        generated.degX = generated.degY = generated.degZ = 0;
        element.GetLocalTransformMatrix(1, matrix, generated);
        if (fail) throw new InvalidOperationException("scope check");
    }
    return Proxy.Default(method.ReturnType);
});
for (int i = 0; i < 120; i++) { now = i * 17; manager.OnClientFrame(.017f); }
Check(Math.Abs(generated.degX) + Math.Abs(generated.degY) + Math.Abs(generated.degZ) > 1,
    "real manager scope applies directional legs to actual engine transform");
Check(!manager.AnimationsDirty && manager.ActiveAnimationsByAnimCode.Count == 0, "visual correction adds no replicated animations");
var outside = new ElementPose { degX = 3 }; element.GetLocalTransformMatrix(1, matrix, outside);
Check(outside.degX == 3 && outside.degY == 0 && outside.degZ == 0, "hook cannot leak outside player animation update");
fail = true; now += 17;
try { manager.OnClientFrame(.017f); Check(false, "test exception must propagate"); } catch (InvalidOperationException) { }
outside = new ElementPose { degX = 3 }; element.GetLocalTransformMatrix(1, matrix, outside);
Check(outside.degX == 3 && outside.degY == 0 && outside.degZ == 0, "finalizer restores scope even after error");
shape.InitForAnimations(logger, "game:entity/humanoid/seraph-faceless", "Head");
var nativeAnimator = new ClientAnimator(() => 1, shape.Animations, shape.Elements, shape.JointsById);
manager.Animator = nativeAnimator;
for (int i = 0; i < 120; i++) { now += 17; manager.OnClientFrame(.017f); }
var nativeLeg = nativeAnimator.GetPosebyName("UpperFootR");
Check(nativeLeg != null && Math.Abs(nativeLeg.degX) + Math.Abs(nativeLeg.degY) + Math.Abs(nativeLeg.degZ) > 1,
    "actual ClientAnimator applies directional gait on full stock skeleton");
Check(nativeLeg!.AnimModelMatrix.All(float.IsFinite), "actual animated leg matrix is finite");
Check(nativeAnimator.GetPosebyName("Head").AnimModelMatrix.All(float.IsFinite), "shared gait keeps native head matrix finite");
entity.WatchedAttributes.SetInt("warbandPhase", 2);
for (int i = 0; i < 90; i++) { now += 17; manager.OnClientFrame(.017f); }
Near(nativeAnimator.GetPosebyName("LowerTorso").degY, 0, .001, "committed combat returns pelvis to authored heading");
entity.OnGround = false; entity.Pos.Motion.Set(0, 4d / 60, 0);
for (int i = 0; i < 20; i++) { now += 17; manager.OnClientFrame(.017f); }
Check(nativeAnimator.GetPosebyName("UpperFootR").degZ is < -28 and > -40,
    "native skeleton uses the lower, relaxed flight legs during ascending jump");
Near(nativeAnimator.GetPosebyName("UpperArmR").degZ, 0, .001, "flight leaves combat arm untouched");

// Reproduce the user's case: actual native walking is running while our
// directional correction renders the player. Different native phases must
// produce the same legs AND torso, during walking and during a jump.
entity.WatchedAttributes.SetInt("warbandPhase", 0); entity.OnGround = true;
entity.Pos.Motion.Set(2d / 60, 0, 0);
var walkClip = shape.Animations.Single(a => a.Code == "walk");
Check(walkClip.PrevNextKeyFrameByFrame == null, "regression exercises lazy frame baking inside the patched player update");
var secondAnimator = new ClientAnimator(() => 1, shape.Animations, shape.Elements, shape.JointsById);
var secondManager = new AnimationManager { Animator = secondAnimator }; secondManager.Init(api, entity);
var walkMeta = new AnimationMetaData { Code = "walk", Animation = "walk", AnimationSpeed = 1.3f }.Init();
manager.StartAnimation(walkMeta); secondManager.StartAnimation(walkMeta.Clone());
string[] ownedBones = ["LowerTorso", "UpperTorso", "Head", "Neck", "UpperFootR", "UpperFootL", "LowerFootR", "LowerFootL", "UpperArmR", "UpperArmL"];
for (int i = 0; i < 180; i++)
{
    now += 17;
    if (i == 100) { entity.OnGround = false; entity.Pos.Motion.Set(2d / 60, 4d / 60, 0); }
    if (i == 140) { entity.OnGround = true; entity.Pos.Motion.Set(2d / 60, 0, 0); }
    if (i > 0)
    {
        nativeAnimator.GetAnimationState("walk").CurrentFrame = (i * 3) % 25;
        secondAnimator.GetAnimationState("walk").CurrentFrame = (i * 7 + 12) % 25;
    }
    manager.OnClientFrame(.017f); secondManager.OnClientFrame(.017f);
    foreach (string name in ownedBones)
    {
        var a = BonePose.From(nativeAnimator.GetPosebyName(name)); var b = BonePose.From(secondAnimator.GetPosebyName(name));
        Near(a.X, b.X, .0001, "native walk phase cannot shake owned bone X");
        Near(a.Y, b.Y, .0001, "native walk phase cannot shake owned bone Y");
        Near(a.Z, b.Z, .0001, "native walk phase cannot shake owned bone Z");
        Near(a.TY, b.TY, .0001, "native walk phase cannot bounce torso/legs");
    }
    if (i is >= 125 and < 140)
    {
        Near(nativeAnimator.GetPosebyName("LowerTorso").translateY, 0, .001, "airborne pelvis loses walking bob");
        Check(nativeAnimator.GetPosebyName("LowerTorso").degZ is >= 12.5f and <= 18.01f,
            "airborne torso stays in the authored flight posture instead of walking sway");
    }
}
// Masking reads the original contributors; it must not mutate network state.
Check(manager.ActiveAnimationsByAnimCode.ContainsKey("walk") && secondManager.ActiveAnimationsByAnimCode.ContainsKey("walk"),
    "native animation replication retained after local masking");
var frameBefore = walkClip.PrevNextKeyFrameByFrame![0][0].RootElementTransforms[0];
var authoredPose = BonePose.From(frameBefore);
manager.OnClientFrame(.017f);
Check(BonePose.From(frameBefore) == authoredPose, "baked frame data remains immutable");
// Native skirt/FP sprint clips must not survive under the backward stride.
foreach (string variant in new[] { "sprint", "sprint-fp", "sprint-skirt", "sprint-holdskirt" })
foreach (double travelZ in new[] { -3d, 3d })
{
    var runner = new EntityPlayer { Api = api, World = world, OnGround = true, IsRendered = true };
    // Other players receive the effective sprint in ServerControls. Their
    // input Controls can stay false while native rendering is already running.
    runner.ServerControls.Sprint = true; runner.BodyYaw = 0; runner.Pos.Motion.Set(0, 0, travelZ / 60);
    var engine = new ClientAnimator(() => 1, shape.Animations, shape.Elements, shape.JointsById);
    var runningManager = new AnimationManager { Animator = engine }; runningManager.Init(api, runner);
    runningManager.StartAnimation(new AnimationMetaData { Code = variant, Animation = variant }.Init());
    var referenceMotion = new MotionState();
    for (int i = 0; i < 120; i++)
    {
        now += 17;
        referenceMotion.Update(Input(now, vz: travelZ) with { Sprint = true }, config);
        runningManager.OnClientFrame(.017f);
        Near(engine.GetPosebyName("UpperFootR").degZ, library.Walk("UpperFootR", referenceMotion).Z * referenceMotion.GroundBlend,
            .001, "effective remote sprint selects the correct running stride and masks native variants");
    }
    Check(runningManager.ActiveAnimationsByAnimCode.ContainsKey(variant), "sprint variant remains in replicated animation state");
}
var referenceShape = JsonConvert.DeserializeObject<Shape>(text)!;
referenceShape.InitForAnimations(logger, "game:entity/humanoid/seraph-faceless", "Head");
var referenceClip = referenceShape.Animations.Single(a => a.Code == "walk");
referenceClip.GenerateAllFrames(referenceShape.Elements, referenceShape.JointsById);
IEnumerable<ElementPose> Flatten(IEnumerable<ElementPose> poses)
{
    foreach (var pose in poses) {
        yield return pose;
        foreach (var child in Flatten(pose.ChildElementPoses)) yield return child;
    }
}
var referenceFrame = Flatten(referenceClip.PrevNextKeyFrameByFrame[0][0].RootElementTransforms)
    .ToDictionary(p => p.ForElement.Name!, p => BonePose.From(p));
foreach (var pose in Flatten(walkClip.PrevNextKeyFrameByFrame[0][0].RootElementTransforms))
    Check(BonePose.From(pose) == referenceFrame[pose.ForElement.Name!], "lazy baking cannot capture procedural movement in native clip data");
var soundEvent = typeof(EntityPlayer).GetField("OnFootStep", BindingFlags.Instance | BindingFlags.NonPublic)!;
Check(soundEvent.GetValue(entity) is Action, "motion state subscribes to the game's actual footstep event");
((Action)soundEvent.GetValue(entity)!).Invoke();
mod.Dispose();
Check(soundEvent.GetValue(entity) == null, "disposing mod removes its native footstep subscription");
checks += CrashChecks.Run(shape);
checks += FootstepChecks.Run(shape);
checks += JumpChecks.Run(shape);
checks += GaitChecks.Run(shape);
Console.WriteLine($"PASS: {checks} motion, native clip, pose compatibility and Harmony integration checks.");

public class Proxy : DispatchProxy
{
    private System.Func<MethodInfo, object?[]?, object?>? handler;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => handler!(method!, args);
    public static T Create<T>(System.Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var result = Create<T, Proxy>(); ((Proxy)(object)result).handler = handler ?? ((m, a) => Default(m.ReturnType)); return result;
    }
    public static object? Default(Type type) => type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
}
