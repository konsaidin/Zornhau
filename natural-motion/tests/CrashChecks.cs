using System.Runtime.CompilerServices;
using System.Reflection;
using NaturalMotion;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

internal static class CrashChecks
{
    internal static int Run(Shape shape)
    {
        int checks = 0, nativeFrames = 0, warnings = 0;
        long time = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
        IPlayer? account = null;
        var world = Proxy.Create<IClientWorldAccessor>((m, a) => m.Name switch {
            "get_ElapsedMilliseconds" => time,
            "PlayerByUid" => account,
            _ => Proxy.Default(m.ReturnType)
        });
        var logger = Proxy.Create<ILogger>((m, a) => { if (m.Name == "Warning") warnings++; return Proxy.Default(m.ReturnType); });
        var api = Proxy.Create<ICoreClientAPI>((m, a) => m.Name switch {
            "get_World" => world, "get_Logger" => logger, "get_Event" => Proxy.Create<IClientEventAPI>(),
            _ => Proxy.Default(m.ReturnType)
        });
        var mod = new NaturalMotionSystem(); mod.StartClientSide(api);
        try
        {
            var entity = new EntityPlayer { Api = api, World = world, OnGround = true, IsRendered = true };
            entity.Pos.Motion.Set(2d / 60, 0, 0);
            var element = new ShapeElement { Name = "UpperFootR", From = new double[3], To = new double[3], RotationOrigin = new double[3] };
            var pose = new ElementPose { ForElement = element }; var matrix = Mat4f.Create();
            var runs = shape.Animations.Select(a => new RunningAnimation { Animation = a }).ToArray();
            var manager = new AnimationManager(); manager.Init(api, entity);
            IAnimator Make(System.Func<RunningAnimation[]?> getRuns) => Proxy.Create<IAnimator>((m, a) => {
                if (m.Name == "get_Animations") return getRuns();
                if (m.Name == "OnFrame") { nativeFrames++; pose.Clear(); pose.degX = 7; element.GetLocalTransformMatrix(1, matrix, pose); }
                return Proxy.Default(m.ReturnType);
            });
            void Draw() { time += 17; manager.OnClientFrame(.017f); }
            manager.Animator = Make(() => runs);
            // Real game Player objects during connection/inventory initialization.
            var clientPlayer = (ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
            account = clientPlayer;
            bool oldWorldDataCrash = false;
            try { _ = entity.Player?.WorldData.FreeMove; } catch (NullReferenceException) { oldWorldDataCrash = true; }
            Check(oldWorldDataCrash, "reproduced the unsafe 0.1.0 WorldData dereference on a real ClientPlayer");
            Draw(); Check(nativeFrames == 1 && pose.degX == 7 && warnings == 0, "loading player keeps native rendering without correction or exception");
            var data = ClientWorldPlayerData.CreateNew(); data.EntityPlayer = entity;
            typeof(ClientPlayer).GetField("worlddata", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(clientPlayer, data);
            bool oldInventoryCrash = false;
            try { _ = entity.RightHandItemSlot; } catch (NullReferenceException) { oldInventoryCrash = true; }
            Check(oldInventoryCrash, "reproduced unsafe game slot getter when InventoryManager is not ready");
            Draw(); Check(nativeFrames == 2 && pose.degX == 7 && warnings == 0, "missing inventory cannot crash correction preparation");
            account = null; entity.WatchedAttributes.SetString("playerUID", "unloading-player");
            Draw(); Check(nativeFrames == 3 && pose.degX == 7, "unloading registered player leaves native rendering intact");
            entity.WatchedAttributes.SetString("playerUID", "");
            manager.Animator = Make(() => null);
            Draw(); Check(nativeFrames == 4 && warnings == 0, "missing animation collection skips correction cleanly");
            manager.Animator = Make(() => [null!, new RunningAnimation { Animation = null! }, .. runs]);
            Draw(); Check(nativeFrames == 5 && warnings == 0, "partially populated animation array cannot crash prefix");
            int reads = 0;
            manager.Animator = Make(() => { reads++; throw new NullReferenceException("animation model rebuilding"); });
            Draw(); Draw();
            Check(nativeFrames == 7 && reads == 1 && warnings == 1, "only faulty correction is disabled, reported once, and native renderer continues");
            manager.Animator = Make(() => runs);
            Draw(); Check(nativeFrames == 8 && pose.degX != 7 && warnings == 1, "replacement animator resumes correction after a model rebuild");
            var brokenClip = new Animation { Code = "walk", QuantityFrames = 25, KeyFrames = [null!, new AnimationKeyFrame { Frame = 0, Elements = null! }] };
            var malformed = new StockPoses([null!, brokenClip]);
            Check(malformed.Sample("walk", "UpperFootR", 0) == default, "incomplete authored frame data returns an empty sample instead of crashing");
        }
        finally { mod.Dispose(); }
        return checks;
    }
}
