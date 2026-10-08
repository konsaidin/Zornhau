using NaturalMotion;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;

internal static class ArmChecks
{
    internal static int Run(Shape shape, string game)
    {
        int checks = 0;
        long now = 0;
        string[] arms = ["UpperArmR", "LowerArmR", "UpperArmL", "LowerArmL"];
        var firstPersonManager = typeof(EntityPlayer).GetField("selfFpAnimManager", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        void Near(BonePose a, BonePose b, string message)
            => Check(Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) + Math.Abs(a.Z - b.Z)
                + Math.Abs(a.TX - b.TX) + Math.Abs(a.TY - b.TY) + Math.Abs(a.TZ - b.TZ) < .002, message);
        var metadata = JObject.Parse(File.ReadAllText(Path.Combine(game, "assets/game/entities/humanoid/player.json")))
            .SelectToken("client.animations")!.ToObject<AnimationMetaData[]>()!
            .GroupBy(a => a.Code).ToDictionary(g => g.Key, g => g.First());
        AnimationMetaData Meta(string code) => metadata.TryGetValue(code, out var value) ? value.Clone().Init()
            : new AnimationMetaData { Code = code, Animation = code, BlendMode = EnumAnimationBlendMode.Average }.Init();
        var localPlayer = (ViewPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ViewPlayer));
        var world = Proxy.Create<IClientWorldAccessor>((m, a) => m.Name switch {
            "get_ElapsedMilliseconds" => now, "get_Player" => localPlayer, _ => Proxy.Default(m.ReturnType)
        });
        var api = Proxy.Create<ICoreClientAPI>((m, a) => m.Name switch {
            "get_World" => world, "get_Logger" => Proxy.Create<ILogger>(),
            "get_Event" => Proxy.Create<IClientEventAPI>(), _ => Proxy.Default(m.ReturnType)
        });
        var mod = new NaturalMotionSystem(); mod.StartClientSide(api);
        try
        {
            foreach (var item in new[] { new Item { Tool = EnumTool.Sword }, new Item { Tool = EnumTool.Spear },
                new Item { Tool = EnumTool.Knife }, new Item { Tool = EnumTool.Axe }, new Item { Tool = EnumTool.Shield },
                new Item { Code = new("armory:mace-plain-iron") }, new Item { Code = new("armory:sword-great-plain-steel") },
                new Item { Code = new("game:bow-long") } })
                Check(HeldWeapons.IsWeapon(item), "weapons and shields suppress arm locomotion");
            foreach (var item in new[] { new Item(), new Item { Code = new("game:torch-up") },
                new Item { Code = new("game:lantern-copper") }, new Item { Code = new("game:stone-granite") },
                new Item { Tool = EnumTool.Pickaxe, Code = new("game:pickaxe-iron") },
                new Item { Code = new("game:swordblade-iron") } })
                Check(!HeldWeapons.IsWeapon(item), "ordinary items and weapon components retain synchronized ground motion");
            foreach (int fps in new[] { 30, 60, 144 })
            foreach (int view in new[] { 0, 1, 2 })
            foreach (int held in new[] { 0, 1, 2, 3 })
            foreach (string clip in new[] { "walk", "sprint", "walk-fp", "sprint-fp", "walk-skirt", "sprint-skirt", "sprint-holdskirt", "walkback", "walkleft", "walkright" })
            {
                localPlayer.ImmersiveFpMode = view == 2;
                bool sprint = clip.StartsWith("sprint");
                var actor = new ArmedActor { Api = api, World = world, OnGround = true, IsRendered = true };
                if ((held & 1) != 0) actor.Right.Itemstack = new ItemStack(new Item());
                if ((held & 2) != 0) actor.Left.Itemstack = new ItemStack(new Item());
                actor.ServerControls.Sprint = sprint;
                actor.Pos.Motion.Set(0, 0, 2d / 60);
                var first = new ClientAnimator(() => 1, shape.Animations, shape.Elements, shape.JointsById);
                var second = new ClientAnimator(() => 1, shape.Animations, shape.Elements, shape.JointsById);
                var reference = new ClientAnimator(() => 1, shape.Animations, shape.Elements, shape.JointsById);
                var manager = new PlayerAnimationManager { Animator = first, UseFpAnmations = false }; manager.Init(api, actor);
                var mirror = new PlayerAnimationManager { Animator = second, UseFpAnmations = false }; mirror.Init(api, actor);
                manager.StartAnimation(Meta(clip)); mirror.StartAnimation(Meta(clip));
                string suffix = view > 0 ? "-fp" : "";
                string? hold = held switch { 1 => "helditemready", 2 => "holdinglanternlefthand", 3 => "holdbothhands", _ => null };
                if (hold != null) { manager.StartAnimation(Meta(hold + suffix)); mirror.StartAnimation(Meta(hold + suffix)); }
                var state = new MotionState(); var config = new MotionConfig();
                long start = now;
                long nextFootstep = start + (sprint ? 300 : 410);
                double variation = 0;
                BonePose previous = default;
                for (int frame = 0; frame <= fps * 2; frame++)
                {
                    now = start + (long)Math.Round(frame * 1000d / fps);
                    state.Update(new(now, 0, 0, 0, 0, 0, 0, 2, 0, true, false, sprint, false, false), config);
                    if (frame > 0)
                    {
                        first.GetAnimationState(clip).CurrentFrame = frame * 3 % first.GetAnimationState(clip).Animation.QuantityFrames;
                        second.GetAnimationState(clip).CurrentFrame = (frame * 7 + 12) % second.GetAnimationState(clip).Animation.QuantityFrames;
                    }
                    if (view > 0) firstPersonManager.SetValue(actor, manager);
                    manager.OnClientFrame(1f / fps);
                    if (view > 0) firstPersonManager.SetValue(actor, mirror);
                    mirror.OnClientFrame(1f / fps);
                    foreach (string bone in arms)
                        Near(BonePose.From(first.GetPosebyName(bone)), BonePose.From(second.GetPosebyName(bone)),
                            $"arms follow the footstep clock despite different native phases: {fps}fps view={view} held={held} {clip} {bone}");
                    var current = BonePose.From(first.GetPosebyName(held == 2 ? "UpperArmR" : "UpperArmL"));
                    if (frame > fps) variation += Math.Abs(current.X - previous.X) + Math.Abs(current.Y - previous.Y)
                        + Math.Abs(current.Z - previous.Z) + Math.Abs(current.TX - previous.TX)
                        + Math.Abs(current.TY - previous.TY) + Math.Abs(current.TZ - previous.TZ);
                    previous = current;
                    // Independent engine renderer at the shared gait frame:
                    // it combines the game's real item-hold metadata and poses.
                    reference.OnFrame(manager.ActiveAnimationsByAnimCode, 1f / fps);
                    var frames = StockPoses.GroundFrames(reference.GetAnimationState(clip).Animation, state);
                    reference.GetAnimationState(clip).CurrentFrame = frames.Walk;
                    reference.OnFrame(manager.ActiveAnimationsByAnimCode, 0);
                    if (frame > fps && (held > 0 || view > 0) && frames.Blend == 0)
                        foreach (string bone in arms)
                            Near(BonePose.From(first.GetPosebyName(bone)), BonePose.From(reference.GetPosebyName(bone)),
                                "retiming preserves native per-arm holding weights and first-person offsets");
                    if (view == 1)
                    {
                        reference.GetAnimationState(clip).CurrentFrame = first.GetAnimationState(clip).CurrentFrame;
                        reference.OnFrame(manager.ActiveAnimationsByAnimCode, 0);
                        foreach (string bone in new[] { "UpperFootR", "LowerTorso", "UpperTorso", "Head" })
                            Near(BonePose.From(first.GetPosebyName(bone)), BonePose.From(reference.GetPosebyName(bone)),
                                "ordinary first-person retiming affects arms without replacing legs or camera anchors");
                    }
                    if (now >= nextFootstep)
                    {
                        state.ObserveFootstep(now, sprint, config);
                        ((Action)typeof(EntityPlayer).GetField("OnFootStep", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(actor)!).Invoke();
                        nextFootstep += sprint ? 300 : 410;
                    }
                }
                if (held != 3 && clip != "sprint-holdskirt") Check(variation > .1,
                    $"synchronized free arm still moves instead of being frozen: {fps}fps view={view} held={held} {clip} variation={variation}");
                Check(manager.ActiveAnimationsByAnimCode.ContainsKey(clip) && (hold == null || manager.ActiveAnimationsByAnimCode.ContainsKey(hold + suffix)),
                    "retiming retains native walking and held-item animation state");
                var anchor = first.GetPosebyName("ItemAnchor");
                if (anchor != null) Near(BonePose.From(anchor), BonePose.From(reference.GetPosebyName("ItemAnchor")),
                    "local held-item attachment pose is not overwritten");

                // Combat and excluded activities must retain the engine's arms.
                foreach (bool combat in new[] { true, false })
                {
                    actor.WatchedAttributes.SetInt("zornhauPhase", combat ? 2 : 0);
                    actor.Swimming = !combat;
                    now += 17;
                    if (view > 0) firstPersonManager.SetValue(actor, manager);
                    manager.OnClientFrame(.017f);
                    reference.OnFrame(manager.ActiveAnimationsByAnimCode, .017f);
                    foreach (var run in first.Animations.Where(r => r.Running))
                        reference.GetAnimationState(run.Animation.Code).CurrentFrame = run.CurrentFrame;
                    reference.OnFrame(manager.ActiveAnimationsByAnimCode, 0);
                    foreach (string bone in arms)
                        Near(BonePose.From(first.GetPosebyName(bone)), BonePose.From(reference.GetPosebyName(bone)),
                            "combat/swimming arm poses retain the original engine clock");
                }
            }

            // A separate stock skeleton has zero arm movement in locomotion
            // clips, but the original holding clips and per-bone weights. This
            // is an independent reference for a steady grip, even when native
            // walking, takeoff and flying animations all run together.
            var steady = JsonConvert.DeserializeObject<Shape>(File.ReadAllText(Path.Combine(game,
                "assets/game/shapes/entity/humanoid/seraph-faceless.json")))!;
            foreach (var animation in steady.Animations.Where(a => NativeLocomotion.Owns(a.Code, true, true)))
                foreach (var key in animation.KeyFrames)
                    foreach (string bone in arms)
                        if (key.Elements.TryGetValue(bone, out var element))
                        {
                            element.RotationX = element.RotationY = element.RotationZ = 0;
                            element.OffsetX = element.OffsetY = element.OffsetZ = 0;
                        }
            steady.InitForAnimations(Proxy.Create<ILogger>(), "steady-grip-reference", "Head");
            foreach (int view in new[] { 0, 1, 2 })
            foreach (int held in new[] { 1, 2, 3, 4, 5, 6 })
            foreach (string gait in new[] { "walk", "sprint" })
            {
                localPlayer.ImmersiveFpMode = view == 2;
                bool weapon = held >= 4;
                var actor = new ArmedActor { Api = api, World = world, IsRendered = true, OnGround = true };
                if (held != 2 && held != 5) actor.Right.Itemstack = new ItemStack(new Item {
                    Code = new(weapon ? "armory:sword-great-plain-steel" : "game:stone-granite") });
                if (held is 2 or 3 or 5 or 6) actor.Left.Itemstack = new ItemStack(new Item {
                    Code = new(held == 5 ? "game:shield-wood" : "game:lantern-copper") });
                var engine = new ClientAnimator(() => 1, shape.Animations, shape.Elements, shape.JointsById);
                var reference = new ClientAnimator(() => 1, steady.Animations, steady.Elements, steady.JointsById);
                var manager = new PlayerAnimationManager { Animator = engine, UseFpAnmations = false }; manager.Init(api, actor);
                if (view > 0) firstPersonManager.SetValue(actor, manager);
                string suffix = view > 0 ? "-fp" : "";
                manager.StartAnimation(Meta(gait + suffix));
                manager.StartAnimation(Meta((held is 2 or 5 ? "holdinglanternlefthand" : held is 3 or 6 ? "holdbothhands" : "helditemready") + suffix));
                actor.ServerControls.Sprint = gait == "sprint";
                for (int frame = 0; frame < 150; frame++)
                {
                    now += 17;
                    bool airborne = frame >= 60;
                    actor.OnGround = !airborne;
                    actor.Pos.Motion.Set(0, airborne ? 4d / 60 : 0, 2d / 60);
                    if (frame == 60)
                        foreach (string clip in new[] { "jump", "newjump", "creativefly" }) manager.StartAnimation(Meta(clip));
                    manager.OnClientFrame(.017f);
                    reference.OnFrame(manager.ActiveAnimationsByAnimCode, .017f);
                    foreach (var run in engine.Animations.Where(r => r.Running))
                        reference.GetAnimationState(run.Animation.Code).CurrentFrame = run.CurrentFrame;
                    reference.OnFrame(manager.ActiveAnimationsByAnimCode, 0);
                    if (weapon || frame >= 125)
                        foreach (string bone in arms)
                            Near(BonePose.From(engine.GetPosebyName(bone)), BonePose.From(reference.GetPosebyName(bone)),
                                $"steady grip without locomotion pumping: weapon={weapon} airborne={airborne} view={view} {gait} {bone}");
                }
            }
        }
        finally { mod.Dispose(); }
        return checks;
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
    }
}
