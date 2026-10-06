using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.API.Datastructures;
public class Stub : DispatchProxy
{
    public static EntityPlayer? CurrentPlayer;
    public static bool Privileges;
    public static IPlayerInventoryManager? InventoryManager;
    public static IServerPlayer Player(EntityPlayer entity, string uid = "guard-test")
    {
        CurrentPlayer = entity;
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("GuardPlayerMocks"), AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(IgnoresAccessChecksToAttribute).GetConstructors()[0], new object[] { "VintagestoryAPI" }));
        var type = assembly.DefineDynamicModule("Mocks").DefineType("Player", TypeAttributes.Public);
        var entityField = type.DefineField("EntityValue", typeof(EntityPlayer), FieldAttributes.Public);
        var uidField = type.DefineField("UidValue", typeof(string), FieldAttributes.Public);
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
            if (method.Name == "get_Entity") { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, entityField); }
            else if (method.Name == "HasPrivilege") il.Emit(OpCodes.Ldsfld, typeof(Stub).GetField(nameof(Privileges))!);
            else if (method.Name == "get_PlayerUID") { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, uidField); }
            else if (method.Name == "get_InventoryManager")
            {
                var fallback = il.DefineLabel();
                il.Emit(OpCodes.Ldsfld, typeof(Stub).GetField(nameof(InventoryManager))!);
                il.Emit(OpCodes.Dup); il.Emit(OpCodes.Brfalse_S, fallback); il.Emit(OpCodes.Ret);
                il.MarkLabel(fallback); il.Emit(OpCodes.Pop);
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
        var result = (IServerPlayer)Activator.CreateInstance(type.CreateType()!)!;
        result.GetType().GetField("EntityValue")!.SetValue(result, entity);
        result.GetType().GetField("UidValue")!.SetValue(result, uid);
        entity.WatchedAttributes.SetString("playerUID", uid);
        return result;
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
