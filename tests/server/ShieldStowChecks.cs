using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;
using Zornhau;

internal static class ShieldStowChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); checks++; }
        var profiles = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, WeaponBalanceProfile[]>>>(
            File.ReadAllText("assets/zornhau/config/balance-profiles.json"))!["co"];
        var player = new Holder();
        var shieldItem = new MeshShield { Code = new("game:shield-blackguard"), Tool = EnumTool.Shield };
        var shield = new ItemStack(shieldItem);
        player.Left.Itemstack = shield;
        foreach (var code in new[] { "armory:sword-great-plain-iron", "armory:axe-long-plain-iron", "armory:halberd-plain-iron",
            "armory:pike-plain-iron", "armory:quarterstaff-plain-iron", "armory:sword-long-plain-iron", "game:spear-generic-iron" })
        {
            var item = new Item { Code = new(code), Tool = EnumTool.Sword };
            CombatBalance.Register(item, profiles[code]); player.Right.Itemstack = new ItemStack(item);
            bool two = code is not ("armory:sword-long-plain-iron" or "game:spear-generic-iron");
            Check(CombatShields.Stowed(player) == two && CombatShields.Equipped(player) != two,
                "Only an exclusive two-handed grip stows the shield: " + code);
            Check(CombatBalance.For(player)!.Stance == (two ? "two" : "one"), "Grip and shield defense use the same stow decision");
            Check(ReferenceEquals(player.Left.Itemstack, shield), "Stowage neither copies nor relocates the shield");
        }
        var great = new Item { Code = new("armory:sword-great-plain-iron"), Tool = EnumTool.Sword };
        CombatBalance.Register(great, profiles[great.Code.ToString()]); player.Right.Itemstack = new ItemStack(great);
        var back = new AttachmentPointAndPose { AttachPoint = ShieldGeometryChecks.Back() };
        player.Manager.Animator = Stub.Make<IAnimator>((m, a) => m.Name == "GetAttachmentPointPose" && (string)a![0]! == "Back"
            ? back : Stub.Default(m.ReturnType));
        var original = new ModelTransform().EnsureDefaultValues();
        var info = new ItemRenderInfo { Transform = original,
            ModelRef = (MultiTextureMeshRef)RuntimeHelpers.GetUninitializedObject(typeof(MultiTextureMeshRef)) };
        var renderApi = Stub.Make<IRenderAPI>((m, a) => m.Name == "GetItemStackRenderInfo" ? info : Stub.Default(m.ReturnType));
        int tesselations = 0;
        var tesselator = Stub.Make<ITesselatorAPI>((m, a) =>
        {
            if (m.Name == "TesselateItem")
            {
                tesselations++;
                string game = Environment.GetEnvironmentVariable("VSPath") ?? Environment.GetEnvironmentVariable("VS_GAME_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "vintagestory");
                a![1] = ShieldGeometryChecks.Mesh(game + "/assets/survival/shapes/item/tool/shield/rough.json");
            }
            return Stub.Default(m.ReturnType);
        });
        var api = Stub.Make<ICoreClientAPI>((m, a) => m.Name switch
        { "get_Render" => renderApi, "get_Tesselator" => tesselator, _ => Stub.Default(m.ReturnType) });
        var renderer = (CapturingRenderer)RuntimeHelpers.GetUninitializedObject(typeof(CapturingRenderer));
        AccessTools.Field(typeof(EntityShapeRenderer), "entity").SetValue(renderer, player);
        AccessTools.Field(typeof(EntityShapeRenderer), "capi").SetValue(renderer, api);
        var patch = new Harmony("tests.shield-back"); ShieldBackRenderer.Patch(patch); CombatShieldProtection.Patch(patch);
        try
        {
            var wearable = (ModSystemWearableStats)RuntimeHelpers.GetUninitializedObject(typeof(ModSystemWearableStats));
            var owner = Stub.Player(player);
            float remaining = (float)AccessTools.Method(typeof(ModSystemWearableStats), "applyShieldProtection")
                .Invoke(wearable, [owner, 8f, new DamageSource { Type = EnumDamageType.PiercingAttack }])!;
            Check(remaining == 8, "A shield on the back grants neither active nor random passive native protection");
            var held = AccessTools.Method(typeof(EntityShapeRenderer), "RenderHeldItem");
            foreach (bool shadow in new[] { false, true })
            {
                held.Invoke(renderer, [0f, shadow, false]);
                Check(renderer.Pose == back && renderer.Stack == shield,
                    "Real held/shadow renderer attaches the original shield to Back instead of LeftHand");
                Check(renderer.Transform != null && !ReferenceEquals(renderer.Transform, original),
                    "Back rendering receives its own transform");
                Check(ReferenceEquals(info.Transform, original), "Shared native render info is restored after back rendering");
            }
            Check(shieldItem.MeshCalls == 1, "Per-stack native mesh source is used and fitting is cached between regular/shadow frames");
            info.ModelRef = (MultiTextureMeshRef)RuntimeHelpers.GetUninitializedObject(typeof(MultiTextureMeshRef));
            held.Invoke(renderer, [0f, false, false]);
            Check(shieldItem.MeshCalls == 2, "Changing rendered mesh invalidates the shield fitting");
            var crude = new ItemStack(new ItemShield { Code = new("game:shield-crude"), Tool = EnumTool.Shield });
            player.Left.Itemstack = crude;
            info.ModelRef = (MultiTextureMeshRef)RuntimeHelpers.GetUninitializedObject(typeof(MultiTextureMeshRef));
            held.Invoke(renderer, [0f, false, false]);
            held.Invoke(renderer, [0f, true, false]);
            Check(tesselations == 1 && renderer.Stack == crude && renderer.Pose == back,
                "Fixed vanilla ItemShield uses and caches native TesselateItem without a contained-mesh interface");
            player.Left.Itemstack = shield;
        }
        finally { patch.UnpatchAll(patch.Id); }
        player.Right.Itemstack = null;
        Check(CombatShields.Equipped(player) && !CombatShields.Stowed(player), "Changing away from two-handed weapon returns the shield to hand");
        Console.WriteLine($"PASS: {checks} server grip and native shield-on-back rendering checks");
    }

    private sealed class MeshShield : Item, IContainedMeshSource
    {
        internal int MeshCalls;
        public MeshData GenMesh(ItemSlot slot, ITextureAtlasAPI atlas, Vintagestory.API.MathTools.BlockPos pos)
        {
            MeshCalls++;
            string game = Environment.GetEnvironmentVariable("VSPath") ?? Environment.GetEnvironmentVariable("VS_GAME_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "vintagestory");
            return ShieldGeometryChecks.Mesh(game + "/assets/survival/shapes/item/tool/shield/blackguard.json");
        }
        public string GetMeshCacheKey(ItemSlot slot) => "shield-test";
    }

    private sealed class CapturingRenderer : EntityShapeRenderer
    {
        internal AttachmentPointAndPose? Pose;
        internal ItemStack? Stack;
        internal ModelTransform? Transform;
        public CapturingRenderer() : base(null!, null!) { }
        protected override void RenderItem(float dt, bool isShadowPass, ItemStack stack, AttachmentPointAndPose apap, ItemRenderInfo renderInfo)
        { Pose = apap; Stack = stack; Transform = renderInfo.Transform; }
    }
}
