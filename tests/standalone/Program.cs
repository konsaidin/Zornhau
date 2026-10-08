using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using Newtonsoft.Json;
using OpenTK.Mathematics;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Zornhau;

int checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
var box = new PhysicalBox(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, Vector3d.UnitZ, new(.1, .1, .1));
Check(box.Contact(new(new(-1, 0, 0), new(1, 0, 0)), 0, out var entry, out var point) && Math.Abs(entry - .45) < 1e-9,
    "Thin blade enters the real box surface");
Check(Math.Abs(point.X + .1) < 1e-9, "Returned contact is on target, not the blade center");
Check(box.Contact(new(new(-1, .13, 0), new(1, .13, 0)), .031, out _, out point) && Math.Abs(point.Y - .1) < 1e-9,
    "Three-centimeter padding hits a near edge");
Check(!box.Contact(new(new(-1, .13, .13), new(1, .13, .13)), .031, out _, out _),
    "Capsule radius does not turn square corners into false hits");
Check(box.Contact(new(Vector3d.Zero, Vector3d.Zero), .03, out _, out point) && point == Vector3d.Zero, "Zero-length probe remains finite");
Check(!box.Contact(new(new(double.NaN, 0, 0), Vector3d.Zero), .03, out _, out _), "Nonfinite geometry is rejected");

var random = new Random(739);
for (int test = 0; test < 250; test++)
{
    Vector3d RandomPoint() => new(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1);
    var a = RandomPoint(); var b = RandomPoint(); double radius = .02 + random.NextDouble() * .3;
    bool result = box.Contact(new(a, b), radius, out entry, out point);
    double minimum = double.PositiveInfinity;
    for (int i = 0; i <= 4000; i++)
    {
        var p = a + (b - a) * (i / 4000d);
        var clamped = new Vector3d(Math.Clamp(p.X, -.1, .1), Math.Clamp(p.Y, -.1, .1), Math.Clamp(p.Z, -.1, .1));
        minimum = Math.Min(minimum, (p - clamped).Length);
    }
    Check(result == minimum <= radius || Math.Abs(minimum - radius) < .0005, "Exact capsule distance agrees with independent dense samples");
    if (result)
    {
        Check(Math.Abs(point.X) <= .10000001 && Math.Abs(point.Y) <= .10000001 && Math.Abs(point.Z) <= .10000001,
            "Every contact stays on or inside the target model");
        Check((a + (b - a) * entry - point).Length <= radius + 1e-8, "Contact proof lies within blade radius");
    }
}

foreach (double yaw in new[] { 0, .37, 1.5, Math.PI, 5.1 })
{
    var x = new Vector3d(Math.Cos(yaw), 0, -Math.Sin(yaw)); var z = new Vector3d(Math.Sin(yaw), 0, Math.Cos(yaw));
    var rotated = new PhysicalBox(new(1000000, 120, -1000000), x, Vector3d.UnitY, z, new(.1));
    Check(rotated.Contact(new(rotated.Center - x, rotated.Center + x), .03, out _, out point), "Oriented model works at large world coordinates");
    Check(Math.Abs(Vector3d.Dot(point - rotated.Center, x)) <= .10000001, "World-space point belongs to rotated model");
}

foreach (int fps in new[] { 10, 20, 30, 60, 144 })
{
    var before = new BladeSegment(new(-.6, 0, -.6), new(-.6, 0, .6));
    bool contacted = false;
    int count = Math.Max(1, fps / 10);
    for (int frame = 1; frame <= count; frame++)
    {
        double x = -.6 + 1.2 * frame / count;
        var after = new BladeSegment(new(x, 0, -.6), new(x, 0, .6));
        int steps = BladeGeometry.SweepSteps(before, after);
        for (int i = 0; i <= steps; i++) contacted |= box.Contact(BladeSegment.Lerp(before, after, i / (double)steps), .03, out _, out _);
        before = after;
    }
    Check(contacted, "Fast lateral cut cannot tunnel at " + fps + " FPS");
}
Check(box.Contact(new(new(0, 0, .1), new(0, 0, .8)), 0, out _, out _), "Exact current/end-of-sweep contact is included");
var wall = new[] { PhysicalBox.FromCuboid(new Cuboidf(.25f, -1, -1, .35f, 1, 1), Vector3d.Zero) };
Check(StandalonePhysics.TerrainStop(new(Vector3d.Zero, Vector3d.UnitX), .03, wall) < .5, "Physical wall blocks the blade before a target behind it");

string game = Environment.GetEnvironmentVariable("VSPath") ?? Environment.GetEnvironmentVariable("VS_GAME_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "vintagestory");
int shapes = 0;
foreach (var family in new[] { "blade", "knife", "spear", "axe" })
foreach (string path in Directory.EnumerateFiles(game + "/assets/survival/shapes/item/tool/" + family, "*.json", SearchOption.AllDirectories))
{
    var shape = JsonConvert.DeserializeObject<Shape>(File.ReadAllText(path))!;
    var pieces = WeaponGeometry.FromShape(shape, new CompositeShape());
    Check(pieces.Length > 0 && pieces.Length <= 96, "Physical profile exists for vanilla model " + path);
    foreach (var piece in pieces)
        Check(piece.Segment.Finite && piece.Segment.Length > 0 && double.IsFinite(piece.Radius) && piece.Radius > 0,
            "Asset profile has finite blade geometry");
    shapes++;
}
var bear = JsonConvert.DeserializeObject<Shape>(File.ReadAllText(game + "/assets/survival/shapes/entity/animal/mammal/bear/brown-adult.json"))!;
foreach (var e in bear.Elements) e.TrimTextureNamesAndResolveFaces();
foreach (var (element, matrix) in BladeGeometry.Elements(bear.Elements, Mat4f.Create()).Where(e => BladeGeometry.BodyElement(e.element)))
{
    var part = PhysicalBox.FromMatrix(matrix, element, Vector3d.Zero)!.Value;
    Check(part.Contact(new(part.Center - part.X, part.Center + part.X), .03, out _, out _), "Every real bear body cuboid is hittable without CO");
}

var segment = new BladeSegment(new(0, .5, 0), new(0, .5, 1));
Check(StandalonePhysics.ValidateBlade(segment, segment, new(0, .5, .5), Vector3d.Zero, 3, 1, .04, 1), "Valid standalone contact proof passes");
Check(!StandalonePhysics.ValidateBlade(segment, segment, new(1, .5, .5), Vector3d.Zero, 3, 1, .04, 1), "Point away from the blade is rejected");
Check(!StandalonePhysics.ValidateBlade(segment, segment, new(0, .5, .5), Vector3d.Zero, .3, 1, .04, 1), "Out-of-reach blade is rejected");
Check(!StandalonePhysics.ValidateBlade(segment, segment, new(0, .5, .5), Vector3d.Zero, 3, .2, .04, 1), "Forged blade length is rejected");
Check(!StandalonePhysics.ValidateBlade(segment, segment, new(0, .5, .5), Vector3d.Zero, 3, 1, .04, double.NaN), "Nonfinite sweep fraction is rejected");
Check(!StandalonePhysics.ValidateBlade(segment, new(new(3, .5, 0), new(3, .5, 1)), new(0, .5, .5), Vector3d.Zero, 6, 1, .04, 0), "Teleport-sized sweep is rejected");

var packet = new BladeContactPacket { AttackId = 123, Target = 82, Blade = 0, Previous = segment.Pack(), Current = segment.Pack(), Fraction = 1, Point = new[] { 0d, .5, .5 }, Rotation = [1, 3, 2] };
using (var buffer = new MemoryStream())
{
    Serializer.Serialize(buffer, packet); buffer.Position = 0;
    var copy = Serializer.Deserialize<BladeContactPacket>(buffer);
    Check(copy.AttackId == 123 && copy.Target == 82 && copy.Previous.SequenceEqual(packet.Previous)
        && copy.Rotation!.SequenceEqual(packet.Rotation), "Contact protocol preserves geometry, rotation and exact attack ID");
}
foreach (var tool in new[] { EnumTool.Sword, EnumTool.Spear, EnumTool.Axe, EnumTool.Knife })
{
    var timing = CombatSystem.Timing(new Item { Tool = tool });
    Check(timing.PhysicalContacts, "All standalone weapons have physical active windows");
    var fighter = new Fighter(); fighter.Apply(Command.Begin, Direction.Left, 1000, timing); fighter.Apply(Command.Release, Direction.Right, 1001, timing);
    Check(fighter.CanHit(1000 + timing.Windup) && fighter.Direction == Direction.Left, "Physical cut opens after full locked windup");
    Check(fighter.TakeHit(1000 + timing.Windup) && !fighter.CanHit(1000 + timing.Windup + 1), "First physical contact consumes the swing");
}

// Exercise the production server entry without loading a single Overhaul assembly.
long now = 1400;
var weapon = new PhysicsTestItem { ItemId = 710, Tool = EnumTool.Sword, Code = new AssetLocation("game:blade-falx-iron") };
var ownerEntity = new Holder { EntityId = 70, Alive = true };
var target = new PhysicsTestCreature { EntityId = 82, Alive = true, CollisionBox = new Cuboidf(-.2f, 0, -.2f, .2f, 1.8f, .2f) };
target.Pos.Z = .5;
var world = Stub.Make<IServerWorldAccessor>((m, a) => m.Name switch
{
    "get_Side" => EnumAppSide.Server, "get_ElapsedMilliseconds" => now,
    "GetEntityById" => (long)a![0]! == 82 ? target : ownerEntity,
    "get_BlockAccessor" => Stub.Make<IBlockAccessor>((b, args) => b.Name == "GetBlock" ? new Block { CollisionBoxes = Array.Empty<Cuboidf>() } : Stub.Default(b.ReturnType)),
    _ => Stub.Default(m.ReturnType)
});
var api = Stub.Make<ICoreServerAPI>((m, a) => m.Name switch
{
    "get_Side" => EnumAppSide.Server, "get_World" => world,
    "get_Event" or "get_Logger" or "get_Server" => Stub.Nested(m.ReturnType),
    _ => Stub.Default(m.ReturnType)
});
ownerEntity.Api = target.Api = api; ownerEntity.World = target.World = world;
packet.Rotation = CombatTurnAuthority.Capture(ownerEntity, true);
ownerEntity.Right.Itemstack = new ItemStack(weapon);
var owner = Stub.Player(ownerEntity); Stub.Privileges = true;
var system = new CombatSystem(); const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
typeof(CombatSystem).GetField("server", fields)!.SetValue(system, api);
var session = typeof(CombatSystem).GetMethod("GetSession", fields)!.Invoke(system, new object[] { owner })!;
session.GetType().GetField("Weapon")!.SetValue(session, ownerEntity.Right.Itemstack);
session.GetType().GetField("AttackId")!.SetValue(session, 123);
var state = (Fighter)session.GetType().GetField("Fighter")!.GetValue(session)!;
var standalone = new StandalonePhysics(api, system);
var geom = (WeaponGeometry)typeof(StandalonePhysics).GetField("geometry", fields)!.GetValue(standalone)!;
((Dictionary<int, BladePiece[]>)typeof(WeaponGeometry).GetField("profiles", fields)!.GetValue(geom)!)[710] = new[] { new BladePiece(segment, .04) };
void Begin()
{
    state.Reset(); CombatTurnAuthority.Observe(ownerEntity);
    state.Apply(Command.Begin, Direction.Right, 1000, CombatSystem.Timing(weapon));
    state.Apply(Command.Release, Direction.Right, 1001, CombatSystem.Timing(weapon));
}
Begin(); packet.AttackId = 122; standalone.OnContact(owner, packet);
Check(state.Phase == Phase.Swing && target.WatchedAttributes.GetInt("onHurtCounter") == 0, "Stale attack ID cannot deal damage or consume a current swing");
packet.AttackId = 123; standalone.OnContact(owner, packet);
Check(state.Phase == Phase.Recovery && target.WatchedAttributes.GetInt("onHurtCounter") == 1 && weapon.Attacks == 1,
    $"Real standalone server handler applies damage and durability once: phase={state.Phase} hurt={target.WatchedAttributes.GetInt("onHurtCounter")} attacks={weapon.Attacks} canAccept={system.CanAcceptPhysicalContact(owner)} privilege={owner.HasPrivilege("attackcreatures")} scale={WeaponGeometry.HandScale(weapon)}");
standalone.OnContact(owner, packet);
Check(target.WatchedAttributes.GetInt("onHurtCounter") == 1 && weapon.Attacks == 1, "Duplicate packet cannot apply a second hit");
Begin(); packet.Point = new[] { double.NaN, .5, .5 }; standalone.OnContact(owner, packet);
Check(state.Phase == Phase.Swing, "Malformed contact is rejected before consuming the swing");
packet.Point = new[] { 0d, .5, .5 }; Stub.Privileges = false; standalone.OnContact(owner, packet);
Check(state.Phase == Phase.Swing, "Creature attack permission is checked before consuming contact");
Stub.Privileges = true; ownerEntity.Pos.Dimension = target.Pos.Dimension = 1;
double dimensionY = ownerEntity.Pos.InternalY;
packet.Previous = packet.Current = new BladeSegment(new(0, dimensionY + .5, 0), new(0, dimensionY + .5, 1)).Pack();
packet.Point = new[] { 0d, dimensionY + .5, .5 }; standalone.OnContact(owner, packet);
Check(state.Phase == Phase.Recovery && weapon.Attacks == 1, "Changing dimension cancels the old attack before a contact can migrate with it");
Begin(); standalone.OnContact(owner, packet);
Check(state.Phase == Phase.Recovery && weapon.Attacks == 2, "Physical contacts use dimension-aware world coordinates on the server");
standalone.Dispose();
NativeChecks.Run(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")), Check);
Check(!AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name?.Contains("Overhaul", StringComparison.OrdinalIgnoreCase) == true),
    "Production standalone physics and damage run with no Overhaul libraries loaded");
Console.WriteLine($"PASS: {checks} independent physical combat checks on {shapes} vanilla weapon models; no CO assemblies");

public class PhysicsTestItem : Item
{
    public int Attacks;
    public override float GetAttackPower(ItemStack stack) => 4;
    public override void OnAttackingWith(IWorldAccessor world, Entity byEntity, Entity attackedEntity, ItemSlot slot) => Attacks++;
}

public class PhysicsTestCreature : EntityAgent
{
    private readonly QuietAnimationManager animations = new();
    public PhysicsTestCreature()
    { Properties = new EntityProperties { Server = new EntityServerProperties(Array.Empty<Vintagestory.API.Datastructures.JsonObject>(), new()) }; }
    public override bool ShouldReceiveDamage(DamageSource source, float damage) => true;
    public override IAnimationManager AnimManager { get => animations; set { } }
}
