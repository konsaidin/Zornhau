using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

internal static class AnimationGeometry
{
    // Use the engine's transforms, not the generator's rotation implementation.
    internal static void Check()
    {
        string game = Environment.GetEnvironmentVariable("VSPath") ?? Environment.GetEnvironmentVariable("VS_GAME_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "vintagestory");
        using var shape = JsonDocument.Parse(File.ReadAllText($"{game}/assets/game/shapes/entity/humanoid/seraph.json"));
        var bones = new Dictionary<string, ShapeElement>();
        void Read(JsonElement elements)
        {
            foreach (var element in elements.EnumerateArray())
            {
                double[] Vector(string name) => element.TryGetProperty(name, out var value)
                    ? value.EnumerateArray().Select(v => v.GetDouble()).ToArray() : new double[3];
                double Angle(string name) => element.TryGetProperty(name, out var value) ? value.GetDouble() : 0;
                string name = element.GetProperty("name").GetString()!;
                bones[name] = new ShapeElement { Name = name, From = Vector("from"), To = Vector("to"),
                    RotationOrigin = Vector("rotationOrigin"), RotationX = Angle("rotationX"),
                    RotationY = Angle("rotationY"), RotationZ = Angle("rotationZ") };
                if (element.TryGetProperty("children", out var children)) Read(children);
            }
        }
        Read(shape.RootElement.GetProperty("elements"));
        float[] Transform(JsonElement elements, bool blade, bool elbow = false)
        {
            float[] matrix = Mat4f.Create();
            foreach (string name in new[] { "root", "LowerTorso", "UpperTorso", "UpperArmR", "LowerArmR", "ItemAnchor" })
            {
                if (elbow && name == "LowerArmR")
                {
                    var origin = bones[name].RotationOrigin!;
                    for (int i = 0; i < 3; i++)
                        matrix[12 + i] += (float)(matrix[i] * origin[0] + matrix[4 + i] * origin[1]
                            + matrix[8 + i] * origin[2]) / 16;
                    return matrix;
                }
                var pose = new ElementPose();
                if (elements.TryGetProperty(name, out var values))
                {
                    float Value(string key) => values.TryGetProperty(key, out var v) ? v.GetSingle() : 0;
                    pose.degX = Value("rotationX"); pose.degY = Value("rotationY"); pose.degZ = Value("rotationZ");
                    pose.translateX = Value("offsetX") / 16; pose.translateY = Value("offsetY") / 16;
                    pose.translateZ = Value("offsetZ") / 16;
                }
                Mat4f.Mul(matrix, matrix, bones[name].GetLocalTransformMatrix(0, Mat4f.Create(), pose));
            }
            // RenderItem adds RightHand (0,-180,0) to falx grip (85,0,2).
            if (blade)
            {
                Mat4f.RotateX(matrix, matrix, 85 * GameMath.DEG2RAD);
                Mat4f.RotateY(matrix, matrix, -180 * GameMath.DEG2RAD);
                Mat4f.RotateZ(matrix, matrix, 2 * GameMath.DEG2RAD);
            }
            return matrix;
        }
        using var patches = JsonDocument.Parse(File.ReadAllText("assets/zornhau/patches/animations.json"));
        var animations = patches.RootElement.EnumerateArray()
            .Where(p => p.GetProperty("file").GetString() == "game:shapes/entity/humanoid/seraph.json")
            .Select(p => p.GetProperty("value"))
            .ToDictionary(a => a.GetProperty("code").GetString()!);
        int checks = 0;
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        foreach (var patch in patches.RootElement.EnumerateArray())
        {
            if (patch.GetProperty("file").GetString() != "game:shapes/entity/humanoid/seraph.json") continue;
            var animation = patch.GetProperty("value");
            string code = animation.GetProperty("code").GetString()!;
            if (!code.StartsWith("zornhau-swing-") || code.Contains("overhead")) continue;
            var frames = animation.GetProperty("keyframes").EnumerateArray().ToArray();
            JsonElement Frame(int number) => frames.Single(f => f.GetProperty("frame").GetInt32() == number).GetProperty("elements");
            var contact = Transform(Frame(4), true);
            Require(contact[0] < -.999f && Math.Abs(contact[1]) < .001f && Math.Abs(contact[2]) < .001f,
                $"{code}: blade must point forward (-X), not sideways");
            Require(code.Contains("left") ? contact[5] < -.93f : contact[5] > .999f,
                $"{code}: the single cutting edge must face the selected cut");
            if (code.Contains("thrust"))
            {
                var ready = Transform(frames[0].GetProperty("elements"), false);
                var hit = Transform(Frame(4), false);
                Require(hit[12] < ready[12] - .4f, $"{code}: hand must extend forward");
                Require(Math.Abs(hit[14] - ready[14]) < .15f, $"{code}: thrust must stay near the same lateral line");
            }
            else
            {
                var approach = Transform(Frame(2), true);
                var follow = Transform(Frame(6), true);
                bool right = code.Contains("right");
                Require(right ? approach[2] < -.5f && follow[2] > .9f : approach[2] > .5f && follow[2] < -.85f,
                    $"{code}: blade must cross from the selected side to the opposite side");
                foreach (var frame in frames)
                {
                    if (frame.GetProperty("frame").GetInt32() == 0) continue;
                    var blade = Transform(frame.GetProperty("elements"), true);
                    // Falx edge is local -Z. Its horizontal tangent must agree
                    // with the direction of travel, including the held windup.
                    float edgeAlongCut = (-blade[8] * blade[2] + blade[10] * blade[0]) * (right ? 1 : -1);
                    Require(edgeAlongCut > (right ? .999f : .93f), $"{code}: blade must lead with its cutting edge during the cut");
                }
                var heldBlade = Transform(Frame(0), true);
                var heldHand = Transform(Frame(0), false);
                var heldElbow = Transform(Frame(0), false, elbow: true);
                var forearm = Enumerable.Range(0, 3).Select(i => heldHand[12 + i] - heldElbow[12 + i]).ToArray();
                float forearmLength = MathF.Sqrt(forearm.Sum(v => v * v));
                float parallel = Enumerable.Range(0, 3).Sum(i => heldBlade[i] * forearm[i]) / forearmLength;
                Require(right ? parallel > .89f && parallel < .98f : parallel > .99f,
                    $"{code}: left blade follows the forearm; right blade has only a small wrist cock");
                if (right)
                    Require(heldBlade[0] > (code.EndsWith("-fp") ? .05f : .1f) && heldBlade[0] < .4f,
                        $"{code}: right wrist must point the blade a little behind the shoulder");
                if (!right)
                {
                    Require(-contact[9] < -.3f, $"{code}: left cutting edge must travel diagonally downward");
                    if (!code.EndsWith("-fp"))
                        Require(heldBlade[1] > .85f && heldBlade[1] < .97f && Math.Abs(heldBlade[0]) < .15f,
                            "Left chamber must be less vertical with only a small fore/aft lean");
                }
                var recovery = animations[code.Replace("swing", "recovery")].GetProperty("keyframes").EnumerateArray().ToArray();
                var lastWrist = recovery[^1].GetProperty("elements").GetProperty("ItemAnchor");
                foreach (string axis in new[] { "rotationX", "rotationY", "rotationZ" })
                {
                    Require(lastWrist.GetProperty(axis).GetSingle() == 0,
                        $"{code}: recovery must reach zero Euler winding before blending to idle");
                    var contactWrist = Frame(4).GetProperty("ItemAnchor");
                    var recoveryWrist = recovery[0].GetProperty("elements").GetProperty("ItemAnchor");
                    Require(contactWrist.GetProperty(axis).GetSingle() == recoveryWrist.GetProperty(axis).GetSingle(),
                        $"{code}: contact and recovery must use the same Euler branch");
                }
                if (right && !code.EndsWith("-fp"))
                {
                    var raised = Transform(Frame(0), false);
                    var lowered = Transform(Frame(1), false);
                    var blade = Transform(Frame(1), true);
                    Require(lowered[13] < raised[13] - .3f && lowered[12] < raised[12] - .3f,
                        "Right cut must leave the raised chamber before crossing the target");
                    Require(blade[2] < -.9f, "Right chamber exit must keep the blade outside the face");
                }
            }
        }
        Console.WriteLine($"PASS: {checks} animation geometry checks using game matrices");
    }
}
