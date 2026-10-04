using System.Reflection;
using System.Runtime.CompilerServices;
using CombatOverhaul.MeleeSystems;
using CombatOverhaul.Colliders;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using System.Collections;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using WarbandCombat;

internal static class HitDiagnosticChecks
{
    internal static void Run()
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        int count = 0;
        void Check(bool value, string text) { if (!value) throw new Exception(text); count++; }
        long now = 1000;
        var replies = new List<HitDiagnosticPacket>();
        var logs = new List<string>();
        var logger = Stub.Make<ILogger>((method, args) => { logs.Add(method.Name); return Stub.Default(method.ReturnType); });
        var world = Stub.Make<IServerWorldAccessor>((method, args) => method.Name == "get_ElapsedMilliseconds" ? now : Stub.Default(method.ReturnType));
        var api = Stub.Make<ICoreServerAPI>((method, args) => method.Name switch
        {
            "get_World" => world, "get_Logger" => logger, _ => Stub.Default(method.ReturnType)
        });
        var network = Stub.Make<IServerNetworkChannel>((method, args) =>
        {
            if (args?[0] is HitDiagnosticPacket packet) replies.Add(packet);
            return Stub.Default(method.ReturnType);
        });
        var owner = new Holder { World = world, Api = api, Alive = true, EntityId = 72 };
        owner.Right.Itemstack = new ItemStack(new Item { Tool = EnumTool.Sword });
        var player = Stub.Player(owner);
        var system = new CombatSystem();
        typeof(CombatSystem).GetField("server", fields)!.SetValue(system, api);
        typeof(CombatSystem).GetField("channel", fields)!.SetValue(system, network);
        var session = typeof(CombatSystem).GetMethod("GetSession", fields)!.Invoke(system, new object[] { player })!;
        var report = typeof(CombatSystem).GetMethod("ReportHitDiagnostic", fields)!;
        void Report(string code = "hit", string detail = "torso") => report.Invoke(system, new object[] { player, 35, 99L, code, detail });
        Report();
        Check(replies.Count == 0 && logs.Count == 0, "Disabled diagnostics produce neither reply nor log");
        session.GetType().GetField("HitDiagnostics")!.SetValue(session, true);
        session.GetType().GetField("AttackId")!.SetValue(session, 35);
        Report("reach", "line\nbreak" + new string('x', 400));
        Check(replies.Count == 1 && replies[0].AttackId == 35 && replies[0].Target == 99, "Replies retain attack and target identity");
        Check(replies[0].Detail.Length == 320 && !replies[0].Detail.Contains('\n'), "Diagnostic text is bounded and cannot inject log lines");
        using (var bytes = new MemoryStream())
        {
            Serializer.Serialize(bytes, replies[0]); bytes.Position = 0;
            var copy = Serializer.Deserialize<HitDiagnosticPacket>(bytes);
            Check(copy.AttackId == 35 && copy.Result == "reach" && copy.Target == 99 && copy.Detail == replies[0].Detail,
                "Diagnostic result roundtrips through the real wire serializer");
        }
        for (int i = 0; i < 30; i++) Report();
        Check(replies.Count == 20, "Server caps diagnostic traffic at 20 replies per player per second");
        now += 1000; Report();
        Check(replies.Count == 21, "Diagnostic rate budget resets independently of combat input");

        // Install the actual production hooks on the pinned library, including
        // the finalizer used when a prefix rejects the attack before CO runs.
        var bridgeType = typeof(CombatSystem).Assembly.GetType("WarbandCombat.OverhaulBridge")!;
        var bridge = RuntimeHelpers.GetUninitializedObject(bridgeType);
        bridgeType.GetField("api", fields)!.SetValue(bridge, api);
        bridgeType.GetField("combat", fields)!.SetValue(bridge, system);
        var staticBridge = bridgeType.GetField("serverBridge", statics)!;
        var traceField = bridgeType.GetField("activeTrace", statics)!;
        var oldBridge = staticBridge.GetValue(null);
        var patch = new Harmony("warband.tests.hitdiagnostics");
        var native = RuntimeHelpers.GetUninitializedObject(typeof(MeleeSystemServer));
        try
        {
            staticBridge.SetValue(null, bridge);
            var method = AccessTools.Method(typeof(MeleeSystemServer), "Attack");
            patch.Patch(method, prefix: new HarmonyMethod(bridgeType.GetMethod("AttackPrefix", statics)!),
                finalizer: new HarmonyMethod(bridgeType.GetMethod("AttackFinished", statics)!));
            patch.Patch(AccessTools.Method(typeof(MeleeSystemServer), "LogRejectedAttackPacket"),
                prefix: new HarmonyMethod(bridgeType.GetMethod("NativeRejected", statics)!));
            var packet = new MeleeDamagePacket { AttackerEntityId = 72, TargetEntityId = 99, MainHand = true };
            method.Invoke(native, new object[] { player, packet });
            Check(replies[^1].Result == "not-physical-weapon", "Actual Harmony prefix returns an exact Warband rejection without running native damage");
            Check(traceField.GetValue(null) == null, "Actual finalizer clears diagnostic context after prefix rejection");
            var traceType = bridgeType.GetNestedType("AttackTrace", BindingFlags.NonPublic)!;
            var trace = Activator.CreateInstance(traceType, fields, null, new object?[] { player, packet, 35, null }, null)!;
            traceField.SetValue(null, trace);
            // This is the same callback CO invokes for failed packet validation.
            bridgeType.GetMethod("NativeRejected", statics)!.Invoke(null, new object[] { player, packet, "terrain-obstructed", "wall" });
            Check(replies[^1].Result == "co-terrain-obstructed", "Native library rejection reasons remain distinguishable from client terrain contact");
            int previousCount = replies.Count;
            bridgeType.GetMethod("AttackFinished", statics)!.Invoke(null, new object?[] { null, trace });
            Check(replies.Count == previousCount && traceField.GetValue(null) == null, "Finalizing a reported result cannot send a second outcome");
        }
        finally { patch.UnpatchAll(patch.Id); staticBridge.SetValue(null, oldBridge); traceField.SetValue(null, null); }
        var lines = new List<float[]>();
        var render = Stub.Make<IRenderAPI>((method, args) =>
        {
            if (method.Name == "RenderLine") lines.Add(args!.Skip(1).Take(6).Cast<float>().ToArray());
            return Stub.Default(method.ReturnType);
        });
        var client = Stub.Make<ICoreClientAPI>((method, args) => method.Name == "get_Render" ? render : Stub.Default(method.ReturnType));
        var drawingBridge = RuntimeHelpers.GetUninitializedObject(bridgeType);
        bridgeType.GetField("api", fields)!.SetValue(drawingBridge, client);
        bridgeType.GetField("debugWeapon", fields)!.SetValue(drawingBridge, owner.Right.Itemstack);
        bridgeType.GetField("debugUntil", fields)!.SetValue(drawingBridge, 1500L);
        var current = new LineSegmentCollider(new Vector3d(2, 3, 4), Vector3d.UnitZ);
        var previous = new LineSegmentCollider(new Vector3d(1, 3, 4), Vector3d.UnitZ);
        var cacheField = bridgeType.GetField("debugColliders", fields)!;
        var cache = (IList)Activator.CreateInstance(cacheField.FieldType)!;
        cache.Add((current, previous, .13f)); cacheField.SetValue(drawingBridge, cache);
        var damage = new MeleeDamageType(new MeleeDamageTypeJson()) { InWorldCollider = current, PreviousInWorldCollider = previous };
        var liveAttack = RuntimeHelpers.GetUninitializedObject(typeof(MeleeAttack));
        typeof(MeleeAttack).GetField("<DamageTypes>k__BackingField", fields)!.SetValue(liveAttack, new[] { damage });
        bridgeType.GetField("collisionAttack", fields)!.SetValue(drawingBridge, liveAttack);
        var draw = bridgeType.GetMethod("RenderDiagnostics", fields)!;
        draw.Invoke(drawingBridge, new object[] { owner, 1000L });
        Check(lines.Count > 20 && lines.All(line => line.All(float.IsFinite)), "Actual overlay draws finite blade, radius, caps and prior sample lines");
        Check(damage.InWorldCollider.Position == current.Position && damage.PreviousInWorldCollider.Position == previous.Position,
            "Rendering never overwrites the live collider or its previous physics sample");
        int lineCount = lines.Count;
        draw.Invoke(drawingBridge, new object[] { owner, 1501L });
        Check(lines.Count == lineCount, "Overlay hides an expired collision sample");
        owner.Right.Itemstack = new ItemStack(new Item { Tool = EnumTool.Sword });
        draw.Invoke(drawingBridge, new object[] { owner, 1100L });
        Check(lines.Count == lineCount, "Changing weapons hides the old weapon collider");
        Console.WriteLine($"PASS: {count} production hit diagnostic checks");
    }
}
