using Newtonsoft.Json;
using OpenTK.Mathematics;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Zornhau;
using Zornhau.Native.Animations;

static class GuardAnimationChecks
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var clips = JsonConvert.DeserializeObject<Dictionary<string, AnimationJson>>(
            File.ReadAllText(Path.Combine(root, "assets/zornhau/config/overhaul-fallback.json")))!;
        var player = new Holder();
        var claymore = new Item { Tool = EnumTool.Sword, Code = new("game:blade-claymore-ruined") };
        player.Right.Itemstack = new ItemStack(claymore);
        check(StandaloneAnimations.TwoHanded(player), "Empty offhand enables two-handed guards on a claymore");
        player.Left.Itemstack = new ItemStack(new Item { Code = new("game:torch-basic-lit") });
        check(!StandaloneAnimations.TwoHanded(player), "An occupied offhand never gets pulled into a two-handed guard");
        player.Left.Itemstack = null;
        player.Right.Itemstack = new ItemStack(new Item { Tool = EnumTool.Sword, Code = new("game:blade-arming-ruined") });
        check(!StandaloneAnimations.TwoHanded(player), "An ordinary one-handed sword keeps its original guards with an empty offhand");

        string game = Environment.GetEnvironmentVariable("VSPath") ?? Environment.GetEnvironmentVariable("VS_GAME_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "vintagestory");
        foreach (string rig in new[] { "seraph", "seraph-faceless", "seraph-hairless" })
        {
            var shape = JsonConvert.DeserializeObject<Shape>(File.ReadAllText(
                Path.Combine(game, "assets/game/shapes/entity/humanoid", rig + ".json")))!;
            var bones = new Dictionary<string, ShapeElement>();
            void Read(ShapeElement[] elements)
            {
                foreach (var bone in elements) { bones[bone.Name!] = bone; Read(bone.Children ?? []); }
            }
            Read(shape.Elements);
            float[] Arm(PlayerItemFrame frame, bool left, float pitch = 0)
            {
                var matrix = Mat4f.Create();
                foreach (string name in left ? new[] { "UpperArmL", "LowerArmL", "ItemAnchorL" }
                    : new[] { "UpperArmR", "LowerArmR", "ItemAnchor" })
                {
                    var pose = new ElementPose { ForElement = bones[name] };
                    StandaloneAnimations.Apply(frame, bones[name], pose);
                    if (name.StartsWith("Upper")) CombatAim.RotateArm(pose, pitch);
                    Mat4f.Mul(matrix, matrix, bones[name].GetLocalTransformMatrix(0, Mat4f.Create(), pose));
                }
                return matrix;
            }
            foreach (Direction direction in Enum.GetValues<Direction>())
            {
                var stock = clips[CombatGuardAnimations.Code(direction)].ToAnimation()
                    .Interpolate(PlayerItemFrame.Zero, TimeSpan.FromSeconds(1));
                var json = clips[CombatGuardAnimations.Code(direction, true)];
                var animation = json.ToAnimation();
                var guard = animation.Interpolate(PlayerItemFrame.Zero, TimeSpan.FromSeconds(1));
                check(json.Hold && json.PlayerKeyFrames.All(f => f.Elements.ContainsKey("UpperArmL")
                    && f.Elements.ContainsKey("LowerArmL")), rig + "/" + direction + ": held guard drives both left joints");
                var original = Arm(stock, false); var weapon = Arm(guard, false);
                check(original.Zip(weapon).All(pair => Math.Abs(pair.First - pair.Second) < 1e-6),
                    direction + ": two-handed guard preserves the saved right hand and blade exactly");
                var left = Arm(guard, true);
                var rightPoint = BladeGeometry.Transform(weapon, Vector3d.Zero, Vector3d.Zero);
                var leftPoint = BladeGeometry.Transform(left, Vector3d.Zero, Vector3d.Zero);
                double separation = (leftPoint - rightPoint).Length;
                check(separation > .075 && separation < .17,
                    rig + "/" + direction + ": left fist supports the right grip without overlapping it or detaching the arm; distance=" + separation);
                check(json.PlayerKeyFrames.All(f => f.Elements.Where(e => e.Key.EndsWith("L"))
                    .All(e => e.Value.Take(3).All(v => v == 0))), "The left arm reaches by rotating shoulder/elbow, never by stretching or translating them");
                foreach (float pitch in new[] { -60, 0, 60 })
                {
                    var aimedRight = BladeGeometry.Transform(Arm(guard, false, pitch * GameMath.DEG2RAD), Vector3d.Zero, Vector3d.Zero);
                    var aimedLeft = BladeGeometry.Transform(Arm(guard, true, pitch * GameMath.DEG2RAD), Vector3d.Zero, Vector3d.Zero);
                    check(Math.Abs((aimedLeft - aimedRight).Length - separation) < 1e-5,
                        "Both hands retain their grip when looking up/down");
                }
                var current = clips[CombatGuardAnimations.Code(Direction.Right, true)].ToAnimation()
                    .Interpolate(PlayerItemFrame.Zero, TimeSpan.FromMilliseconds(430));
                check(JsonConvert.SerializeObject(animation.Interpolate(current, TimeSpan.Zero).Player)
                    == JsonConvert.SerializeObject(current.Player), "Raising a two-handed guard starts from the current attack/ready pose");
                check(JsonConvert.SerializeObject(guard.Player) == JsonConvert.SerializeObject(
                    animation.Interpolate(PlayerItemFrame.Zero, TimeSpan.FromSeconds(5)).Player),
                    "Holding guard never replays or drops the second hand");
            }
        }
    }
}
