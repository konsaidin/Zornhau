using System.Reflection;
using System.Runtime.CompilerServices;
using CombatOverhaul.Implementations;
using CombatOverhaul.MeleeSystems;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Zornhau;

internal static class BalanceIntegrationChecks
{
    internal static void Run()
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        long now = 1000;
        IServerPlayer? owner = null;
        var world = Stub.Make<IServerWorldAccessor>((m, a) => m.Name switch
        { "get_ElapsedMilliseconds" => now, "PlayerByUid" => owner, _ => Stub.Default(m.ReturnType) });
        var api = Stub.Make<ICoreServerAPI>((m, a) => m.Name switch
        { "get_World" => world, "get_Side" => EnumAppSide.Server, _ => Stub.Default(m.ReturnType) });
        var assembly = typeof(CombatSystem).Assembly;
        var balance = assembly.GetType("Zornhau.CombatBalance")!;
        var register = balance.GetMethod("Register", statics)!;
        var bridgeType = assembly.GetType("Zornhau.OverhaulBridge")!;
        var profileMethod = bridgeType.GetMethod("Profile", instance)!;
        var before = bridgeType.GetMethod("BeforeDamage", instance)!;
        var native = RuntimeHelpers.GetUninitializedObject(typeof(MeleeSystemServer));
        var limitsMethod = typeof(MeleeSystemServer).GetMethod("TryGetAttackLimits", instance)!;
        foreach (float balancedPower in new[] { 1f, 12f })
        foreach (float flatBonus in new[] { 0f, 2f })
        foreach (Direction direction in Enum.GetValues<Direction>())
        {
            // Native ceiling is four, while our new base may be below or above it.
            var item = new MeleeWeapon { Code = new("game:test-sword-iron"), Tool = EnumTool.Sword,
                Attributes = new JsonObject(JObject.Parse("""
                {"OneHandedStance":{"CanAttack":true,"Attack":{"MaxReach":3,"DamageTypes":[
                  {"Damage":{"Damage":4,"DamageType":"SlashingAttack","Tier":1},"Collider":[0,0,0,0,0,1]}]}}}
                """)) };
            var profiles = JsonConvert.DeserializeObject($$"""
                [{"Mode":"","Stance":"one","CanAttack":true,"Windup":350,"Strike":260,"Recovery":480,
                  "Power":{{balancedPower}},"SourcePower":[4,4,4,4]}]
                """, register.GetParameters()[1].ParameterType)!;
            register.Invoke(null, new[] { (object)item, profiles });
            var attacker = new Holder { World = world, Api = api, Alive = true, EntityId = 1 };
            attacker.Right.Itemstack = new ItemStack(item);
            attacker.Right.Itemstack.Attributes.SetFloat("damageBonus", flatBonus);
            attacker.Right.Itemstack.Attributes.SetFloat("damageMultiplier", 1.25f);
            var player = Stub.Player(attacker); owner = player; Stub.Privileges = true;
            var target = new TestTarget { Alive = true, EntityId = 2 };
            var system = new CombatSystem(); typeof(CombatSystem).GetField("server", instance)!.SetValue(system, api);
            var session = typeof(CombatSystem).GetMethod("GetSession", instance)!.Invoke(system, new object[] { player })!;
            session.GetType().GetField("Weapon")!.SetValue(session, attacker.Right.Itemstack);
            var fighter = (Fighter)session.GetType().GetField("Fighter")!.GetValue(session)!;
            var timing = new WeaponTiming(350, 260, 480) { PhysicalContacts = true };
            fighter.Apply(Command.Begin, direction, 1000, timing); fighter.Apply(Command.Release, direction, 1001, timing);
            now = fighter.HitAt;
            var bridge = RuntimeHelpers.GetUninitializedObject(bridgeType);
            bridgeType.GetField("api", instance)!.SetValue(bridge, api);
            bridgeType.GetField("combat", instance)!.SetValue(bridge, system);
            foreach (string name in new[] { "singleStats", "modeStats", "contacts" })
            { var field = bridgeType.GetField(name, instance)!; field.SetValue(bridge, Activator.CreateInstance(field.FieldType)); }
            float Power(bool balanced)
            {
                var p = profileMethod.Invoke(bridge, new object[] { attacker, target, direction, "SlashingAttack", balanced })!;
                return (float)p.GetType().GetProperty("Power")!.GetValue(p)!;
            }
            float nativePower = Power(false), normalized = Power(true);
            Check(Math.Abs(nativePower - (4 + flatBonus) * 1.25f) < .00001, "Authorized packet retains native damage before CO validation");
            object?[] limitArgs = { attacker, target, attacker.Right, null };
            Check((bool)limitsMethod.Invoke(native, limitArgs)!, "Real CO can derive its original weapon limits");
            float ceiling = (float)limitArgs[3]!.GetType().GetField("MaxDamage")!.GetValue(limitArgs[3])!;
            Check(nativePower <= ceiling + .00001, "Server-authorized packet is within the real native CO damage ceiling");
            Check(Math.Abs(normalized - (balancedPower + flatBonus) * 1.25f) < .00001, "Base normalization preserves flat bonuses and multipliers");
            var source = new DamageSource { SourceEntity = attacker, Type = EnumDamageType.SlashingAttack };
            object[] damageArgs = { target, source, attacker.Right, nativePower };
            before.Invoke(bridge, damageArgs);
            float bonus = direction == Direction.Overhead ? 1.12f : 1;
            Check(Math.Abs((float)damageArgs[3] - normalized * bonus) < .00001, "Production post-validation event applies balanced typed damage exactly once");
            Check(fighter.HitDelivered && fighter.Ends == 1000 + 350 + 260 + 50 + 480, "Post-validation contact consumes exactly one fixed-cadence swing");
            damageArgs[3] = nativePower; before.Invoke(bridge, damageArgs);
            Check((float)damageArgs[3] == 0, "Repeated native contact cannot damage a second time");
        }
        Stub.Privileges = false;
        AuditedContacts(Check);
        Console.WriteLine($"PASS: {checks} production CO damage validation and balance integration checks");
    }

    private static void AuditedContacts(Action<bool, string> check)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        long now = 1000;
        IServerPlayer? owner = null;
        var world = Stub.Make<IServerWorldAccessor>((m, a) => m.Name switch
        { "get_ElapsedMilliseconds" => now, "PlayerByUid" => owner, _ => Stub.Default(m.ReturnType) });
        var config = Stub.Make<IServerConfig>((m, a) => m.Name == "get_AllowPvP" ? true : Stub.Default(m.ReturnType));
        var server = Stub.Make<IServerAPI>((m, a) => m.Name == "get_Config" ? config : Stub.Default(m.ReturnType));
        var api = Stub.Make<ICoreServerAPI>((m, a) => m.Name switch
        { "get_World" => world, "get_Server" => server, "get_Side" => EnumAppSide.Server, _ => Stub.Default(m.ReturnType) });
        var runtime = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, WeaponBalanceProfile[]>>>(
            File.ReadAllText("assets/zornhau/config/balance-profiles.json"))!["co"];
        var fixtures = JArray.Parse(File.ReadAllText("balance/integration-weapons.json"));
        var native = RuntimeHelpers.GetUninitializedObject(typeof(MeleeSystemServer));
        var limitsMethod = typeof(MeleeSystemServer).GetMethod("TryGetAttackLimits", all)!;
        foreach (var fixture in fixtures)
        foreach (var definition in runtime[(string)fixture["code"]!].Where(p => p.CanAttack))
        foreach (Direction direction in Enum.GetValues<Direction>())
        foreach (bool pvp in new[] { false, true })
        {
            var item = new MeleeWeapon { Code = new((string)fixture["code"]!),
                Tool = Enum.Parse<EnumTool>((string)fixture["tool"]!), Attributes = new JsonObject(fixture["attributes"]!.DeepClone()) };
            CombatBalance.Register(item, runtime[item.Code.ToString()]);
            var attacker = new Holder { Api = api, World = world, Alive = true, EntityId = 1 };
            attacker.Right.Itemstack = new ItemStack(item);
            attacker.Right.Itemstack.Attributes.SetInt("armorPiercingBonus", 2);
            if (definition.Stance != "two") attacker.Left.Itemstack = new ItemStack(new Item { Code = new(
                definition.Stance.StartsWith("dual:") ? "armory:dagger-long-plain-iron" : "game:torch") });
            attacker.WatchedAttributes.SetString("zornhauWeaponMode", definition.Mode);
            owner = Stub.Player(attacker); Stub.Privileges = true;
            Entity target = pvp ? new Holder { Alive = true, EntityId = 2 } : new TestTarget { Alive = true, EntityId = 2 };
            var bridge = (OverhaulBridge)RuntimeHelpers.GetUninitializedObject(typeof(OverhaulBridge));
            var system = new CombatSystem(); typeof(CombatSystem).GetField("server", all)!.SetValue(system, api);
            typeof(OverhaulBridge).GetField("api", all)!.SetValue(bridge, api);
            typeof(OverhaulBridge).GetField("combat", all)!.SetValue(bridge, system);
            foreach (string name in new[] { "singleStats", "modeStats", "contacts" })
            { var field = typeof(OverhaulBridge).GetField(name, all)!; field.SetValue(bridge, Activator.CreateInstance(field.FieldType)); }
            var profileMethod = typeof(OverhaulBridge).GetMethod("Profile", all)!;
            var original = (AttackProfile?)profileMethod.Invoke(bridge, [attacker, target, direction, null, false]);
            check(original.HasValue, "Actual loaded weapon has a valid contact profile");
            string type = original!.Value.Type.ToString();
            var balanced = (AttackProfile?)profileMethod.Invoke(bridge, [attacker, target, direction, type, true]);
            check(balanced.HasValue, "Effective dagger top/side damage type resolves instead of losing damage");
            if (item.Code.Path.Contains("dagger") && definition.Stance == "one")
                check(original.Value.Type == (direction == Direction.Thrust ? EnumDamageType.PiercingAttack : EnumDamageType.SlashingAttack),
                    "Single Armory dagger keeps three cutting directions and a piercing thrust");
            object?[] limits = [attacker, target, attacker.Right, null];
            check((bool)limitsMethod.Invoke(native, limits)!, "Real CO limits are derived from the exported item's full attack data");
            check(original.Value.ArmorPiercing <= (int)limits[3]!.GetType().GetField("MaxArmorPiercingTier")!.GetValue(limits[3])!,
                "Native AP stays within packet ceilings before normalization");
            var session = typeof(CombatSystem).GetMethod("GetSession", all)!.Invoke(system, [owner])!;
            session.GetType().GetField("Weapon")!.SetValue(session, attacker.Right.Itemstack);
            session.GetType().GetField("Offhand")!.SetValue(session, attacker.Left.Itemstack);
            var fighter = (Fighter)session.GetType().GetField("Fighter")!.GetValue(session)!;
            fighter.Apply(Command.Begin, direction, 1000, definition.Timing); fighter.Apply(Command.Release, direction, 1001, definition.Timing);
            now = fighter.HitAt;
            var source = new CombatOverhaul.DamageSystems.DirectionalTypedDamageSource { SourceEntity = attacker,
                Type = original.Value.Type, DamageTier = original.Value.Tier,
                DamageTypeData = new(original.Value.Type, original.Value.Tier, original.Value.ArmorPiercing) };
            object[] damage = [target, source, attacker.Right, original.Value.Power];
            typeof(OverhaulBridge).GetMethod("BeforeDamage", all)!.Invoke(bridge, damage);
            check((float)damage[3] > 0 && Math.Abs((float)damage[3] - balanced!.Value.Power
                * CombatBalance.DirectionBonus(item.Tool, direction)) < .00002,
                "Actual dagger/spear/2h contacts deal balanced damage in all four directions");
            check(source.ArmorPiercingTier == (pvp ? balanced!.Value.ArmorPiercing : original.Value.ArmorPiercing),
                "Only PvP receives the family AP floor; native quality AP stays additive");
            check(source.DamageTier == original.Value.Tier, "Armor balance does not increase stagger or other attack-tier rules");
        }
        Stub.Privileges = false;
    }

    private sealed class TestTarget : EntityAgent
    {
        public TestTarget() { Properties = new EntityProperties(); }
    }
}
