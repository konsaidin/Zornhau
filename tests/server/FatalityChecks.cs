using System.Reflection;
using System.Runtime.CompilerServices;
using OpenTK.Mathematics;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using WarbandCombat;

internal static class FatalityChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        foreach (var direction in Enum.GetValues<Direction>())
        foreach (var type in Enum.GetValues<EnumDamageType>())
        foreach (bool head in new[] { false, true })
        {
            var expected = !head ? FatalityKind.None : type == EnumDamageType.BluntAttack ? FatalityKind.HeadCrush
                : type == EnumDamageType.SlashingAttack && direction is Direction.Left or Direction.Right
                ? FatalityKind.Decapitation : FatalityKind.None;
            Check(FatalityRules.Choose(head, type, direction) == expected, "Only lateral head cuts sever the head; blunt head hits crush it");
        }
        foreach (string name in new[] { "head", "Head", "skull", "head-front" })
            Check(FatalityRules.HeadElement(name), "Recognize genuine head collider names");
        foreach (string? name in new[] { null, "", "neck", "headgear", "left-arm", "torso" })
            Check(!FatalityRules.HeadElement(name), "Body and equipment names cannot authorize a head effect");
        var eye = new Vector3d(0, 1.6, 0);
        Check(FatalityRules.HeadPoint(eye, eye, 1), "Head center passes plausibility check");
        Check(FatalityRules.HeadPoint(new(0, .6, 0), new(0, .6, 0), 1), "Crouching eye position is supported");
        foreach (var point in new[] { Vector3d.Zero, new Vector3d(.8, 1.6, 0), new Vector3d(0, 2.5, 0), new Vector3d(double.NaN, 1.6, 0) })
            Check(!FatalityRules.HeadPoint(point, eye, 1), "A forged label cannot turn a remote or body contact into a head effect");
        Check(!FatalityRules.HeadPoint(eye, eye, double.NaN), "Nonfinite model size is rejected");
        foreach (var name in new[] { "mace", "woodenclub", "quarterstaff", "warhammer" })
        foreach (var direction in Enum.GetValues<Direction>())
        {
            var player = new Holder(); player.Right.Itemstack = new ItemStack(new Item { Code = new("test:" + name) });
            Check(CombatBalance.StandaloneDamageType(player, direction) == EnumDamageType.BluntAttack, "Standalone blunt weapons must not decapitate");
        }
        Physics(Check);
        Mesh(Check);
        Server(Check);
        Camera(Check);
        Gore(Check);
        StockDummyVoice(Check);
        var packet = new FatalityPacket { Id = 9, Victim = 14, Kind = FatalityKind.HeadCrush, Dimension = 1,
            Position = [2, 3, 4], HelmetPrefix = "steelhelmet", AgeMs = 120,
            Impulse = [1, 2, 3], SprayPosition = [2, 3.1, 4] };
        using var stream = new MemoryStream(); Serializer.Serialize(stream, packet); stream.Position = 0;
        var decoded = Serializer.Deserialize<FatalityPacket>(stream);
        Check(decoded.Id == 9 && decoded.Kind == FatalityKind.HeadCrush && decoded.Position.SequenceEqual(packet.Position)
            && decoded.HelmetPrefix == packet.HelmetPrefix && decoded.AgeMs == 120
            && decoded.Impulse.SequenceEqual(packet.Impulse) && decoded.SprayPosition.SequenceEqual(packet.SprayPosition),
            "Fatality protocol preserves event, appearance and validated blood impulse/contact metadata");
        using var movementStream = new MemoryStream();
        var movement = new FatalityMotion { Id = 4, Part = 0, Position = [1, 2, 3], Rotation = [0, 1, 0],
            Impact = 1, ImpactSpeed = 4.5f, ImpactPosition = [1, 0, 3] };
        Serializer.Serialize(movementStream, movement); movementStream.Position = 0;
        var received = Serializer.Deserialize<FatalityMotion>(movementStream);
        Check(received.Impact == 1 && received.ImpactSpeed == 4.5f && received.ImpactPosition.SequenceEqual(movement.ImpactPosition),
            "Authoritative collision metadata survives the real protobuf protocol");
        Console.WriteLine($"PASS: {checks} production fatality rules, physics, mesh and death-hook checks");
    }

    private static void Physics(Action<bool, string> check)
    {
        bool Ground(Vector3d p, double r) => p.Y - r < 0;
        var fallen = new FatalityBody(new(0, 2, 0), new(2, 1.7, 0), .18);
        for (int i = 0; i < 3000; i++) { fallen.Advance(.01, Ground); check(fallen.Position.Y >= .18 - 1e-9, "Head cannot fall through terrain"); }
        check(fallen.Resting && fallen.Position.Y < .19 && fallen.Position.X > .1, "Head falls, moves, bounces and eventually rests");
        check(fallen.Impact == 1 && fallen.ImpactSpeed >= 1.2 && Math.Abs(fallen.ImpactPosition.Y) < .03,
            "Falling head reports one substantial contact at the real ground surface");
        var quiet = new FatalityBody(new(0, .18, 0), Vector3d.Zero, .18);
        for (int i = 0; i < 100; i++) quiet.Advance(.01, Ground);
        check(quiet.Impact == 0, "Tiny resting floor contacts cannot produce repeated wet landing sounds");
        var thinWall = new FatalityBody(new(0, 1, 0), new(100, 0, 0), .07);
        thinWall.Advance(.02, (p, r) => p.X + r > .45 && p.X - r < .5);
        check(thinWall.Position.X < .45 && thinWall.Velocity.X < 0, "Fast fragments cannot tunnel through a thin wall");
        Vector3d Simulate(int fps)
        {
            var body = new FatalityBody(new(0, 3, 0), new(1, 2, .5), .18);
            for (int i = 0; i < fps * 5; i++) body.Advance(1d / fps, Ground);
            return body.Position;
        }
        var reference = Simulate(100);
        foreach (int fps in new[] { 30, 60, 144 }) check((Simulate(fps) - reference).Length < .002, "Fixed physics steps are independent of render cadence");
    }

    private static MeshData Fixture()
    {
        var mesh = new MeshData(8, 12, true, true, true, true) { CustomInts = new CustomMeshDataPartInt() { InterleaveSizes = [1], InterleaveStride = 4 } };
        for (int face = 0; face < 2; face++)
        {
            float bottom = face == 0 ? 1.5f : .5f, top = bottom + .3f;
            mesh.AddVertex(0, bottom, 0, .1f, .1f); mesh.AddVertex(.3f, bottom, 0, .2f, .1f);
            mesh.AddVertex(.3f, top, 0, .2f, .2f); mesh.AddVertex(0, top, 0, .1f, .2f);
            if (face == 1) for (int uv = 8; uv < 16; uv++) mesh.Uv[uv] += .4f;
            mesh.AddIndices(face * 4, face * 4 + 1, face * 4 + 2, face * 4, face * 4 + 2, face * 4 + 3);
            mesh.AddTextureId(42);
            for (int corner = 0; corner < 4; corner++) { mesh.CustomInts.Add(face == 0 ? 2 : 3); mesh.AddNormal(0, 0, 1); }
        }
        return mesh;
    }

    private static void Mesh(Action<bool, string> check)
    {
        var source = Fixture(); var ids = new HashSet<int> { 2 };
        var bloody = FatalityMesh.Blood(source, ids, [], 71);
        check(bloody.VerticesCount > source.VerticesCount && bloody.VerticesCount <= source.VerticesCount + 36,
            "Head blood creates bounded, small surface patches rather than recoloring the whole character");
        check(bloody.xyz.Take(source.VerticesCount * 3).SequenceEqual(source.xyz.Take(source.VerticesCount * 3))
            && bloody.Rgba.Take(source.RgbaCount).SequenceEqual(source.Rgba.Take(source.RgbaCount)),
            "Original skin and torso geometry/colors remain untouched beneath the blood patches");
        check(bloody.CustomInts.Count == bloody.VerticesCount
            && bloody.CustomInts.Values.Skip(source.VerticesCount).Take(bloody.VerticesCount - source.VerticesCount).All(i => i == 2),
            "All blood patches stay attached to the correct head animation joint");
        check(FatalityMesh.Filter(bloody, face => !FatalityMesh.HeadFace(bloody, ids, face)).VerticesCount == 4,
            "Decapitation removes the blood-covered head from the corpse, preserving the torso");
        var neck = FatalityMesh.Blood(source, [], new HashSet<int> { 3 }, 71);
        check(neck.VerticesCount > source.VerticesCount
            && neck.CustomInts.Values.Skip(source.VerticesCount).Take(neck.VerticesCount - source.VerticesCount).All(i => i == 3),
            "Neck blood uses its own joint and remains on the corpse after head separation");
        check(FatalityMesh.Blood(source, ids, [], 71).xyz.SequenceEqual(bloody.xyz),
            "The same victim has stable blood patches across re-tessellation");
        var detached = FatalityMesh.Filter(bloody, face => FatalityMesh.HeadFace(bloody, ids, face));
        check(detached.Rgba.Take(detached.RgbaCount).Where((_, i) => i % 4 == 1).Any(g => g == 6),
            "Detached heads and helmet geometry retain the blood colors");
        var regions = new[] { new TextureAtlasPosition { atlasTextureId = 42, x1 = .09f, x2 = .21f, y1 = .09f, y2 = .21f } };
        check(FatalityMesh.TextureFace(source, 0, regions) && !FatalityMesh.TextureFace(source, 1, regions), "Helmet extraction respects the actual atlas region rather than copying skin faces");
        var head = FatalityMesh.Filter(source, face => FatalityMesh.HeadFace(source, ids, face));
        var body = FatalityMesh.Filter(source, face => !FatalityMesh.HeadFace(source, ids, face));
        check(head.VerticesCount == 4 && body.VerticesCount == 4 && head.CustomInts.Values.Take(4).All(id => id == 2), "Head separation preserves correct engine joint attributes");
        check(body.CustomInts.Count == body.VerticesCount && body.CustomInts.Values.Take(body.CustomInts.Count).All(id => id == 3)
            && body.NormalsCount == body.VerticesCount, "Filtering with byte strides keeps the body's own bones and normal count");
        var crushed = source.Clone(); FatalityMesh.Crush(crushed, face => FatalityMesh.HeadFace(crushed, ids, face));
        check(crushed.xyz[7] < source.xyz[7], "Crushing actually deforms the face");
        check(crushed.xyz.Skip(12).Take(12).SequenceEqual(source.xyz.Skip(12).Take(12)), "Torso geometry is not deformed");
        var matrices = Enumerable.Range(0, 4).SelectMany(_ => Mat4f.Create()).ToArray();
        matrices[2 * 16 + 12] = .4f;
        FatalityMesh.Bake(head, matrices, Mat4f.Create(), new(10, 0, 0), new(9, 0, 0));
        check(Math.Abs(head.xyz[0] - 1.4) < .00001 && head.CustomInts == null, "Detached mesh bakes actual joint and camera-relative world transforms");
        check(head.xyz.Take(head.VerticesCount * 3).All(float.IsFinite), "Baked mesh stays finite");
        FatalityMesh.Recenter(head);
        check(Math.Abs(head.xyz.Take(head.VerticesCount * 3).Where((_, i) => i % 3 == 0).Average()) < .00001,
            "Late observers center the head on its rigid body even after the corpse's pose moved");
        var remaining = Enumerable.Range(0, 18).Where(i => FatalityMesh.FragmentPart(i) < 0).ToHashSet();
        var flying = Enumerable.Range(0, 3).SelectMany(p => Enumerable.Range(0, 18).Where(i => FatalityMesh.FragmentPart(i) == p)).ToHashSet();
        check(!remaining.Overlaps(flying) && remaining.Count == 12 && flying.Count == 6, "Fragments detach a minority of shell faces without duplicating the helmet");
    }

    private static void Server(Action<bool, string> check)
    {
        long now = 1000; IServerPlayer? owner = null;
        var sent = new List<object>();
        var calendar = Stub.Make<IGameCalendar>((m, a) => Stub.Default(m.ReturnType));
        var network = Stub.Make<IServerNetworkChannel>((m, a) => { if (m.Name == "SendPacket") sent.Add(a![0]!); return Stub.Default(m.ReturnType); });
        var world = Stub.Make<IServerWorldAccessor>((m, a) => m.Name switch
        { "get_ElapsedMilliseconds" => now, "get_AllOnlinePlayers" => new IPlayer[] { owner! }, "PlayerByUid" => owner,
            "get_Side" => EnumAppSide.Server, "get_Calendar" => calendar, _ => Stub.Default(m.ReturnType) });
        var events = Stub.Make<IServerEventAPI>((m, a) => Stub.Default(m.ReturnType));
        var networkApi = Stub.Make<IServerNetworkAPI>((m, a) => m.Name == "GetChannel" ? network : Stub.Default(m.ReturnType));
        var logger = Stub.Make<ILogger>((m, a) => Stub.Default(m.ReturnType));
        var api = Stub.Make<ICoreServerAPI>((m, a) => m.Name switch
        { "get_World" => world, "get_Event" => events, "get_Network" => networkApi, "get_Logger" => logger, _ => Stub.Default(m.ReturnType) });
        var inventory = new InventoryGeneric(20, "fatality-test", null);
        Stub.InventoryManager = Stub.Make<IPlayerInventoryManager>((m, a) => m.Name == "GetOwnInventory" ? inventory : Stub.Default(m.ReturnType));
        try
        {
            using var manager = new CombatFatalities(api);
            foreach (var direction in Enum.GetValues<Direction>())
            foreach (var type in new[] { EnumDamageType.SlashingAttack, EnumDamageType.PiercingAttack, EnumDamageType.BluntAttack })
            foreach (bool head in new[] { false, true })
            foreach (bool helmet in new[] { false, true })
            {
                sent.Clear(); now += 100;
                var victim = new Holder { Api = api, World = world, Alive = true, EntityId = now };
                victim.LocalEyePos.Set(0, 1.6, 0); owner = Stub.Player(victim);
                var attacker = new Holder { Alive = true, EntityId = 1 };
                var armor = new FatalityHelmet { Code = new("game:test-helmet") };
                inventory[(int)EnumCharacterDressType.ArmorHead].Itemstack = helmet ? new ItemStack(armor) : null;
                var source = new DirectionalDamage { SourceEntity = attacker, Type = type, Direction = direction };
                manager.RecordContact(victim, source, direction, new(0, head ? 1.6 : .5, 0), head ? "Head" : "torso");
                manager.OnDeath(victim, source);
                check(sent.Count == 0 && armor.Wear == 0, "A live target cannot trigger cosmetic death or gear wear");
                victim.Alive = false;
                // Exercise the real GuardBehavior callback, not a separate test implementation.
                new GuardBehavior(victim, new CombatSystem { Fatalities = manager }).OnEntityDeath(source);
                var expected = FatalityRules.Choose(head, type, direction);
                var packets = sent.OfType<FatalityPacket>().ToArray();
                check(packets.Length == (expected == FatalityKind.None ? 0 : 1), "Only the verified lethal source creates one death event");
                if (packets.Length > 0) check(packets[0].Kind == expected, "Server death event respects side-only cuts and blunt crushing");
                check(armor.Wear == (helmet && expected == FatalityKind.HeadCrush ? 35 : 0), "Only a worn helmet receives configured durability loss");
                int count = sent.Count; manager.OnDeath(victim, source);
                check(sent.Count == count, "Repeated death callbacks cannot duplicate effects or fragments");
                manager.Reset(victim);
                check(victim.WatchedAttributes.GetInt(FatalityRules.Attribute) == 0 && victim.WatchedAttributes.GetString("warbandCrushedHelmet") == "", "Revival clears mesh deformation metadata");
            }
            sent.Clear();
            var target = new Holder { Api = api, World = world, Alive = true, EntityId = 9000 };
            target.LocalEyePos.Set(0, 1.6, 0); owner = Stub.Player(target);
            var registered = new DirectionalDamage { SourceEntity = new Holder(), Type = EnumDamageType.SlashingAttack };
            manager.RecordContact(target, registered, Direction.Left, new(0, 1.6, 0), "Head"); target.Alive = false;
            manager.OnDeath(target, new DamageSource { Type = EnumDamageType.SlashingAttack });
            check(sent.Count == 0, "Unregistered death source cannot consume another hit's head contact");
            registered.Blocked = true; manager.OnDeath(target, registered);
            check(sent.Count == 0, "Blocked hit can never sever a head");
            foreach (var direction in new[] { Direction.Left, Direction.Overhead })
            {
                sent.Clear(); now += 100;
                var killed = new FatalityPlayer { Api = api, World = world, Alive = true, EntityId = now };
                killed.LocalEyePos.Set(0, 1.6, 0); owner = Stub.Player(killed);
                killed.SidedProperties.Behaviors.Add(new GuardBehavior(killed, new CombatSystem { Fatalities = manager }));
                var death = new DirectionalDamage { SourceEntity = new FatalityPlayer { Api = api, World = world, Code = new("game:player") }, Type = EnumDamageType.SlashingAttack };
                manager.RecordContact(killed, death, direction, new(0, 1.6, 0), "Head");
                killed.Die(EnumDespawnReason.Death, death);
                check(!killed.Alive && sent.OfType<FatalityPacket>().Count() == (direction == Direction.Left ? 1 : 0),
                    "Actual engine EntityPlayer.Die forwards the exact lethal source to our side-only death effect");
            }
        }
        finally { Stub.InventoryManager = null; }
    }

    private static void Camera(Action<bool, string> check)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        long now = 1000;
        var victim = new Holder { Alive = false, EntityId = 71 };
        victim.LocalEyePos.Set(0, 1.6, 0); victim.Pos.SetPos(10, 20, 30);
        var physicsPosition = victim.Pos.XYZ.Clone();
        var player = (Vintagestory.Client.NoObf.ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.Client.NoObf.ClientPlayer));
        var data = Vintagestory.Client.NoObf.ClientWorldPlayerData.CreateNew(); data.EntityPlayer = victim;
        typeof(Vintagestory.Client.NoObf.ClientPlayer).GetField("worlddata", fields)!.SetValue(player, data);
        var world = Stub.Make<FatalityTestWorld>((m, a) => m.Name switch
        { "get_Player" => player, "get_ElapsedMilliseconds" => now, "GetEntityById" => victim, _ => Stub.Default(m.ReturnType) });
        var events = Stub.Make<IClientEventAPI>((m, a) => Stub.Default(m.ReturnType));
        var api = Stub.Make<ICoreClientAPI>((m, a) => m.Name switch
        { "get_World" => world, "get_Event" => events, _ => Stub.Default(m.ReturnType) });
        using var renderer = new FatalityRenderer(api, true);
        void Create(long id)
        {
            renderer.Receive(new FatalityPacket { Id = id, Victim = 71, Kind = FatalityKind.Decapitation, Position = [10, 21.6, 30] });
            var effect = typeof(FatalityRenderer).GetField("cameraEffect", fields)!.GetValue(renderer)!;
            effect.GetType().GetField("Ready", fields)!.SetValue(effect, true);
            var parts = (Array)effect.GetType().GetField("Parts", fields)!.GetValue(effect)!;
            var part = parts.GetValue(0)!;
            part.GetType().GetField("Mesh", fields)!.SetValue(part, new MultiTextureMeshRef([], []));
            renderer.Receive(new FatalityFrame { Bodies = [new FatalityMotion { Id = id, Part = 0, Position = [11, .18, 31], Rotation = [.1, .4, .2] }] });
        }
        Create(1);
        var camera = new Vintagestory.Client.NoObf.Camera { CamSourcePosition = new(10, 20, 30), OriginPosition = new(), Yaw = 1, Pitch = -.2 };
        camera.forwardVec.Set(0, 0, -1);
        // Runs both actual engine camera calls under our Harmony prefix. No
        // rendering or image inspection is involved in this integration test.
        camera.Update(.02f, null!);
        check(camera.CameraEyePos.DistanceTo(new(11, .18, 31)) < .031, "Real engine camera follows the detached head's server position");
        check(camera.CameraMatrix.All(double.IsFinite) && camera.CameraMatrixOriginf.All(float.IsFinite), "Both world and camera-relative matrices remain finite while tumbling");
        check(victim.Pos.XYZ.DistanceTo(physicsPosition) == 0 && camera.Yaw == 1 && camera.Pitch == -.2,
            "Death camera never teleports the player or changes mouse pitch/yaw");
        now += 100; victim.Alive = true;
        camera.GetCameraMatrix(camera.OriginPosition, camera.CamSourcePosition, 1, -.2, new AABBIntersectionTest(world));
        check(typeof(FatalityRenderer).GetField("cameraEffect", fields)!.GetValue(renderer) == null, "Respawning returns control to the normal engine camera");
        victim.Alive = false; Create(2); camera.Update(.02f, null!);
        now += FatalityRules.CameraMs + 1;
        camera.GetCameraMatrix(camera.OriginPosition, camera.CamSourcePosition, 1, -.2, new AABBIntersectionTest(world));
        check(typeof(FatalityRenderer).GetField("cameraEffect", fields)!.GetValue(renderer) == null, "Camera also restores after its bounded death interval");
        victim.Alive = true; victim.WatchedAttributes.SetInt(FatalityRules.Attribute, 0);
        renderer.Receive(new FatalityPacket { Id = 3, Victim = 71, Kind = FatalityKind.Decapitation, Position = [10, 21, 30], AgeMs = 10000 });
        check(victim.WatchedAttributes.GetInt(FatalityRules.Attribute) == 0, "Receiving an old severed head never removes a respawned player's head");
    }

    private static void Gore(Action<bool, string> check)
    {
        long now = 1000;
        var particles = new List<SimpleParticleProperties>(); var sounds = new List<AssetLocation>();
        var owner = new Holder { Alive = true, EntityId = 71 }; owner.Pos.SetPos(10, 20, 30);
        var player = (Vintagestory.Client.NoObf.ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.Client.NoObf.ClientPlayer));
        var data = Vintagestory.Client.NoObf.ClientWorldPlayerData.CreateNew(); data.EntityPlayer = owner;
        typeof(Vintagestory.Client.NoObf.ClientPlayer).GetField("worlddata", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(player, data);
        var world = Stub.Make<FatalityTestWorld>((m, a) =>
        {
            if (m.Name == "get_Player") return player;
            if (m.Name == "get_ElapsedMilliseconds") return now;
            if (m.Name == "SpawnParticles" && a![0] is SimpleParticleProperties p) particles.Add(p);
            if (m.Name == "PlaySoundAt" && a![0] is AssetLocation s) sounds.Add(s);
            return Stub.Default(m.ReturnType);
        });
        var events = Stub.Make<IClientEventAPI>((m, a) => Stub.Default(m.ReturnType));
        var api = Stub.Make<ICoreClientAPI>((m, a) => m.Name switch
        { "get_World" => world, "get_Event" => events, _ => Stub.Default(m.ReturnType) });
        FatalityPacket Packet(long id, FatalityKind kind = FatalityKind.Decapitation, int age = 0, int dimension = 0)
            => new() { Id = id, Victim = 72, Kind = kind, AgeMs = age, Dimension = dimension,
                Position = [10, 21.6, 30], SprayPosition = [10.1, 21.6, 30], Impulse = [2, 1.7, .8] };
        using (var renderer = new FatalityRenderer(api, false))
        {
            var packet = Packet(1); renderer.Receive(packet);
            int count = particles.Count, audio = sounds.Count;
            check(count == 3 && audio == 2, "One fresh severing emits blood, tissue, neck blood and positional wet audio");
            renderer.Receive(packet);
            check(particles.Count == count && sounds.Count == audio, "Duplicate death packets cannot repeat gore");
            renderer.Receive(Packet(2, age: 500)); renderer.Receive(Packet(3, dimension: 1));
            var far = Packet(4); far.Position = [1000, 21, 1000]; far.SprayPosition = [];
            renderer.Receive(far);
            check(particles.Count == count && sounds.Count == audio, "Late observers, other dimensions and distant events do not replay gore");
            var invalid = Packet(5); invalid.Impulse = [double.NaN, 0, 0]; renderer.Receive(invalid);
            invalid = Packet(6); invalid.SprayPosition = [500, 21, 30]; renderer.Receive(invalid);
            check(particles.Count == count && sounds.Count == audio, "Invalid vectors or remote spray contact cannot emit particles or audio");
            var hit = new FatalityMotion { Id = 1, Part = 0, Position = [10, 20.18, 30], Rotation = [0, 0, 0],
                Impact = 1, ImpactSpeed = 5, ImpactPosition = [10, 20, 30] };
            renderer.Receive(new FatalityFrame { Bodies = [hit] });
            check(particles.Count == count + 1 && sounds.Count == audio + 1 && sounds.Last().Path.Contains("land"),
                "A server-confirmed substantial terrain collision emits one wet landing and splash");
            renderer.Receive(new FatalityFrame { Bodies = [hit] });
            check(particles.Count == count + 1 && sounds.Count == audio + 1, "Repeated movement snapshots cannot repeat the landing sound");
            var crush = Packet(7, FatalityKind.HeadCrush); crush.HelmetPrefix = "ironhelmet"; renderer.Receive(crush);
            check(sounds.Last().Path.Contains("shieldblock-metal") && particles.Count == count + 3,
                "Helmet crush layers metal with flesh; no decapitated neck stream");
        }
        check(particles.All(p => p.Quantity <= 52 && p.LifeLength <= 1.3f && p.WithTerrainCollision && p.Bounciness == 0),
            "Gore uses bounded, short-lived particles that collide with terrain");
        foreach (var switches in new[] { (false, false), (true, false), (false, true) })
        {
            particles.Clear(); sounds.Clear();
            var gore = new FatalityGore(api, switches.Item1, switches.Item2); gore.Begin(Packet(8));
            check((particles.Count > 0) == switches.Item1 && (sounds.Count > 0) == switches.Item2,
                "Blood and flesh audio can be disabled independently");
        }
        int AtFps(int fps)
        {
            particles.Clear(); sounds.Clear(); now = 1000;
            var gore = new FatalityGore(api, true, false); gore.Begin(Packet(9));
            int initial = particles.Count;
            for (int frame = 0; frame < fps * 2; frame++)
            { now = 1000 + frame * 1000 / fps; gore.Trail(9, new(10, 21, 30), new(10, 21.37, 30), now); }
            int trails = particles.Count - initial;
            gore.Remove(9); now = 1200; gore.Trail(9, new(10, 21, 30), new(10, 21.37, 30), now);
            check(particles.Count - initial == trails, "Removing an effect immediately stops its blood emission");
            return trails;
        }
        foreach (int fps in new[] { 30, 60, 144 }) check(AtFps(fps) is >= 13 and <= 17,
            "Blood trail stays bounded at different render rates and expires after 1.2 seconds");
    }

    private static void StockDummyVoice(Action<bool, string> check)
    {
        int packetId = 0; byte[]? message = null;
        var network = Stub.Make<IServerNetworkAPI>((m, a) =>
        {
            if (m.Name == "BroadcastEntityPacket") { packetId = (int)a![1]!; message = (byte[])a[2]!; }
            return Stub.Default(m.ReturnType);
        });
        var server = Stub.Make<ICoreServerAPI>((m, a) => m.Name == "get_Network" ? network : Stub.Default(m.ReturnType));
        var dummy = new TrainingDummy { Api = server, EntityId = 8 };
        dummy.PlayEntitySound("death");
        check(packetId == 1231 && message != null
            && Vintagestory.API.Util.SerializerUtil.Deserialize<EnumTalkType>(message) == EnumTalkType.Death,
            "Dummy death sends the stock character voice event instead of the recorded male NPC scream");
    }
}

internal sealed class FatalityHelmet : Item
{
    internal int Wear;
    public override int GetMaxDurability(ItemStack stack) => 100;
    public override void DamageItem(IWorldAccessor world, Entity byEntity, ItemSlot slot, int amount = 1, bool playSound = true) => Wear += amount;
}

public interface FatalityTestWorld : IClientWorldAccessor, IWorldIntersectionSupplier { }

internal sealed class FatalityPlayer : Holder
{
    public override string GetName() => "fatality-fixture";
}
