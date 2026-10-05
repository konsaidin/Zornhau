using Newtonsoft.Json;
using OpenTK.Mathematics;
using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using WarbandCombat;
using WarbandCombat.Native.Animations;
using WarbandCombat.Native.Colliders;
using NativeAnimation = WarbandCombat.Native.Animations.Animation;

static class NativeChecks
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var source = JsonConvert.DeserializeObject<Dictionary<string, AnimationJson>>(
            File.ReadAllText(Path.Combine(root, "third-party/reanimated/clips.json")))!;
        // A connected client receives the mod config, but no original entity JSON.
        var profileAsset = Stub.Make<IAsset>((m, a) => m.Name == "ToText"
            ? File.ReadAllText(Path.Combine(root, "assets/warbandcombat/config/native-colliders.json")) : Stub.Default(m.ReturnType));
        var assetApi = Stub.Make<ICoreAPI>((m, a) => m.Name switch
        {
            "get_Assets" => Stub.Make<IAssetManager>((call, args) => call.Name == "TryGet"
                && args?[0] is AssetLocation location && location.Path == "config/native-colliders.json" ? profileAsset : Stub.Default(call.ReturnType)),
            "get_Logger" => Stub.Nested(m.ReturnType),
            _ => Stub.Default(m.ReturnType)
        });
        var profiles = new NativeBodyColliders(assetApi); profiles.Load();
        check(profiles.Names(new("game:player"))?.Contains("UpperTorso") == true, "Native player body profile loads without server entity assets");
        check(profiles.Names(new("game:bear-black-adult-male"))?.Contains("shoulders") == true, "Bear age profile resolves through type/gender variants");
        check(profiles.Names(new("game:deer-redbrocket-adult-female"))?.Contains("Chest") == true, "Deer uses the native articulated profile on a connected client");
        foreach (var (code, original) in source)
        {
            var split = NativePhaseAnimations.Split(code, original);
            foreach (int strike in WeaponTiming.StrikeDurations)
            {
                var windup = split[NativePhaseAnimations.Code(code, "windup")].ToAnimation();
                var swing = split[NativePhaseAnimations.Code(code, "swing", strike)].ToAnimation();
                var back = split[NativePhaseAnimations.Code(code, "return")].ToAnimation();
                check(windup.Hold && swing.Hold && back.Hold, "Native phases hold their final pose without replay");
                var mid = swing.Interpolate(PlayerItemFrame.Zero, TimeSpan.FromMilliseconds(strike / 2));
                var startReturn = back.Interpolate(mid, TimeSpan.Zero);
                check(JsonConvert.SerializeObject(mid.Player) == JsonConvert.SerializeObject(startReturn.Player),
                    "Ported CO interpolation keeps the current grip at an early contact");
                var composer = new Composer();
                composer.Play(new AnimationRequest(windup, 1000f / 330, 1, "main", TimeSpan.FromMilliseconds(35), TimeSpan.FromMilliseconds(20), false));
                var held = composer.Compose(TimeSpan.FromMilliseconds(331));
                var later = composer.Compose(TimeSpan.FromMilliseconds(2000));
                check(JsonConvert.SerializeObject(held.Player) == JsonConvert.SerializeObject(later.Player), "Held windup does not keep moving");
                composer.Play(new AnimationRequest(swing, 1, 1, "main", TimeSpan.FromMilliseconds(35), TimeSpan.FromMilliseconds(20), false));
                var current = composer.Compose(TimeSpan.FromMilliseconds(strike / 2));
                composer.Play(new AnimationRequest(back, 1000f / 480, 1, "main", TimeSpan.FromMilliseconds(35), TimeSpan.FromMilliseconds(20), false));
                var replaced = composer.Compose(TimeSpan.Zero);
                check(JsonConvert.SerializeObject(current.Player) == JsonConvert.SerializeObject(replaced.Player),
                    "Ported composer replaces swing with return without resetting to zero");
            }
        }
        var sword = new Item { Tool = EnumTool.Sword, Code = new("game:blade-arming-ruined") };
        foreach (Direction direction in new[] { Direction.Left, Direction.Right })
            check(source.ContainsKey(StandaloneAnimations.Source(sword, direction, false)), "Standalone side cuts use Reanimated rather than the old one-handed clip");
        var claymore = new Item { Tool = EnumTool.Sword, Code = new("game:blade-claymore-ruined") };
        foreach (Direction direction in Enum.GetValues<Direction>())
        {
            var clip = source[StandaloneAnimations.Source(claymore, direction, true)];
            check(clip.PlayerKeyFrames.All(f => f.Elements.ContainsKey("UpperArmL") && f.Elements.ContainsKey("LowerArmL")), "Claymore drives both arms in every direction");
        }
        check(CombatSystem.Timing(sword).Strike == 260 && CombatSystem.Timing(claymore).Strike == 380,
            "Ported poses retain the proven CO family cut timings");
        check(StandalonePhysics.ContactRadius(.001) == .1 + CombatRules.WeaponContactPadding,
            "Thin blades keep CO's default contact radius and requested padding");
        var blade = new BladeSegment(new(-1, 0, 0), new(1, 0, 0));
        check(NativeBodyColliders.SweepSteps(blade, new(new(-1, 1, 0), new(1, 1, 0)), .13) >= 10,
            "Native blade sweep uses temporal subdivisions");

        foreach (string file in new[] {
            "/home/dead/Documents/vintagestory/assets/game/shapes/entity/humanoid/seraph.json",
            "/home/dead/Documents/vintagestory/assets/survival/shapes/entity/animal/mammal/bear/brown-adult.json" })
        {
            var shape = JsonConvert.DeserializeObject<Shape>(File.ReadAllText(file))!;
            var elements = Link(shape.Elements, null).Where(e => e.From?.Length == 3 && e.To?.Length == 3
                && Enumerable.Range(0, 3).All(i => e.To[i] > e.From[i])).ToArray();
            foreach (var element in elements)
            {
                element.JointId = 1;
                var inverse = element.GetInverseModelMatrix();
                var rest = Mat4f.Create(); Mat4f.Invert(rest, inverse);
                var pose = new Matrixf().Identity().Translate(.3, .2, -.4).RotateX(.35f).RotateY(.8f).Mul(rest);
                var joint = Mat4f.Create(); Mat4f.Mul(joint, pose.Values, inverse);
                float[] matrices = new float[32]; Array.Copy(Mat4f.Create(), matrices, 16); Array.Copy(joint, 0, matrices, 16, 16);
                var model = new Matrixf().Identity().Translate(1, 2, 3).RotateY(.7f).Scale(1.1f, 1.1f, 1.1f);
                var origin = new Vector3d(20000, 32768, -30000);
                var collider = new ShapeElementCollider(element);
                collider.Transform(matrices, model.Values, origin);
                var expected = Mat4f.Create(); Mat4f.Mul(expected, model.Values, pose.Values);
                var size = new Vector3d((element.To![0] - element.From![0]) / 16,
                    (element.To[1] - element.From[1]) / 16, (element.To[2] - element.From[2]) / 16);
                Vector3d[] local = [Vector3d.Zero, new(size.X, 0, 0), new(size.X, size.Y, 0), new(0, size.Y, 0),
                    new(0, 0, size.Z), new(size.X, 0, size.Z), size, new(0, size.Y, size.Z)];
                for (int i = 0; i < 8; i++)
                {
                    var actual = collider.InworldVertices[i];
                    var visible = BladeGeometry.Transform(expected, local[i], origin);
                    check((new Vector3d(actual.X, actual.Y, actual.Z) - visible).Length < 1e-5,
                        "Native joint vertices match the rendered model including parent pivots, scale and dimension");
                }
                var center = collider.InworldVertices.Aggregate(Vector4d.Zero, (a, b) => a + b) / 8;
                var point = new Vector3d(center.X, center.Y, center.Z);
                check(NativeBodyColliders.Contact(collider, new(point, point), .13, out _, out var contact) && BladeGeometry.Finite(contact),
                    "Native zero-motion blade contact remains finite");
            }
        }
        // Applying a native grip never writes camera-related body/eye elements.
        var native = source["combatoverhaul:sword-falx-slash-1"].ToAnimation().Interpolate(PlayerItemFrame.Zero, TimeSpan.FromMilliseconds(200));
        foreach (string name in new[] { "Head", "Neck", "eyesroot", "LowerTorso" })
        {
            var untouched = new ElementPose { degX = 23, degY = 11, degZ = 7, translateY = .2f };
            StandaloneAnimations.Apply(native, new ShapeElement { Name = name }, untouched);
            check(untouched.degX == 23 && untouched.degY == 11 && untouched.degZ == 7 && untouched.translateY == .2f,
                "Embedded frame application leaves camera anchors untouched");
        }
    }

    private static IEnumerable<ShapeElement> Link(ShapeElement[] elements, ShapeElement? parent)
    {
        foreach (var element in elements)
        {
            element.ParentElement = parent; yield return element;
            foreach (var child in Link(element.Children ?? Array.Empty<ShapeElement>(), element)) yield return child;
        }
    }
}
