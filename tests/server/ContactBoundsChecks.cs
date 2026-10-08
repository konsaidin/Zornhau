using System.Reflection;
using System.Runtime.CompilerServices;
using CombatOverhaul.Colliders;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Zornhau;

internal static class ContactBoundsChecks
{
    internal static void Run()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        int checks = 0;
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks++; }
        var assembly = typeof(CombatSystem).Assembly;
        var type = assembly.GetType("Zornhau.OverhaulContactBounds")!;
        var historyType = assembly.GetType("Zornhau.ContactPositionHistory")!;
        var world = Stub.Make<IWorldAccessor>((m, a) => m.Name == "get_Side" ? EnumAppSide.Server : Stub.Default(m.ReturnType));
        var api = Stub.Make<ICoreAPI>((m, a) => m.Name == "get_Side" ? EnumAppSide.Server : Stub.Default(m.ReturnType));
        var validator = Activator.CreateInstance(type, all, null, new object[] { api }, null)!;
        bool Validate(Entity target, Vec3d point, long now, double ping, out string detail)
        {
            object[] args = { target, point, now, ping, "" };
            bool result = (bool)type.GetMethod("Validate", all)!.Invoke(validator, args)!;
            detail = (string)args[^1]; return result;
        }
        void Record(Entity target, long time) => type.GetMethod("Record", all)!.Invoke(validator, new object[] { target, time });
        string game = Environment.GetEnvironmentVariable("VSPath") ?? Environment.GetEnvironmentVariable("VS_GAME_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "vintagestory");
        var shape = JsonConvert.DeserializeObject<Shape>(File.ReadAllText(game + "/assets/survival/shapes/entity/animal/mammal/bear/brown-adult.json"))!;
        var patch = JArray.Parse(File.ReadAllText(".artifacts/compat/unpacked/overhaulliblegacycompat_1.2.14/assets/overhaulliblegacycompat/patches/colliders/carnivores.json"));
        var config = patch.First(p => p["target"]?.ToString() == "bear-adult")["value"]!["elements"]!;
        var names = new HashSet<string>(config.Children<JProperty>().SelectMany(p => p.Value.Values<string>()).Select(s => s!), StringComparer.Ordinal);
        var colliders = (CollidersEntityBehavior)RuntimeHelpers.GetUninitializedObject(typeof(CollidersEntityBehavior));
        typeof(CollidersEntityBehavior).GetField("<CollidersTypes>k__BackingField", all)!.SetValue(colliders, names.ToDictionary(n => n, _ => ColliderTypes.Torso));
        var target = new BoundEntity { Api = api, World = world, Alive = true, EntityId = 800 };
        target.SetProperties(new EntityProperties { Client = new EntityClientProperties(Array.Empty<Vintagestory.API.Datastructures.JsonObject>(), new()) { LoadedShape = shape, Size = 1.05f },
            Server = new EntityServerProperties(Array.Empty<Vintagestory.API.Datastructures.JsonObject>(), new()) });
        target.Properties.Server.Behaviors.Add(colliders);
        target.Pos.SetPos(100, 115, 200);
        target.CollisionBox = new Cuboidf(-.65f, 0, -.65f, .65f, 1.2f, .65f);
        target.SelectionBox = new Cuboidf(-.75f, 0, -.75f, .75f, 1.8f, .75f);
        int outsideOldBox = 0, nativeVertices = 0;
        void Walk(ShapeElement[] elements, ShapeElement? parent)
        {
            foreach (var element in elements)
            {
                element.ParentElement = parent;
                if (element.Name != null && names.Contains(element.Name))
                {
                    var native = new ShapeElementCollider(element);
                    foreach (var vertex in native.ElementVertices)
                    foreach (double angle in new[] { 0, .4, Math.PI / 2, Math.PI, 5.8 })
                    {
                        double x = (vertex.X - .5) * 1.05, y = vertex.Y * 1.05, z = (vertex.Z - .5) * 1.05;
                        var world = new Vec3d(100 + x * Math.Cos(angle) - z * Math.Sin(angle), 115 + y,
                            200 + x * Math.Sin(angle) + z * Math.Cos(angle));
                        bool oldAccepts = Math.Abs(world.X - 100) <= 1.4 && Math.Abs(world.Z - 200) <= 1.4
                            && world.Y >= 114.25 && world.Y <= 116.95;
                        if (!oldAccepts) outsideOldBox++;
                        Check(Validate(target, world, 1000, .1, out _), "Configured native bear vertices must fit the model bound at every yaw");
                        nativeVertices++;
                    }
                }
                if (element.Children != null) Walk(element.Children, element);
            }
        }
        Walk(shape.Elements, null);
        Check(nativeVertices > 100 && outsideOldBox > 0, "Regression fixture includes real CO model contacts rejected by the old movement box");
        Check(!Validate(target, new Vec3d(120, 115, 200), 1000, .1, out var bad) && bad.Contains("bounds=outside"),
            "Model validation still rejects contacts far outside the creature");
        Check(!Validate(target, new Vec3d(100, 150, 200), 1000, .1, out _), "Model validation retains finite vertical limits");
        Check(!Validate(target, new Vec3d(double.NaN, 115, 200), 1000, .1, out _), "Nonfinite contacts cannot pass the bounds check");
        foreach (var offset in new[] { new Vec3d(-1.39, 0, -1.39), new Vec3d(1.39, 1.94, 1.39) })
            Check(Validate(target, target.Pos.XYZ.AddCopy(offset), 1000, .1, out _), "Previously accepted movement-box corners are preserved");
        var plain = new BoundEntity { Api = api, World = world, Alive = true, EntityId = 801 };
        plain.CollisionBox = plain.SelectionBox = new Cuboidf(-.3f, 0, -.3f, .3f, 1.8f, .3f);
        for (int i = 0; i <= 4; i++) { plain.Pos.SetPos(i * .25, 0, 0); Record(plain, 1000 + i * 50); }
        var contact = new Vec3d(-.9, 1, 0);
        Check(!Validate(plain, contact, 1200, 0, out _), "Low ping does not authorize an older target position outside its lookback");
        Check(Validate(plain, contact, 1200, .2, out var lag) && lag.Contains("bounds=history"), "A moving target's actual recent position can validate an older model contact");
        Check(!Validate(plain, contact, 1551, .2, out _), "Expired or untracked history cannot keep an old location hittable");
        plain.Pos.SetPos(20, 0, 0); Record(plain, 1205);
        Check(!Validate(plain, contact, 1205, .2, out _), "A teleport clears old target locations");
        Check(!Validate(plain, new Vec3d(1, 1, 0), 1205, 999, out _), "Claiming huge latency cannot permit remote contacts");
        foreach (double ping in new[] { -1d, 0, .1, .4, 999, double.NaN, double.PositiveInfinity })
        {
            int lookback = (int)historyType.GetMethod("Lookback", all)!.Invoke(null, new object[] { ping })!;
            Check(lookback >= 150 && lookback <= 350, "Server ping lookback is always bounded");
        }
        plain.Pos.SetPos(21, 0, 0); Record(plain, 1500);
        Check(!Validate(plain, new Vec3d(19.1, 1, 0), 1500, .4, out _), "A tracking gap clears an otherwise recent old location");
        var history = Activator.CreateInstance(historyType, true)!;
        var record = historyType.GetMethod("Record", all)!;
        var recent = historyType.GetMethod("Recent", all)!;
        record.Invoke(history, new object[] { 90L, 1000L, 0, new Vec3d() });
        record.Invoke(history, new object[] { 90L, 1050L, 1, new Vec3d(.5, 0, 0) });
        var samples = (System.Collections.IEnumerable)recent.Invoke(history, new object[] { 90L, 1050L, 0, new Vec3d(), 350 })!;
        Check(!samples.Cast<object>().Any(), "History cannot authorize a target in another dimension");
                type.GetMethod("Clear", all)!.Invoke(validator, null);
        Check(!Validate(plain, contact, 1205, .2, out _), "Clearing runtime state drops all contact history");
        Console.WriteLine($"PASS: {checks} production target bounds and movement-history checks ({outsideOldBox} native contacts outside old box)");
    }
}

public class BoundEntity : Holder
{
    public void SetProperties(EntityProperties properties) => Properties = properties;
}
