using System.Reflection;
using System.Runtime.CompilerServices;
using CombatOverhaul.Implementations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using WarbandCombat;

internal static class WeaponModeChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var bridge = (OverhaulBridge)RuntimeHelpers.GetUninitializedObject(typeof(OverhaulBridge));
        var modes = (MeleeWeaponClientModesCollection)RuntimeHelpers.GetUninitializedObject(typeof(MeleeWeaponClientModesCollection));
        typeof(MeleeWeaponClientModesCollection).GetField("CurrentModeValue", fields)!.SetValue(modes, "default");
        var profiles = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, WeaponBalanceProfile[]>>>(
            File.ReadAllText("assets/warbandcombat/config/balance-profiles.json"))!;
        foreach (var context in profiles.Values)
        foreach (var (code, definitions) in context.Where(kv => kv.Value.All(p => p.Mode.Length == 0)))
        {
            var item = new MeleeWeapon { Code = new(code), Tool = EnumTool.Sword, Attributes = new JsonObject(new JObject()) };
            typeof(MeleeWeapon).GetField("<ClientModes>k__BackingField", fields)!.SetValue(item, modes);
            CombatBalance.Register(item, definitions);
            var player = new Holder(); player.Right.Itemstack = new ItemStack(item);
            var expected = definitions.FirstOrDefault(p => p.Stance == "two") ?? definitions.First(p => p.Stance == "one");
            Check(bridge.WeaponMode(player) == "", "Native CO default mode must be normalized for " + code);
            foreach (string mode in new[] { "", "default", "previous-weapon-mode" })
            {
                Check(ReferenceEquals(CombatBalance.For(player, mode), expected), "Single-mode balance resolves the actual grip: " + code);
                Check(CombatBalance.CanAttack(player, mode) == expected.CanAttack, "Native default mode cannot disable an audited weapon: " + code);
                Check(CombatSystem.Timing(player, mode).Windup == expected.Windup, "Single-mode timing retains the correct family: " + code);
                player.WatchedAttributes.SetString("warbandWeaponMode", mode);
                Check(ReferenceEquals(CombatBalance.For(player), expected), "Stale server mode cannot disable the next single-mode item: " + code);
            }
        }
        var selectable = new MeleeWeapon { Code = new("test:selectable"), Attributes = new JsonObject(JObject.Parse("""{"Modes":{"cut":{},"thrust":{}}}""")) };
        typeof(MeleeWeapon).GetField("<ClientModes>k__BackingField", fields)!.SetValue(selectable, modes);
        WeaponBalanceProfile Profile(string mode, float power) => new()
        { Mode = mode, Stance = "one", CanAttack = true, Windup = 350, Strike = 260, Recovery = 480, Power = power, SourcePower = [4, 4, 4, 4] };
        var cut = Profile("cut", 4); var thrust = Profile("thrust", 5);
        CombatBalance.Register(selectable, [cut, thrust]);
        var holder = new Holder(); holder.Right.Itemstack = new ItemStack(selectable);
        typeof(MeleeWeaponClientModesCollection).GetField("CurrentModeValue", fields)!.SetValue(modes, "thrust");
        Check(bridge.WeaponMode(holder) == "thrust" && ReferenceEquals(CombatBalance.For(holder, "thrust"), thrust), "Selectable native modes retain their identity and damage profile");
        Check(!CombatBalance.CanAttack(holder, "forged"), "Unknown selectable modes remain forbidden");
        ServerInput(Check);
        Console.WriteLine($"PASS: {checks} real CO default-mode, audited grip and production attack-input checks");
    }

    private static void ServerInput(Action<bool, string> check)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        IServerPlayer? owner = null;
        var world = Stub.Make<IServerWorldAccessor>((m, a) => m.Name switch
        { "get_ElapsedMilliseconds" => 1000L, "get_Side" => EnumAppSide.Server, "PlayerByUid" => owner, _ => Stub.Default(m.ReturnType) });
        var api = Stub.Make<ICoreServerAPI>((m, a) => m.Name switch
        { "get_World" => world, "get_Server" => Stub.Nested(m.ReturnType), _ => Stub.Default(m.ReturnType) });
        var item = new MeleeWeapon { Code = new("test:greatsword"), Tool = EnumTool.Sword };
        var one = new WeaponBalanceProfile { Mode = "", Stance = "one", CanAttack = false,
            Windup = 540, Strike = 380, Recovery = 680, Power = 8, SourcePower = [8,8,8,8] };
        var two = new WeaponBalanceProfile { Mode = "", Stance = "two", CanAttack = true,
            Windup = 540, Strike = 380, Recovery = 680, Power = 10, SourcePower = [8,8,8,8] };
        CombatBalance.Register(item, [one, two]);
        foreach (bool occupied in new[] { false, true })
        {
            var entity = new Holder { Api = api, World = world, Alive = true };
            entity.Right.Itemstack = new ItemStack(item);
            if (occupied) entity.Left.Itemstack = new ItemStack(new Item { Code = new("game:shield") });
            owner = Stub.Player(entity);
            var system = new CombatSystem(); typeof(CombatSystem).GetField("server", fields)!.SetValue(system, api);
            typeof(CombatSystem).GetField("channel", fields)!.SetValue(system,
                Stub.Make<IServerNetworkChannel>((m, a) => Stub.Default(m.ReturnType)));
            typeof(CombatSystem).GetMethod("OnInput", fields)!.Invoke(system,
                new object[] { owner, new InputPacket { Command = (int)Command.Begin, Direction = (int)Direction.Left, Sequence = 1, WeaponMode = "default" } });
            var session = typeof(CombatSystem).GetMethod("GetSession", fields)!.Invoke(system, new object[] { owner })!;
            var fighter = (Fighter)session.GetType().GetField("Fighter")!.GetValue(session)!;
            check(fighter.Phase == (occupied ? Phase.Idle : Phase.Windup), "Real server starts CO default-mode heavy attacks only when both hands are available");
        }
    }
}
