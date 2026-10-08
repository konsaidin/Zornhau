using CombatOverhaul.Animations;
using Newtonsoft.Json;

namespace Zornhau;

// Overhaul clips include their own windup, contact and recovery. Split those
// poses while retaining Zornhau's independent server-controlled timings.
internal static class OverhaulPhaseAnimations
{
    internal static string Code(string source, string phase, int strike = 140)
        => "zornhau:co-" + source.Replace(':', '-').Replace('/', '-') + "-" + phase
            + (phase == "swing" ? "-" + strike : "");

    internal static Dictionary<string, AnimationJson> Split(string code, AnimationJson source)
    {
        var result = new Dictionary<string, AnimationJson>();
        var frames = source.PlayerKeyFrames.OrderBy(f => f.EasingTime).Select(Clone).ToArray();
        if (frames.Length == 0) return result;
        Normalize(frames);
        float duration = frames[^1].EasingTime;
        var start = source.CallbackFrames.FirstOrDefault(f => f.Code == "start" || f.Code == "startParry");
        var stop = source.CallbackFrames.FirstOrDefault(f => f.Code == "stop");
        float startAt = start == null ? frames[0].EasingTime : duration * start.DurationFraction;
        float stopAt = stop == null ? duration : duration * stop.DurationFraction;
        int chamber = Array.FindLastIndex(frames, f => f.EasingTime <= startAt + .01f);
        chamber = Math.Max(0, chamber);
        int end = Array.FindIndex(frames, f => f.EasingTime >= stopAt - .01f);
        end = Math.Max(chamber, end < 0 ? frames.Length - 1 : end);

        var windup = Clone(frames[chamber]); windup.EasingTime = 1000;
        result[Code(code, "windup")] = Clip(new[] { windup }, true);

        foreach (int strike in WeaponTiming.StrikeDurations)
        {
            var swing = new List<PLayerKeyFrameJson>();
            var beginning = Clone(frames[chamber]); beginning.EasingTime = 0; swing.Add(beginning);
            if (end == chamber)
            {
                var held = Clone(frames[chamber]); held.EasingTime = strike; swing.Add(held);
            }
            else
            {
                // Preserve the pacing of the entire cut. Compressing all frames
                // after its first approach into 50 ms produces a visible second snap.
                for (int index = chamber + 1; index <= end; index++)
                {
                    var frame = Clone(frames[index]);
                    float fraction = (frames[index].EasingTime - frames[chamber].EasingTime)
                        / Math.Max(1, frames[end].EasingTime - frames[chamber].EasingTime);
                    frame.EasingTime = fraction * strike;
                    swing.Add(frame);
                }
            }
            // Preserve a short tail for camera turning after the cut completes.
            var tail = Clone(swing[^1]); tail.EasingTime = strike + CombatRules.ContactWindow; swing.Add(tail);
            result[Code(code, "swing", strike)] = Clip(swing.ToArray(), true);
        }

        var recovery = new List<PLayerKeyFrameJson>();
        var recoveryStart = Clone(frames[end]); recoveryStart.EasingTime = 0; recovery.Add(recoveryStart);
        // Some source returns revisit a chamber or flourish the blade. A single
        // continuous return to rest prevents that from resembling another attack.
        var rest = Clone(frames[^1]); rest.EasingTime = 1000; rest.EasingFunction = "Sin";
        recovery.Add(rest);
        Normalize(recovery.ToArray());
        result[Code(code, "recovery")] = Clip(recovery.ToArray(), true);
        // A single target frame blends from the composer's current pose. An
        // explicit end-of-cut frame would snap there after an early contact.
        result[Code(code, "return")] = Clip(new[] { Clone(rest) }, true);

        foreach (Direction direction in Enum.GetValues<Direction>())
        {
            var guard = Clone(frames[chamber]); guard.EasingTime = 1000;
            // Keep the author's jointed grip; shift the guard to the selected side.
            if (guard.Elements.TryGetValue("UpperArmR", out var upper))
            {
                if (direction == Direction.Right) upper[4] = (upper[4] ?? 0) - 25;
                if (direction == Direction.Left) upper[4] = (upper[4] ?? 0) + 25;
                if (direction == Direction.Overhead) upper[5] = (upper[5] ?? 0) - 20;
                if (direction == Direction.Thrust) upper[5] = (upper[5] ?? 0) + 15;
            }
            result[Code(code, "block-" + direction.ToString().ToLowerInvariant())] = Clip(new[] { guard }, true);
        }
        return result;
    }

    private static AnimationJson Clip(PLayerKeyFrameJson[] frames, bool hold) => new()
    {
        PlayerKeyFrames = frames, Hold = hold,
        // No native attack/block callbacks: Zornhau owns every state transition.
        CallbackFrames = Array.Empty<CallbackFrameJson>(),
        SoundFrames = Array.Empty<SoundFrameJson>()
    };

    private static PLayerKeyFrameJson Clone(PLayerKeyFrameJson frame)
    {
        var copy = JsonConvert.DeserializeObject<PLayerKeyFrameJson>(JsonConvert.SerializeObject(frame))!;
        copy.FOVMultiplier = 1; copy.BobbingAmplitude = 1;
        copy.PitchFollow = true; copy.PitchDontFollow = false;
        return copy;
    }

    private static void Normalize(PLayerKeyFrameJson[] frames)
    {
        for (int index = 1; index < frames.Length; index++)
            foreach (var (name, values) in frames[index].Elements)
                if (frames[index - 1].Elements.TryGetValue(name, out var previous))
                    for (int axis = 3; axis < Math.Min(6, values.Length); axis++)
                    {
                        if (!values[axis].HasValue || !previous[axis].HasValue) continue;
                        while (values[axis] - previous[axis] > 180) values[axis] -= 360;
                        while (values[axis] - previous[axis] < -180) values[axis] += 360;
                    }
    }
}
