using System.Reflection;
using System.Runtime.CompilerServices;
using CombatOverhaul.Implementations;
using CombatOverhaul.RangedSystems;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Zornhau;

internal static class HeldUseChecks
{
    internal static void Run()
    {
        int checks = 0;
        if (Lang.CurrentLocale == null)
        {
            string assets = Path.Combine(Environment.GetEnvironmentVariable("VSPath") ?? Environment.GetEnvironmentVariable("VS_GAME_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "vintagestory"), "assets");
            string previous = GamePaths.AssetsPath;
            typeof(GamePaths).GetProperty(nameof(GamePaths.AssetsPath))!.SetValue(null, assets);
            try { Lang.PreLoad(Stub.Make<ILogger>((m, a) => Stub.Default(m.ReturnType)), assets); }
            finally { typeof(GamePaths).GetProperty(nameof(GamePaths.AssetsPath))!.SetValue(null, previous); }
        }
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        long clock = 1000;
        IServerPlayer? currentOwner = null;
        var world = Stub.Make<IServerWorldAccessor>((m, a) => m.Name switch
        { "get_ElapsedMilliseconds" => clock, "get_Side" => EnumAppSide.Server, "PlayerByUid" => currentOwner, _ => Stub.Default(m.ReturnType) });
        var api = Stub.Make<ICoreServerAPI>((m, a) => m.Name switch
        { "get_World" => world, "get_Side" => EnumAppSide.Server, _ => Stub.Default(m.ReturnType) });
        var patch = new Harmony("tests.held-use");
        CombatUseInterrupt.Patch(patch); OverhaulUseInterrupt.Patch(patch);
        var previousHit = CombatUseInterrupt.ServerHit;
        try
        {
            var player = new HealingTarget { World = world, Api = api, Alive = true };
            currentOwner = Stub.Player(player);
            int confirmed = 0;
            CombatUseInterrupt.ServerHit = _ => confirmed++;
            player.OnHurt(new DamageSource { Type = EnumDamageType.PiercingAttack }, 0);
            player.OnHurt(new DamageSource { Type = EnumDamageType.Heal }, 1);
            Check(confirmed == 0, "Blocked/absorbed damage and healing ticks cannot interrupt a use");
            player.OnHurt(new DamageSource { Type = EnumDamageType.PiercingAttack }, 1);
            Check(confirmed == 1, "Actual engine player hurt callback confirms physical/projectile damage for interruption");
            var bandage = new Item { Code = new("game:bandage"), MaxStackSize = 64 };
            var healing = new CollectibleBehaviorHealingItem(bandage) { CancelInAir = false, Sound = null, AppliedSound = null };
            bandage.CollectibleBehaviors = [healing];
            player.Right.Itemstack = new ItemStack(bandage, 2);
            var health = new EntityBehaviorHealth(player);
            player.Properties.Server.Behaviors.Add(health);
            health.ApplyDoTEffect(EnumDamageSource.Internal, EnumDamageType.Heal, 0, 4, TimeSpan.FromSeconds(10), 10, 0);
            var pendingHeal = health.ActiveDoTEffects.Single();
            player.Controls.HandUse = EnumHandInteract.HeldItemInteract; player.Controls.UsingBeginMS = 1000;
            clock = 2900;
            CombatUseInterrupt.Cancel(player);
            Check(player.Controls.HandUse == EnumHandInteract.None, "Hit cancels a real healing behavior through the native cancel callback");
            player.Right.Itemstack.Collectible.OnHeldUseStop(3, player.Right, player, null, null, EnumHandInteract.HeldItemInteract);
            Check(player.Heals == 0 && player.Right.Itemstack.StackSize == 2,
                "Late completion cannot apply a cancelled bandage or consume it");
            Check(health.ActiveDoTEffects.Single() == pendingHeal && pendingHeal.TicksLeft == 10,
                "Already applied healing continues; hit leaves its remaining ticks intact");
            EnumHandHandling handling = EnumHandHandling.NotHandled;
            bandage.OnHeldUseStart(player.Right, player, null, null, EnumHandInteract.HeldItemInteract, true, ref handling);
            Check(handling == EnumHandHandling.PreventDefault, "Immediate restart during hit recovery is rejected");
            clock += CombatRules.HitStun;
            player.Controls.UsingBeginMS = clock; handling = EnumHandHandling.NotHandled;
            bandage.OnHeldUseStart(player.Right, player, null, null, EnumHandInteract.HeldItemInteract, true, ref handling);
            bandage.OnHeldUseStop(3, player.Right, player, null, null, EnumHandInteract.HeldItemInteract);
            Check(player.Heals == 1 && player.Right.Itemstack.StackSize == 1,
                "A fresh application after recovery can heal normally");

            var bow = new ItemBow { Code = new("game:bow-simple") };
            player.Right.Itemstack = new ItemStack(bow);
            player.Controls.HandUse = EnumHandInteract.HeldItemInteract; player.Controls.UsingBeginMS = clock;
            player.Attributes.SetInt("aiming", 1); player.Attributes.SetInt("aimingCancel", 0);
            CombatUseInterrupt.Cancel(player);
            Check(player.Controls.HandUse == EnumHandInteract.None && player.Attributes.GetInt("aimingCancel") == 1,
                "Vanilla bow uses cancellation, not the release callback that fires an arrow");

            var coBow = new BowItem { Code = new("game:bow-long") };
            var logic = (BowServer)RuntimeHelpers.GetUninitializedObject(typeof(BowServer));
            const BindingFlags all = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var arrows = new Dictionary<long, (InventoryBase, int)> { [player.EntityId] = (null!, 0) };
            typeof(BowServer).GetField("ArrowSlots", all)!.SetValue(logic, arrows);
            typeof(BowItem).GetField("<ServerLogic>k__BackingField", all)!.SetValue(coBow, logic);
            player.Right.Itemstack = new ItemStack(coBow);
            OverhaulUseInterrupt.Cancel(player);
            Check(!arrows.ContainsKey(player.EntityId), "CO hit clears the server's loaded-arrow authorization");
            var owner = Stub.Player(player);
            Check(!logic.Shoot(owner, player.Right, new ShotPacket(), player),
                "A shot arriving after interruption cannot spawn or consume an arrow");
            Check(!logic.Reload(owner, player.Right, null, new ReloadPacket()), "CO cannot reload during hit recovery");
            clock += CombatRules.HitStun;
            Check(logic.Reload(owner, player.Right, null, new ReloadPacket()), "CO reload resumes after recovery");
        }
        finally { CombatUseInterrupt.ServerHit = previousHit; patch.UnpatchAll(patch.Id); }
        Console.WriteLine($"PASS: {checks} native bandage, healing ticks and bow interruption checks");
    }

    private sealed class HealingTarget : Holder
    {
        internal int Heals;
        public override bool ReceiveDamage(DamageSource source, float damage)
        { if (source.Type == EnumDamageType.Heal) Heals++; return true; }
    }
}
