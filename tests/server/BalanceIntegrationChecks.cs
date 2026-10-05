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
using WarbandCombat;

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
        var balance = assembly.GetType("WarbandCombat.CombatBalance")!;
        var register = balance.GetMethod("Register", statics)!;
        var bridgeType = assembly.GetType("WarbandCombat.OverhaulBridge")!;
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
        Console.WriteLine($"PASS: {checks} production CO damage validation and balance integration checks");
    }

    private sealed class TestTarget : EntityAgent
    {
        public TestTarget() { Properties = new EntityProperties(); }
    }
}
