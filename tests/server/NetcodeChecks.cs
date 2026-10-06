using System.Reflection;
using System.Runtime.CompilerServices;
using CombatOverhaul.MeleeSystems;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using WarbandCombat;

internal static class NetcodeChecks
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    internal static void Run()
    {
        int checks = 0, scenarios = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        var timing = WeaponTiming.Overhaul("sword");
        StatePacket Snapshot(Fighter f, int seq, long revision, int attack, long at, long echo, long received, bool authority = false)
            => new() { Phase = (int)f.Phase, Direction = (int)f.Direction, Sequence = seq, Revision = revision,
                AttackId = attack, Elapsed = (int)(at - f.Started), HitIn = (int)(f.HitAt - at), EndsIn = (int)(f.Ends - at),
                ContactEndsIn = (int)(f.ContactEnds - at), SprintIn = (int)Math.Max(0, f.SprintUntil - at),
                Charge = f.Charge, RecoveryAttackPose = f.RecoveryAttackPose, ContactConsumed = f.HitDelivered,
                EchoSentAt = echo, InputReceivedAt = received, ServerSentAt = at, AuthoritativeClock = authority };

        // Reliable ordered queues with variable transit times and server tick
        // quantization, plus real production reconciliation at render cadence.
        foreach (int rtt in new[] { 0, 30, 80, 150, 300, 600 })
        foreach (int jitter in new[] { 0, 30, 75 })
        foreach (int fps in new[] { 30, 60, 144 })
        foreach (int hold in new[] { 60, 1200 })
        {
            scenarios++;
            var client = new Fighter(); var server = new Fighter(); var sync = new CombatReconciliation();
            int sequence = 0, ack = 0, clientAttack = 0, serverAttack = 0, corrections = 0;
            long revision = 0, echo = 0, received = 0, upstream = 0, downstream = 0;
            var inputs = new Queue<(long Due, int Seq, Command Command, long At)>();
            var states = new Queue<(long Due, StatePacket Packet)>();
            var random = new Random(rtt * 11 + jitter * 19 + fps + hold);
            void Send(Command command, long at)
            {
                sync.Track(++sequence, command, Direction.Left, at, timing, true);
                if (client.Apply(command, Direction.Left, at, timing) && command == Command.Begin) clientAttack = sequence;
                upstream = Math.Max(upstream, at + rtt / 2 + random.Next(jitter + 1));
                inputs.Enqueue((upstream, sequence, command, at));
            }
            void Publish(long at)
            {
                downstream = Math.Max(downstream, at + rtt / 2 + random.Next(jitter + 1));
                states.Enqueue((downstream, Snapshot(server, ack, ++revision, serverAttack, at, echo, received)));
            }
            long nextFrame = 1000;
            for (long at = 1000; at <= 6000; at++)
            {
                if (at == 1000) Send(Command.Begin, at);
                if (at > 1000 && at < 1000 + hold && (at - 1000) % 200 == 0) Send(Command.KeepAlive, at);
                if (at == 1000 + hold) Send(Command.Release, at);
                if (at % 20 == 0)
                {
                    while (inputs.TryPeek(out var input) && input.Due <= at)
                    {
                        inputs.Dequeue(); ack = input.Seq; echo = input.At; received = at;
                        if (server.Apply(input.Command, Direction.Left, at, timing) && input.Command == Command.Begin) serverAttack = ack;
                        Publish(at);
                    }
                    server.Advance(at);
                    if (at % 150 == 0) Publish(at);
                }
                if (at >= nextFrame)
                {
                    while (states.TryPeek(out var state) && state.Due <= at)
                    {
                        states.Dequeue();
                        if (sync.Apply(state.Packet, client, at, ref clientAttack)) corrections++;
                    }
                    client.Advance(at); nextFrame = at + Math.Max(1, 1000 / fps);
                    Check(client.Direction == Direction.Left, "Network replay preserves the pressed direction");
                }
            }
            Check(corrections > 10 && clientAttack == 1 && serverAttack == 1 && sync.PendingCount == 0,
                $"All acknowledgements drain at RTT={rtt} jitter={jitter} fps={fps} hold={hold}");
            Check(client.Phase == Phase.Idle && server.Phase == Phase.Idle,
                "Queued snapshots cannot leave a completed attack stuck in windup or recovery");
        }

        // Release is newer than the received Begin ACK; it must survive replay.
        {
            var local = new Fighter(); var remote = new Fighter(); var sync = new CombatReconciliation(); int id = 1;
            local.Apply(Command.Begin, Direction.Left, 1000, timing); sync.Track(1, Command.Begin, Direction.Left, 1000, timing, true);
            local.Apply(Command.Release, Direction.Right, 1001, timing); sync.Track(2, Command.Release, Direction.Right, 1001, timing, true);
            remote.Apply(Command.Begin, Direction.Left, 1100, timing);
            var ack = Snapshot(remote, 1, 1, 1, 1100, 1000, 1100);
            Check(sync.Apply(ack, local, 1200, ref id) && local.Phase == Phase.Swing && local.HitAt == 1000 + timing.Windup,
                "Older input ACK restores authority and replays an unacknowledged release without an extra windup");
            Check(!sync.Apply(ack, local, 1250, ref id), "Duplicate snapshot has no effect");
            local.TakeHit(1350);
            ack.Revision = 2;
            Check(sync.Apply(ack, local, 1400, ref id) && local.Phase == Phase.Recovery && !local.CanHit(1400),
                "A pre-release snapshot cannot reopen a locally consumed physical contact after input replay");
            ack.Revision = 1; ack.Feedback = "warbandcombat:hit";
            Check(!sync.Apply(ack, local, 1450, ref id), "Older revisions cannot replay phase or HUD feedback");
        }
        // Continuous heartbeats make the server's acknowledged input older than
        // the client sequence. An asynchronous punishment still takes effect.
        foreach (int rtt in new[] { 30, 80, 150, 300, 600 })
        {
            var local = new Fighter(); var authority = new Fighter(); var sync = new CombatReconciliation(); int id = 1;
            local.Apply(Command.Begin, Direction.Left, 1000, timing);
            authority.Apply(Command.Begin, Direction.Left, 1000 + rtt / 2, timing);
            for (int seq = 2; seq <= 7; seq++) sync.Track(seq, Command.KeepAlive, Direction.Right, 1000 + seq * 20, timing, true);
            long serverEvent = 1300; authority.Stun(serverEvent, CombatRules.ParryStun);
            var stun = Snapshot(authority, 1, 1, 1, serverEvent, 1000, 1000 + rtt / 2, true);
            long now = serverEvent + rtt / 2;
            Check(sync.Apply(stun, local, now, ref id) && local.Phase == Phase.Recovery && sync.PendingCount == 6,
                "Unacknowledged held input never suppresses an authoritative stun");
            Check(local.Ends == serverEvent + CombatRules.ParryStun && !local.CanHit(now),
                "Asynchronous penalty loses only downlink travel, rather than the entire RTT");
        }
        {
            var sync = new CombatReconciliation();
            for (int i = 1; i <= 10000; i++) sync.Track(i, Command.KeepAlive, Direction.Left, i, timing, true);
            Check(sync.PendingCount == 128, "A stalled connection cannot grow the input journal without bound");
            sync.Reset(); Check(sync.PendingCount == 0, "World leave clears pending input and snapshot revision");
        }

        // Two independent production server sessions, including actual damage
        // feedback, rather than a second reference to the same mocked player.
        long clock = 1300;
        var players = new Dictionary<string, IServerPlayer>();
        var packets = new List<(StatePacket Packet, IServerPlayer Player)>();
        var world = Stub.Make<IServerWorldAccessor>((m, a) => m.Name switch
        {
            "get_ElapsedMilliseconds" => clock, "get_Side" => EnumAppSide.Server,
            "PlayerByUid" => players.GetValueOrDefault((string)a![0]!),
            "get_AllOnlinePlayers" => players.Values.Cast<IPlayer>().ToArray(),
            _ => Stub.Default(m.ReturnType)
        });
        var api = Stub.Make<ICoreServerAPI>((m, a) => m.Name switch
        { "get_World" => world, "get_Server" => Stub.Nested(m.ReturnType), _ => Stub.Default(m.ReturnType) });
        var channel = Stub.Make<IServerNetworkChannel>((m, a) =>
        {
            if (m.Name == "SendPacket" && a?[0] is StatePacket packet && a[1] is IServerPlayer[] recipients)
                foreach (var player in recipients) packets.Add((packet, player));
            return Stub.Default(m.ReturnType);
        });
        var combat = new CombatSystem(); Set(combat, "server", api); Set(combat, "channel", channel);
        var attacker = new Holder { Api = api, World = world, Alive = true, EntityId = 100 };
        var victim = new Holder { Api = api, World = world, Alive = true, EntityId = 101 };
        foreach (var (uid, entity) in new[] { ("attacker", attacker), ("victim", victim) })
        { entity.Right.Itemstack = new ItemStack(new Item { Tool = EnumTool.Sword }); players[uid] = Stub.Player(entity, uid); }
        object Session(string uid) => typeof(CombatSystem).GetMethod("GetSession", All)!.Invoke(combat, [players[uid]])!;
        var enemySession = Session("victim"); var enemyFighter = (Fighter)Get(enemySession, "Fighter")!;
        var ownSession = Session("attacker");
        foreach (var (session, entity) in new[] { (enemySession, victim), (ownSession, attacker) })
        { Set(session, "Weapon", entity.Right.Itemstack); Set(session, "AttackId", 1); }
        foreach (bool block in new[] { false, true })
        foreach (bool damage in new[] { false, true })
        foreach (bool released in new[] { false, true })
        {
            enemyFighter.Reset(); enemyFighter.Apply(Command.Begin, Direction.Left, 1000, timing);
            if (released) enemyFighter.Apply(Command.Release, Direction.Left, 1001, timing);
            combat.PhysicalFeedback(players["attacker"], victim, block, damage);
            Check(enemyFighter.Phase == (damage && !block ? Phase.Recovery : released ? Phase.Swing : Phase.Windup),
                "Only real non-blocked damage interrupts the victim's windup or released attack");
            if (damage && !block) Check(enemyFighter.HitDelivered && !enemyFighter.CanHit(clock)
                && (bool)Get(enemySession, "AuthoritativeClock")!, "Interrupted contact is spent and gets event-based authority");
        }
        Check(players["attacker"].Entity == attacker && players["victim"].Entity == victim,
            "PvP fixture has two independent engine players");
        Check(packets.Any(p => p.Player == players["victim"] && p.Packet.AuthoritativeClock
            && p.Packet.Phase == (int)Phase.Recovery && p.Packet.ContactConsumed && p.Packet.Revision > 0),
            "Hit interruption immediately sends the victim a versioned authoritative recovery snapshot");

        var ownFighter = (Fighter)Get(ownSession, "Fighter")!;
        Set(ownSession, "AttackId", 42);
        ownFighter.Reset(); ownFighter.Apply(Command.Begin, Direction.Right, 1000, timing);
        ownFighter.Apply(Command.Release, Direction.Right, 1001, timing);
        int delivered = 0; clock = ownFighter.HitAt - 50;
        Check(combat.DeferPhysicalContact(players["attacker"], 42, () => delivered++), "Early contact is queued inside bounded jitter budget");
        Check(!combat.CanAcceptPhysicalContact(players["attacker"]), "Buffer never authorizes damage before full windup");
        Check(combat.DeferPhysicalContact(players["attacker"], 42, () => delivered += 100), "Duplicate early contact cannot replace the first proof");
        clock = ownFighter.HitAt;
        typeof(CombatSystem).GetMethod("Tick", All)!.Invoke(combat, [0.02f]);
        Check(delivered == 1, "Server tick delivers exactly one proof at the strike deadline");
        Check(!combat.DeferPhysicalContact(players["attacker"], 41, () => delivered++), "Old attack ID cannot enter a new attack's queue");
        clock = ownFighter.HitAt - 76;
        Check(!combat.DeferPhysicalContact(players["attacker"], 42, () => delivered++), "Buffer rejects proof beyond 75ms");
        clock = ownFighter.HitAt - 30;
        combat.DeferPhysicalContact(players["attacker"], 42, () => delivered++); ownFighter.Stun(clock, 240);
        clock += 50; typeof(CombatSystem).GetMethod("Tick", All)!.Invoke(combat, [0.02f]);
        Check(delivered == 1, "Parry or interruption discards buffered contact instead of resurrecting damage");

        // Optional adapter: unbound native attacks/pushes are disabled for our
        // weapons, while the standalone packet remains free of CO dependencies.
        var bridge = (OverhaulBridge)RuntimeHelpers.GetUninitializedObject(typeof(OverhaulBridge));
        Set(bridge, "combat", combat); Set(bridge, "api", api);
        Set(bridge, "contactRates", new Dictionary<string, (long Started, int Count)>());
        int wrapped = 0;
        Set(bridge, "receiveContact", (Action<IServerPlayer, MeleeDamagePacket>)((player, packet) =>
        {
            Check(typeof(OverhaulBridge).GetField("authorizedPlayer", All)!.GetValue(null) == player
                && (int)typeof(OverhaulBridge).GetField("authorizedAttack", All)!.GetValue(null)! == 42,
                "The native callback receives a scoped authorization for this player and attack only");
            wrapped++; ownFighter.TakeHit(clock);
        }));
        var oldBridge = typeof(OverhaulBridge).GetField("serverBridge", All)!.GetValue(null);
        typeof(OverhaulBridge).GetField("serverBridge", All)!.SetValue(null, bridge);
        try
        {
            var native = typeof(OverhaulBridge).GetMethod("NativeContactHandler", All)!;
            Check(!(bool)native.Invoke(null, [players["attacker"]])!, "Raw CO batches, pushes and statuses cannot bypass Warband ownership");
            object?[] args = [players["attacker"], new MeleeDamagePacket(), null];
            Check(!(bool)typeof(OverhaulBridge).GetMethod("AttackPrefix", All)!.Invoke(null, args)!,
                "Bare CO damage packet has no authority without the matching wrapped attack ID");
            ownFighter.Reset(); ownFighter.Apply(Command.Begin, Direction.Left, 1000, timing);
            ownFighter.Apply(Command.Release, Direction.Left, 1001, timing); clock = ownFighter.HitAt;
            using (var payload = new MemoryStream())
            {
                Serializer.Serialize(payload, new MeleeDamagePacket { DamageType = "SlashingAttack", Collider = "Head",
                    Position = [0, 1.6, 0], MainHand = true, AttackerEntityId = 100, TargetEntityId = 101 });
                var receive = typeof(OverhaulBridge).GetMethod("ReceiveContact", All)!;
                var envelope = new OverhaulContactPacket { AttackId = 41, Payload = payload.ToArray() };
                receive.Invoke(bridge, [players["attacker"], envelope]);
                Check(wrapped == 0, "Stale wrapped contact cannot consume the current attack");
                envelope.AttackId = 42; receive.Invoke(bridge, [players["attacker"], envelope]);
                receive.Invoke(bridge, [players["attacker"], envelope]);
                Check(wrapped == 1, "Real protobuf envelope reaches native validation once, then duplicate is refused");
                Check(typeof(OverhaulBridge).GetField("authorizedPlayer", All)!.GetValue(null) == null,
                    "The native callback cannot leak its authorization into the next packet");
                envelope.Payload = new byte[4097]; receive.Invoke(bridge, [players["attacker"], envelope]);
                Check(wrapped == 1, "Oversized envelope is refused before parsing native protobuf");
            }
            attacker.Right.Itemstack = null;
            Check((bool)native.Invoke(null, [players["attacker"]])!, "Unowned native mechanics retain their handler");
        }
        finally { typeof(OverhaulBridge).GetField("serverBridge", All)!.SetValue(null, oldBridge); }
        using (var stream = new MemoryStream())
        {
            var original = new OverhaulContactPacket { AttackId = 42, Payload = [1, 2, 3] };
            Serializer.Serialize(stream, original); stream.Position = 0;
            var copy = Serializer.Deserialize<OverhaulContactPacket>(stream);
            Check(copy.AttackId == 42 && copy.Payload.SequenceEqual(original.Payload), "Wire envelope retains attack identity and bounded native payload");
        }
        var sentEnvelopes = new List<OverhaulContactPacket>(); int rawSent = 0;
        var clientChannel = Stub.Make<IClientNetworkChannel>((m, a) =>
        { if (a?[0] is OverhaulContactPacket packet) sentEnvelopes.Add(packet); return Stub.Default(m.ReturnType); });
        var rawChannel = Stub.Make<IClientNetworkChannel>((m, a) =>
        { if (m.Name == "SendPacket") rawSent++; return Stub.Default(m.ReturnType); });
        var network = Stub.Make<IClientNetworkAPI>((m, a) => m.Name == "GetChannel" ? clientChannel : Stub.Default(m.ReturnType));
        var clientApi = Stub.Make<ICoreClientAPI>((m, a) => m.Name == "get_Network" ? network : Stub.Default(m.ReturnType));
        var localControls = (CombatClient)RuntimeHelpers.GetUninitializedObject(typeof(CombatClient));
        Set(localControls, "<CurrentAttackId>k__BackingField", 42);
        var clientCombat = new CombatSystem { Controls = localControls };
        var clientBridge = (OverhaulBridge)RuntimeHelpers.GetUninitializedObject(typeof(OverhaulBridge));
        Set(clientBridge, "combat", clientCombat); Set(clientBridge, "api", clientApi);
        var nativeClient = (MeleeSystemClient)RuntimeHelpers.GetUninitializedObject(typeof(MeleeSystemClient));
        Set(nativeClient, "_clientChannel", rawChannel);
        var clientBridgeField = typeof(OverhaulBridge).GetField("clientBridge", All)!;
        var previousClientBridge = clientBridgeField.GetValue(null);
        var hook = new Harmony("warband.tests.netcode-client-envelope");
        try
        {
            clientBridgeField.SetValue(null, clientBridge);
            hook.Patch(AccessTools.Method(typeof(MeleeSystemClient), "SendPackets", [typeof(IEnumerable<MeleeDamagePacket>)]),
                prefix: new HarmonyMethod(typeof(OverhaulBridge), "SendContacts"));
            var nativePacket = new MeleeDamagePacket { MainHand = true, DamageType = "SlashingAttack", Collider = "Head",
                Position = [1, 2, 3], TargetEntityId = 101, AttackerEntityId = 100 };
            using (CombatPrediction.Begin()) nativeClient.SendPackets(new[] { nativePacket, nativePacket });
            Check(sentEnvelopes.Count == 1 && rawSent == 0 && sentEnvelopes[0].AttackId == 42,
                "Actual native client Harmony hook wraps one owned contact and sends no raw batch");
            using var decoded = new MemoryStream(sentEnvelopes[0].Payload);
            Check(Serializer.Deserialize<MeleeDamagePacket>(decoded).Collider == "Head",
                "Client interception preserves the real head collider in the native wire payload");
            nativeClient.SendPackets(new[] { nativePacket });
            Check(rawSent == 1 && sentEnvelopes.Count == 1, "Unowned native contacts outside the prediction scope remain unchanged");
        }
        finally { hook.UnpatchAll(hook.Id); clientBridgeField.SetValue(null, previousClientBridge); }
        var bounds = new ModelContactBounds(api); victim.Pos.SetPos(0, 0, 0);
        for (int i = 0; i < 5; i++) { victim.Pos.X = i * .25; bounds.Record(victim, 1000 + i * 50); }
        Check(bounds.Origins(victim, 1200, .2).Any(p => p.X == 0), "Moving attacker receives the same bounded history as the target");
        victim.Pos.X = 20; bounds.Record(victim, 1210);
        Check(bounds.Origins(victim, 1210, .2).All(p => p.X == 20), "Teleport cannot carry an old attacker's reach into a new location");
        Console.WriteLine($"PASS: {checks} netcode/PvP checks; {scenarios} ordered latency/jitter/FPS scenarios");
    }
    private static void Set(object target, string field, object? value) => target.GetType().GetField(field, All)!.SetValue(target, value);
    private static object? Get(object target, string field) => target.GetType().GetField(field, All)!.GetValue(target);
}
