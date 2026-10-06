using Vintagestory.API.Common;

namespace NaturalMotion;

internal readonly record struct BonePose(float X, float Y, float Z, float TX, float TY, float TZ)
{
    internal static BonePose Blend(BonePose a, BonePose b, float t) => new(
        a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t,
        a.TX + (b.TX - a.TX) * t, a.TY + (b.TY - a.TY) * t, a.TZ + (b.TZ - a.TZ) * t);
    internal static BonePose From(ElementPose pose) => new(pose.degX, pose.degY, pose.degZ,
        pose.translateX, pose.translateY, pose.translateZ);
    internal void Apply(ElementPose pose)
    {
        pose.degX = X; pose.degY = Y; pose.degZ = Z;
        pose.translateX = TX; pose.translateY = TY; pose.translateZ = TZ;
    }
    public static BonePose operator +(BonePose a, BonePose b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.TX + b.TX, a.TY + b.TY, a.TZ + b.TZ);
    public static BonePose operator -(BonePose a, BonePose b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.TX - b.TX, a.TY - b.TY, a.TZ - b.TZ);
    public static BonePose operator *(BonePose a, float weight) => new(a.X * weight, a.Y * weight, a.Z * weight, a.TX * weight, a.TY * weight, a.TZ * weight);
}

// Read the installed game's authored clips, without packaging any game asset.
// Curves are per channel: missing values inherit their surrounding keyframes.
internal sealed class StockPoses(IEnumerable<Animation> animations)
{
    private readonly Dictionary<string, Animation> clips = animations
        .Where(a => a?.Code != null).GroupBy(a => a.Code!, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Clip, string Bone, bool Gait), Curve[]> curves = new();
    internal bool Available => clips.ContainsKey("walk") && clips.ContainsKey("walkleft") && clips.ContainsKey("walkright")
        && clips.ContainsKey("walkback") && clips.ContainsKey("creativefly");
    private readonly record struct Key(float Frame, float Value);
    private sealed record Curve(Key[] Keys, bool Rotation)
    {
        internal float At(float frame, int length)
        {
            if (Keys.Length == 0) return 0;
            if (Keys.Length == 1) return Keys[0].Value;
            Key before = Keys[^1], after = Keys[0];
            foreach (var key in Keys)
            { if (key.Frame <= frame) before = key; else { after = key; break; } }
            float start = before.Frame, end = after.Frame;
            if (end <= start) end += length;
            if (frame < start) frame += length;
            float t = Math.Clamp((frame - start) / Math.Max(.001f, end - start), 0, 1);
            float difference = after.Value - before.Value;
            if (Rotation) difference = (difference + 540) % 360 - 180;
            return before.Value + difference * t;
        }
    }

    internal BonePose Sample(string code, string bone, double cycle, bool gait = false)
    {
        if (!clips.TryGetValue(code, out var clip) || clip.QuantityFrames <= 0 || clip.KeyFrames == null) return default;
        // The authored lateral clips contain two strides (52 frames), while
        // walk/sprint/back contain one. Use one 26-frame lateral stride.
        int length = gait && code is ("walkleft" or "walkright") ? clip.QuantityFrames / 2 : clip.QuantityFrames;
        if (!curves.TryGetValue((code, bone, gait), out var channels))
        {
            var keys = Enumerable.Range(0, 6).Select(_ => new List<Key>()).ToArray();
            foreach (var keyframe in clip.KeyFrames.Where(k => k?.Elements != null).OrderBy(k => k.Frame))
            {
                if (keyframe.Frame >= length) continue;
                var element = keyframe.Elements.FirstOrDefault(k => k.Key.Equals(bone, StringComparison.OrdinalIgnoreCase)).Value;
                if (element == null) continue;
                double?[] values = [element.RotationX, element.RotationY, element.RotationZ, element.OffsetX, element.OffsetY, element.OffsetZ];
                for (int i = 0; i < 6; i++)
                    if (values[i] is { } value && double.IsFinite(value)) keys[i].Add(new(keyframe.Frame, (float)(i < 3 ? value : value / 16)));
            }
            curves[(code, bone, gait)] = channels = keys.Select((keys, i) => new Curve(keys.ToArray(), i < 3)).ToArray();
        }
        // Canonical phase 0 plants the right foot, .5 the left. Authored clips
        // start on different feet / swing phases; align them before blending.
        if (gait)
        {
            // Contacts calibrated from the lowest sole position on the stock
            // 1.22.7 skeleton, in frames. Its two half-strides are not equal.
            (double right, double left) = code switch
            {
                "walk" => (8.1 / 25, 20.3 / 25),
                "sprint" => (5.1 / 24, 15.4 / 24),
                "walkback" => (4.7 / 25, 17.3 / 25),
                "walkleft" => (15.7 / 26, 4.5 / 26),
                "walkright" => (4.4 / 26, 15.8 / 26),
                _ => (0, .5)
            };
            if (left <= right) left += 1;
            double phase = cycle - Math.Floor(cycle);
            cycle = phase < .5 ? right + (left - right) * phase * 2
                : left + (right + 1 - left) * (phase - .5) * 2;
        }
        float frame = (float)((cycle - Math.Floor(cycle)) * length);
        return new(channels[0].At(frame, length), channels[1].At(frame, length),
            channels[2].At(frame, length), channels[3].At(frame, length),
            channels[4].At(frame, length), channels[5].At(frame, length));
    }

    internal BonePose Walk(string bone, MotionState state)
    {
        var forward = WideStep("walk", bone, state.Cycle, state.WalkStepScale);
        if (clips.ContainsKey("sprint")) forward = BonePose.Blend(forward, Sample("sprint", bone, state.Cycle, true), state.SprintBlend);
        BonePose Directional(string code) => BonePose.Blend(WideStep(code, bone, state.Cycle, state.WalkStepScale),
            DirectionalRun(code, bone, state.Cycle), state.SprintBlend);
        return forward * state.ForwardWeight + Directional("walkback") * state.BackWeight
            + Directional("walkleft") * state.LeftWeight + Directional("walkright") * state.RightWeight;
    }

    private BonePose WideStep(string code, string bone, double cycle, float scale)
    {
        // Widening the authored hip/knee arc shifts the lowest sole point.
        // Re-align contacts on the stock skeleton at the default 1.35 scale;
        // interpolate the correction for a user's different walking scale.
        (double right, double left) = code switch
        {
            "walk" => (.018, .514), "walkback" => (.062, .560),
            "walkleft" => (.280, .572), "walkright" => (.074, .780), _ => (0, .5)
        };
        double correction = Math.Clamp((scale - 1) / .35, 0, 1);
        return AuthoredStep(code, bone, ContactPhase(cycle, right * correction, .5 + (left - .5) * correction), scale);
    }

    private static double ContactPhase(double cycle, double right, double left)
    {
        double phase = cycle - Math.Floor(cycle);
        if (left <= right) left += 1;
        return phase < .5 ? right + (left - right) * phase * 2
            : left + (right + 1 - left) * (phase - .5) * 2;
    }

    private BonePose AuthoredStep(string code, string bone, double cycle, float scale)
    {
        var pose = Sample(code, bone, cycle, true);
        return bone is "UpperFootL" or "UpperFootR" or "LowerFootL" or "LowerFootR"
            ? pose with { X = pose.X * scale, Z = pose.Z * scale } : pose;
    }

    private BonePose DirectionalRun(string code, string bone, double cycle)
    {
        // Side/back running keeps knees pointing forwards. Widen the hip
        // sweep and add a distinct heel-lift swing instead of only speeding
        // up walkback/strafe or applying a forward run against the movement.
        (double right, double left) = code switch
        {
            "walkback" => (.178, .670), "walkleft" => (.314, .674),
            "walkright" => (.172, .814), _ => (0, .5)
        };
        cycle = ContactPhase(cycle, right, left);
        var pose = AuthoredStep(code, bone, cycle, 1.9f);
        double contact = bone.EndsWith('L') ? .5 : 0;
        float lift = (float)Math.Pow(Math.Max(0, Math.Sin(2 * Math.PI * (cycle - contact))), 2);
        if (bone is "UpperFootL" or "UpperFootR") pose = pose with { Z = pose.Z - 8 * lift };
        if (bone is "LowerFootL" or "LowerFootR") pose = pose with { Z = pose.Z + 18 * lift };
        if (bone == "LowerTorso") pose = pose with { Z = code == "walkback" ? -4 : 0,
            TY = pose.TY - .025f + .015f * (float)Math.Cos(4 * Math.PI * cycle) };
        if (bone == "UpperTorso") pose = pose with { Z = code == "walkback" ? 6 : 5 };
        return pose;
    }

    internal BonePose Air(string bone, MotionState state)
    {
        var pose = Sample("creativefly", bone, state.FlightCycle);
        string? opposite = bone switch
        {
            "UpperFootR" => "UpperFootL", "UpperFootL" => "UpperFootR",
            "LowerFootR" => "LowerFootL", "LowerFootL" => "LowerFootR", _ => null
        };
        if (opposite != null)
        {
            var mirrored = Sample("creativefly", opposite, state.FlightCycle);
            mirrored = mirrored with { X = -mirrored.X, Y = -mirrored.Y, TZ = -mirrored.TZ };
            pose = BonePose.Blend(pose, mirrored, state.AirSide);
            // Extend for push-off, then fold into the flight pose. The authored
            // jump clip supplies the bend; the envelope prevents restart snaps.
            var takeoff = Sample("jump", bone, .4 * Math.Clamp(state.JumpAge / .24, 0, 1));
            if (clips.ContainsKey("jump")) pose = BonePose.Blend(pose, takeoff, state.JumpPulse * .8f);
            pose *= state.JumpLegHeight;
        }
        return pose;
    }

    internal BonePose AirArms(string bone, MotionState state)
    {
        string? opposite = bone switch
        {
            "UpperArmR" => "UpperArmL", "UpperArmL" => "UpperArmR",
            "LowerArmR" => "LowerArmL", "LowerArmL" => "LowerArmR", _ => null
        };
        if (opposite == null) return default;
        BonePose Held(string name)
        {
            bool right = name.EndsWith('R');
            bool upper = name.StartsWith("Upper", StringComparison.Ordinal);
            // Freeze the authored raised-arm jump pose. Flight, stride and
            // push-off clocks must not introduce flapping within a jump.
            var pose = clips.ContainsKey("newjump") ? Sample("newjump", name, 0)
                : upper ? new BonePose(right ? 17 : -17, 0, -27, 0, 0, 0)
                : new BonePose(0, 0, -40, 0, 0, 0);
            return upper ? pose with { Z = pose.Z + (right ? -4 : 4) } : pose;
        }
        var mirrored = Held(opposite);
        mirrored = mirrored with { X = -mirrored.X, Y = -mirrored.Y, TZ = -mirrored.TZ };
        // The small asymmetry follows the same smoothed leading-leg change;
        // neither hand pumps back and forth during an individual airborne hop.
        return BonePose.Blend(Held(bone), mirrored, state.AirSide);
    }

    internal static BonePose Landing(string? bone, MotionState state)
    {
        float bend = state.LandingPulse * (1 - state.AirBlend);
        return bone switch
        {
            "UpperFootR" or "UpperFootL" => new(0, 0, -10 * bend, 0, 0, 0),
            "LowerFootR" or "LowerFootL" => new(0, 0, 20 * bend, 0, 0, 0),
            "LowerTorso" when !state.Combat => new(0, 0, 0, 0, -.025f * bend, 0),
            _ => default
        };
    }
}
