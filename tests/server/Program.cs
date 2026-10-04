using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using WarbandCombat;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
long now = 1200;
IServerPlayer? owner = null;
var world = Stub.Make<IServerWorldAccessor>((method, args) => method.Name switch
{
    "get_ElapsedMilliseconds" => now,
    "PlayerByUid" => owner,
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
    typeof(CombatSystem).GetMethod("Guard", all)!.Invoke(system, parameters);
    bool blocked = raised && front;
    Check((float)parameters[2] == (blocked ? 0 : 4), $"Production guard: armed={armed} direction={direction} front={front} raised={raised}");
    Check(shield.Wear == (blocked ? 4 : 0), "Production guard wears the actual left-hand shield exactly once");
    Check((bool)sourceType.GetProperty("Blocked")!.GetValue(source)! == blocked, "Production guard reports blocked metadata");
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
    private readonly AnimationManager manager = new QuietAnimationManager();
    public override IAnimationManager AnimManager { get => manager; set { } }
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
    public override bool StartAnimation(string code) => true;
    public override void StopAnimation(string code) { }
}
