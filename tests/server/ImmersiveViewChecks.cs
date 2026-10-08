using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;
using Zornhau;

internal static class ImmersiveViewChecks
{
    private static bool SkipLightingAndOpenGl() => false;

    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        var entity = new CameraPlayer { EntityId = 1700, Alive = true };
        var local = (ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
        var data = ClientWorldPlayerData.CreateNew(); data.EntityPlayer = entity;
        AccessTools.Field(typeof(ClientPlayer), "worlddata").SetValue(local, data);
        local.OverrideCameraMode = EnumCameraMode.FirstPerson;
        bool immersive = true, stuck = false;
        int writes = 0;
        var boolSettings = Stub.Make<ISettingsClass<bool>>((m, a) =>
        {
            if (m.Name == "get_Item") return immersive;
            if (m.Name == "set_Item") { writes++; immersive = (bool)a![1]!; }
            return Stub.Default(m.ReturnType);
        });
        var settings = Stub.Make<ISettings>((m, a) => m.Name == "get_Bool" ? boolSettings : Stub.Default(m.ReturnType));
        var world = Stub.Make<IClientWorldAccessor>((m, a) => m.Name == "get_Player" ? local : Stub.Default(m.ReturnType));
        var uploaded = new List<MeshData>();
        var render = Stub.Make<IRenderAPI>((m, a) =>
        {
            if (m.Name == "get_CameraStuck") return stuck;
            if (m.Name == "UploadMultiTextureMesh")
            { uploaded.Add((MeshData)a![0]!); return new MultiTextureMeshRef([], []); }
            return Stub.Default(m.ReturnType);
        });
        var api = Stub.Make<ICoreClientAPI>((m, a) => m.Name switch
        { "get_World" => world, "get_Settings" => settings, "get_Render" => render, _ => Stub.Default(m.ReturnType) });
        EntityPlayerShapeRenderer Renderer(CameraPlayer owner)
        {
            var renderer = (EntityPlayerShapeRenderer)RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayerShapeRenderer));
            AccessTools.Field(typeof(EntityPlayerShapeRenderer), "entityPlayer").SetValue(renderer, owner);
            AccessTools.Field(typeof(EntityShapeRenderer), "entity").SetValue(renderer, owner);
            AccessTools.Field(typeof(EntityShapeRenderer), "capi").SetValue(renderer, api);
            return renderer;
        }
        var renderer = Renderer(entity);
        var meshReady = AccessTools.GetDeclaredMethods(typeof(EntityPlayerShapeRenderer)).Single(m =>
            m.Name.StartsWith("<Tesselate>") && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(MeshData));
        var modeField = AccessTools.Field(typeof(EntityPlayerShapeRenderer), "renderMode");
        var determine = AccessTools.Method(typeof(EntityPlayerShapeRenderer), "determineRenderMode");
        var poseIds = new Dictionary<string, int> { ["UpperArmR"] = 1, ["UpperArmL"] = 2, ["Neck"] = 4 };
        entity.Animations.Animator = Stub.Make<IAnimator>((m, a) => m.Name == "GetPosebyName"
            ? new ElementPose { ForElement = new ShapeElement { JointId = poseIds[(string)a![0]!] }, ChildElementPoses = [] }
            : Stub.Default(m.ReturnType));
        string game = Environment.GetEnvironmentVariable("VSPath") ?? Environment.GetEnvironmentVariable("VS_GAME_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "vintagestory");
        var mesh = ShieldGeometryChecks.Mesh(game + "/assets/survival/shapes/item/tool/shield/blackguard.json");
        mesh.CustomInts = new CustomMeshDataPartInt { InterleaveSizes = [1], InterleaveOffsets = [0], InterleaveStride = 0 };
        // Four actual mesh quad groups stand in for arms/body/head joint IDs.
        // Let the engine's original callback perform its real filtering.
        for (int i = 0; i < mesh.VerticesCount; i++) mesh.CustomInts.Add(i / 4 % 4 + 1);
        bool HasBody(MeshData result) => result.CustomInts.Values.Take(result.VerticesCount).Contains(3);
        bool HasHead(MeshData result) => result.CustomInts.Values.Take(result.VerticesCount).Contains(4);
        void Build() { uploaded.Clear(); meshReady.Invoke(renderer, [mesh]); }
        var patch = new Harmony("tests.immersive-startup");
        // Keep native camera selection, mesh filtering and animation cleanup;
        // skip only the base frame's world lighting/window dependencies.
        patch.Patch(AccessTools.Method(typeof(EntityShapeRenderer), nameof(EntityShapeRenderer.BeforeRender)),
            prefix: new HarmonyMethod(typeof(ImmersiveViewChecks), nameof(SkipLightingAndOpenGl)));
        try
        {
            // Reproduce the original bug before enabling the production fix.
            Build();
            Check(!HasBody(uploaded.Last()), "Unpatched initial callback wrongly filters out the body with immersive enabled");
            ImmersiveView.Patch(patch);
            Build();
            Check((RenderMode)modeField.GetValue(renderer)! == RenderMode.ImmersiveFirstPerson,
                "Initial mesh selection resolves the local player before the first render frame");
            Check(HasBody(uploaded.Last()) && !HasHead(uploaded.Last()),
                "Real initial engine mesh keeps torso/legs and excludes the head without toggling the saved option");
            local.OverrideCameraMode = EnumCameraMode.ThirdPerson; Build();
            Check(!HasBody(uploaded.Last()), "Third-person mesh build reproduces a cached ordinary FP mesh");
            entity.Animations.ActiveAnimationsByAnimCode["idle-fp"] = new AnimationMetaData { Code = "idle-fp", Animation = "idle-fp" };
            local.OverrideCameraMode = EnumCameraMode.FirstPerson;
            int previous = entity.ShapeChanges;
            renderer.BeforeRender(.016f);
            Check(entity.ShapeChanges == previous + 1, "First ThirdPerson-to-immersive entry invalidates the stale mesh once");
            Check(!entity.Animations.ActiveAnimationsByAnimCode.ContainsKey("idle-fp"), "Entry also clears stale ordinary FP animation variants");
            Build();
            Check(HasBody(uploaded.Last()) && !HasHead(uploaded.Last()), "Rebuilt mesh after first entry includes the immersive body");
            previous = entity.ShapeChanges;
            for (int i = 0; i < 120; i++) renderer.BeforeRender(.016f);
            Check(entity.ShapeChanges == previous, "Stable immersive frames do not repeatedly rebuild the mesh");
            foreach (bool choice in new[] { false, true, false, true })
            {
                immersive = choice; renderer.BeforeRender(.016f); Build();
                Check(HasBody(uploaded.Last()) == choice && !HasHead(uploaded.Last()), "Manual option changes still produce the selected native mesh");
                Check(!entity.Animations.ActiveAnimationsByAnimCode.Keys.Any(k => k.EndsWith(choice ? "-fp" : "-ifp")),
                    "Camera transition does not retain the previous animation variants");
                entity.Animations.ActiveAnimationsByAnimCode[choice ? "idle-ifp" : "idle-fp"] = new AnimationMetaData();
            }
            stuck = true; renderer.BeforeRender(.016f);
            Check((RenderMode)modeField.GetValue(renderer)! == RenderMode.FirstPerson, "Camera-stuck native fallback is respected");
            stuck = false; renderer.BeforeRender(.016f);
            entity.WatchedAttributes.GetOrAddTreeAttribute("tiredness").SetInt("isSleeping", 1);
            renderer.BeforeRender(.016f);
            Check((RenderMode)modeField.GetValue(renderer)! == RenderMode.FirstPerson, "Sleeping native fallback is respected");
            entity.WatchedAttributes.GetTreeAttribute("tiredness").SetInt("isSleeping", 0);
            renderer.BeforeRender(.016f);
            var remote = new CameraPlayer { EntityId = 1701 };
            var remoteRenderer = Renderer(remote);
            determine.Invoke(remoteRenderer, []);
            Check((RenderMode)modeField.GetValue(remoteRenderer)! == RenderMode.ThirdPerson,
                "Remote players retain third-person rendering");
            Check(writes == 0 && immersive, "Startup repair never switches or rewrites the user's setting");
            patch.UnpatchAll(patch.Id);
            var rejoin = Renderer(new CameraPlayer { EntityId = 1700 });
            determine.Invoke(rejoin, []);
            Check((RenderMode)modeField.GetValue(rejoin)! == RenderMode.ThirdPerson, "Disposing the patch restores native startup behavior");
        }
        finally { patch.UnpatchAll(patch.Id); }
        Console.WriteLine($"PASS: {checks} real engine immersive startup, mesh and camera-transition checks");
    }

    private sealed class CameraPlayer : Holder
    {
        internal readonly CameraAnimations Animations = new();
        internal int ShapeChanges;
        public override IAnimationManager AnimManager { get => Animations; set { } }
        public override void MarkShapeModified() => ShapeChanges++;
    }

    private sealed class CameraAnimations : PlayerAnimationManager
    {
        public override void StopAnimation(string code) => ActiveAnimationsByAnimCode.Remove(code);
    }
}
