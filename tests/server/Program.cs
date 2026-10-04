using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using WarbandCombat;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using HarmonyLib;
using CombatOverhaul.MeleeSystems;
using CombatOverhaul.DamageSystems;
using CombatOverhaul.Implementations;
using CombatOverhaul.Colliders;
using OpenTK.Mathematics;
using Vintagestory.API.Datastructures;

int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
long now = 1200;
IServerPlayer? owner = null;
var playedSounds = new List<AssetLocation>();
object? RecordSound(object?[]? args)
{
    if (args?[0] is AssetLocation sound) playedSounds.Add(sound);
    return null;
}
var world = Stub.Make<IServerWorldAccessor>((method, args) => method.Name switch
{
    "get_ElapsedMilliseconds" => now,
    "PlayerByUid" => owner,
    "PlaySoundAt" => RecordSound(args),
    _ => Stub.Default(method.ReturnType)
});
var api = Stub.Make<ICoreServerAPI>((method, args) => method.Name switch
{
    "get_World" => world,
    "get_Server" => Stub.Nested(method.ReturnType),
    _ => Stub.Default(method.ReturnType)
});
foreach (bool armed in new[] { false, true })
foreach (Direction direction in Enum.GetValues<Direction>())
foreach (bool front in new[] { false, true })
foreach (bool raised in new[] { false, true })
{
    var shield = new ShieldItem { Code = new AssetLocation("armory:shield-heavy-plain-iron"), Tool = EnumTool.Shield };
    var defender = new Holder { World = world, Api = api, Alive = true };
    defender.Left.Itemstack = new ItemStack(shield);
    if (armed) defender.Right.Itemstack = new ItemStack(new Item { Tool = EnumTool.Sword });
    owner = Stub.Player(defender);
    var system = new CombatSystem();
    typeof(CombatSystem).GetField("server", all)!.SetValue(system, api);
    var session = typeof(CombatSystem).GetMethod("GetSession", all)!.Invoke(system, new object[] { owner })!;
    var fighter = (Fighter)session.GetType().GetField("Fighter")!.GetValue(session)!;
    session.GetType().GetField("Weapon")!.SetValue(session, defender.Right.Itemstack);
    session.GetType().GetField("Offhand")!.SetValue(session, defender.Left.Itemstack);
    fighter.SetShield(true, 1000);
    fighter.Apply(Command.Block, Direction.Left, 1000, WeaponTiming.Sword);
    now = raised ? 1200 : 1179;
    var attacker = new EntityAgent(); attacker.Pos.Z = front ? 2 : -2;
    var sourceType = typeof(CombatSystem).Assembly.GetType("WarbandCombat.DirectionalDamage")!;
    var source = (DamageSource)Activator.CreateInstance(sourceType, true)!;
    sourceType.GetField("Direction")!.SetValue(source, direction);
    source.SourceEntity = attacker; source.Type = EnumDamageType.SlashingAttack;
    object[] parameters = { defender, source, 4f };
    playedSounds.Clear();
    typeof(CombatSystem).GetMethod("Guard", all)!.Invoke(system, parameters);
    bool blocked = raised && front;
    Check((float)parameters[2] == (blocked ? 0 : 4), $"Production guard: armed={armed} direction={direction} front={front} raised={raised}");
    Check(shield.Wear == (blocked ? 4 : 0), "Production guard wears the actual left-hand shield exactly once");
    Check((bool)sourceType.GetProperty("Blocked")!.GetValue(source)! == blocked, "Production guard reports blocked metadata");
    Check(playedSounds.Count == (blocked ? 1 : 0), "Only a real block produces one contact sound");
    // Exercise the engine entry as well: this counter/animation paints the player red.
    shield.Wear = 0;
    sourceType.GetProperty("Blocked")!.SetValue(source, false);
    var behaviorType = typeof(CombatSystem).Assembly.GetType("WarbandCombat.GuardBehavior")!;
    defender.SidedProperties.Behaviors.Add((EntityBehavior)Activator.CreateInstance(behaviorType,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { defender, system }, null)!);
    playedSounds.Clear();
    bool received = defender.ReceiveDamage(source, 4);
    Check(received == !blocked, "Engine damage entry distinguishes blocked contact from a body hit");
    Check(defender.WatchedAttributes.GetInt("onHurtCounter") == (blocked ? 0 : 1), "Blocked contact never increments the authoritative hurt counter");
    Check(defender.Manager.Hurts == (blocked ? 0 : 1), "Blocked contact never starts the authoritative hurt animation");
    Check(playedSounds.Count == (blocked ? 1 : 0), "Engine entry never duplicates contact sound");
}
Console.WriteLine($"PASS: {checks} production server shield checks");

var movement = typeof(CombatSystem).Assembly.GetType("WarbandCombat.CombatMovement")!;
var runner = new Holder { World = world };
var attack = new Fighter();
attack.Apply(Command.Begin, Direction.Left, now, WeaponTiming.Sword);
movement.GetMethod("Bind", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { runner, attack });
long began = now;
foreach (int offset in new[] { 0, 10, 50 })
{
    now = began + offset;
    runner.Controls.Sprint = runner.ServerControls.Sprint = true;
    runner.Controls.Forward = runner.ServerControls.Forward = true;
    runner.CurrentControls |= EnumEntityActivity.SprintMode;
    movement.GetMethod("BeforeWalkSpeed", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { runner });
    Check(!runner.Controls.Sprint && !runner.ServerControls.Sprint
        && !runner.CurrentControls.HasFlag(EnumEntityActivity.SprintMode), "Physics entry clears incoming sprint on both control sets");
    Check(runner.Controls.Forward && runner.ServerControls.Forward, "Movement restriction preserves walking input");
}
attack.Apply(Command.Release, Direction.Left, now + 1, WeaponTiming.Sword);
attack.TakeHit(attack.HitAt);
now = attack.SprintUntil - 1;
runner.Controls.Sprint = runner.ServerControls.Sprint = true;
movement.GetMethod("BeforeWalkSpeed", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { runner });
Check(!runner.Controls.Sprint && !runner.ServerControls.Sprint, "Physics entry enforces post-contact cooldown");
now++;
attack.Advance(now);
runner.Controls.Sprint = runner.ServerControls.Sprint = true;
movement.GetMethod("BeforeWalkSpeed", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { runner });
Check(runner.Controls.Sprint && runner.ServerControls.Sprint, "Physics entry permits sprint after cooldown expires");
Console.WriteLine("PASS: 8 production movement checks");

int predictionChecks = 0;
void PredictionCheck(bool condition, string message) { Check(condition, message); predictionChecks++; }
var prediction = typeof(CombatSystem).Assembly.GetType("WarbandCombat.CombatPrediction")!;
var predictionPatch = new Harmony("warband.tests.prediction");
prediction.GetMethod("Patch", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { predictionPatch });
var clientApi = Stub.Make<ICoreAPI>((method, args) => method.Name == "get_Side" ? EnumAppSide.Client : Stub.Default(method.ReturnType));
var predictedTarget = new Holder { Api = clientApi, World = world, Alive = true, EntityId = 41 };
var realTarget = new Holder { Api = api, World = world, Alive = true, EntityId = 42 };
var hitSource = new DamageSource { Type = EnumDamageType.SlashingAttack };
IDisposable Scope() => (IDisposable)prediction.GetMethod("Begin", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
try
{
    using (Scope())
    {
        PredictionCheck(!predictedTarget.ReceiveDamage(hitSource, 4), "Owned collision probe suppresses speculative ReceiveDamage");
        PredictionCheck(predictedTarget.WatchedAttributes.GetInt("onHurtCounter") == 0 && predictedTarget.Manager.Hurts == 0,
            "Owned probe produces neither red flash nor hurt animation");
        PredictionCheck(realTarget.ReceiveDamage(hitSource, 4), "Server damage is never suppressed by the prediction scope");
        using (Scope()) PredictionCheck(!predictedTarget.ReceiveDamage(hitSource, 4), "Nested prediction scopes remain suppressed");
        PredictionCheck(!predictedTarget.ReceiveDamage(hitSource, 4), "Inner scope disposal preserves the outer suppression");
        var nativeDamage = new MeleeDamageType(new MeleeDamageTypeJson
            { Damage = new DamageDataJson { Damage = 4, DamageType = "SlashingAttack", Tier = 1 } });
        var attacking = new Holder { Api = clientApi, World = world, Alive = true, EntityId = 40 };
        PredictionCheck(nativeDamage.Attack(attacking, predictedTarget, Vector3d.Zero, "torso", out var packet,
            true, ColliderTypes.Torso, new ItemStackMeleeWeaponStats()), "Native CO still consumes and reports physical contact when prediction returns false");
        PredictionCheck(packet.TargetEntityId == 41 && packet.Damage > 0,
            "Native CO still builds a valid damage packet for server adjudication");
        PredictionCheck(predictedTarget.WatchedAttributes.GetInt("onHurtCounter") == 0,
            "Real native CO attack no longer paints a speculative body hit");
    }
    PredictionCheck(predictedTarget.ReceiveDamage(hitSource, 4), "Ordinary client damage outside owned collision is unchanged");
    PredictionCheck(predictedTarget.WatchedAttributes.GetInt("onHurtCounter") == 1,
        "Real client damage can still produce its hurt counter");
    try { using (Scope()) throw new InvalidOperationException("test scope unwind"); }
    catch (InvalidOperationException) { }
    PredictionCheck(predictedTarget.ReceiveDamage(hitSource, 4), "Exception unwind cannot leave damage prediction globally suppressed");
}
finally { predictionPatch.UnpatchAll(predictionPatch.Id); }
Console.WriteLine($"PASS: {predictionChecks} native prediction and damage presentation checks");

public class Stub : DispatchProxy
{
    public static EntityPlayer? CurrentPlayer;
    public static IServerPlayer Player(EntityPlayer entity)
    {
        CurrentPlayer = entity;
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("GuardPlayerMocks"), AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(IgnoresAccessChecksToAttribute).GetConstructors()[0], new object[] { "VintagestoryAPI" }));
        var type = assembly.DefineDynamicModule("Mocks").DefineType("Player", TypeAttributes.Public);
        type.AddInterfaceImplementation(typeof(IServerPlayer));
        var methods = typeof(IServerPlayer).GetInterfaces().Append(typeof(IServerPlayer))
            .SelectMany(i => i.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)).Where(m => m.IsAbstract).Distinct();
        foreach (var method in methods)
        {
            var definition = type.DefineMethod(method.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final
                | MethodAttributes.NewSlot | MethodAttributes.HideBySig);
            var originalGenerics = method.GetGenericArguments();
            var generics = originalGenerics.Length == 0 ? Array.Empty<GenericTypeParameterBuilder>()
                : definition.DefineGenericParameters(originalGenerics.Select(g => g.Name).ToArray());
            Type Map(Type t) => t.IsGenericParameter ? generics[t.GenericParameterPosition]
                : t.IsByRef ? Map(t.GetElementType()!).MakeByRefType()
                : t.IsArray ? Map(t.GetElementType()!).MakeArrayType()
                : t.IsGenericType ? t.GetGenericTypeDefinition().MakeGenericType(t.GetGenericArguments().Select(Map).ToArray()) : t;
            for (int i = 0; i < generics.Length; i++) generics[i].SetGenericParameterAttributes(originalGenerics[i].GenericParameterAttributes);
            var returnType = Map(method.ReturnType);
            definition.SetReturnType(returnType);
            definition.SetParameters(method.GetParameters().Select(p => Map(p.ParameterType)).ToArray());
            var il = definition.GetILGenerator();
            if (method.Name == "get_Entity") il.Emit(OpCodes.Ldsfld, typeof(Stub).GetField(nameof(CurrentPlayer))!);
            else if (method.Name == "get_PlayerUID") il.Emit(OpCodes.Ldstr, "guard-test");
            else if (method.Name == "get_InventoryManager")
            {
                il.Emit(OpCodes.Ldtoken, method.ReturnType); il.Emit(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
                il.Emit(OpCodes.Call, typeof(Stub).GetMethod(nameof(Nested))!); il.Emit(OpCodes.Castclass, method.ReturnType);
            }
            else if (returnType.IsValueType || returnType.IsGenericParameter)
            {
                var local = il.DeclareLocal(returnType); il.Emit(OpCodes.Ldloca_S, local);
                il.Emit(OpCodes.Initobj, returnType); il.Emit(OpCodes.Ldloc, local);
            }
            else if (method.ReturnType != typeof(void)) il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret); type.DefineMethodOverride(definition, method);
        }
        return (IServerPlayer)Activator.CreateInstance(type.CreateType()!)!;
    }
    public System.Func<MethodInfo, object?[]?, object?>? Handler;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler!(method!, args);
    public static T Make<T>(System.Func<MethodInfo, object?[]?, object?> handler) where T : class
    { var proxy = Create<T, Stub>(); ((Stub)(object)proxy).Handler = handler; return proxy; }
    public static object? Default(Type type) => type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);
    public static object? Nested(Type type)
    {
        if (!type.IsInterface) return Default(type);
        var proxy = (Stub)Create(type, typeof(Stub));
        proxy.Handler = (method, args) => method.Name == "get_Config" ? Nested(method.ReturnType) : Default(method.ReturnType);
        return proxy;
    }
}
public class Holder : EntityPlayer
{
    public readonly QuietAnimationManager Manager = new();
    public Holder() { Properties = new EntityProperties { Server = new EntityServerProperties(Array.Empty<JsonObject>(), new()) }; }
    public override bool ShouldReceiveDamage(DamageSource source, float damage) => true;
    public override IAnimationManager AnimManager { get => Manager; set { } }
    public readonly ItemSlot Left = new ItemSlot(null), Right = new ItemSlot(null);
    public override ItemSlot LeftHandItemSlot => Left;
    public override ItemSlot RightHandItemSlot => Right;
}
public class ShieldItem : Item
{
    public int Wear;
    public override void DamageItem(IWorldAccessor world, Entity byEntity, ItemSlot slot, int amount = 1, bool playSound = true) => Wear += amount;
}

public class QuietAnimationManager : AnimationManager
{
    public int Hurts;
    public override bool StartAnimation(string code) { if (code == "hurt") Hurts++; return true; }
    public override void StopAnimation(string code) { }
}
