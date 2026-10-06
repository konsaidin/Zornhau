using Vintagestory.API.Common;

namespace NaturalMotion;

// Remove precisely what the engine just added for locomotion, including its
// easing-out clips. Other contributors (idle, combat, hurt, head offsets) stay.
// Read-only: the original animation list, frames and running states are intact.
internal sealed class NativeLocomotion
{
    private readonly Dictionary<AnimationFrame, Dictionary<string, ElementPose>> frames = new();
    private readonly ElementPose sum = new();

    internal static bool Owns(string? code, bool walk, bool air)
    {
        var name = code.AsSpan();
        if (name.EndsWith("-ifp", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        else if (name.EndsWith("-fp", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
        return walk && (name.Equals("walk", StringComparison.OrdinalIgnoreCase)
            || name.Equals("sprint", StringComparison.OrdinalIgnoreCase)
            || name.Equals("walkback", StringComparison.OrdinalIgnoreCase)
            || name.Equals("walkleft", StringComparison.OrdinalIgnoreCase)
            || name.Equals("walkright", StringComparison.OrdinalIgnoreCase)
            || name.Equals("walk-skirt", StringComparison.OrdinalIgnoreCase)
            || name.Equals("sprint-skirt", StringComparison.OrdinalIgnoreCase)
            || name.Equals("sprint-holdskirt", StringComparison.OrdinalIgnoreCase))
            || air && (name.Equals("newjump", StringComparison.OrdinalIgnoreCase)
                || name.Equals("jump", StringComparison.OrdinalIgnoreCase)
                || name.Equals("creativefly", StringComparison.OrdinalIgnoreCase));
    }

    internal BonePose Contribution(IAnimator? animator, string bone, bool walk, bool air)
    {
        sum.Clear();
        if (animator is not AnimatorBase engine) return default;
        for (int i = 0; i < Math.Min(engine.ActiveAnimationCount, engine.CurAnims.Length); i++)
        {
            var run = engine.CurAnims[i]; var clip = run?.Animation;
            if (run == null || clip == null) continue;
            if (!Owns(clip.Code, walk, air) || clip.PrevNextKeyFrameByFrame == null || clip.QuantityFrames <= 0) continue;
            int at = Math.Clamp((int)run.CurrentFrame, 0, clip.QuantityFrames - 1);
            if (at >= clip.PrevNextKeyFrameByFrame.Length) continue;
            var pair = clip.PrevNextKeyFrameByFrame[at];
            if (pair == null || pair.Length < 2 || pair[0] == null || pair[1] == null) continue;
            var now = pair[0];
            var next = clip.OnAnimationEnd == EnumEntityAnimationEndHandling.Hold && at + 1 == clip.QuantityFrames ? now : pair[1];
            if (!Index(now).TryGetValue(bone, out var a) || !Index(next).TryGetValue(bone, out var b)) continue;
            float length = next.FrameNumber > now.FrameNumber ? next.FrameNumber - now.FrameNumber : clip.QuantityFrames - now.FrameNumber + next.FrameNumber;
            float delta = run.CurrentFrame >= now.FrameNumber ? run.CurrentFrame - now.FrameNumber : clip.QuantityFrames - now.FrameNumber + run.CurrentFrame;
            // BlendedWeight is calculated for this bone immediately before the
            // transform hook. Use the same Add method as ClientAnimator.
            sum.Add(a, b, delta / Math.Max(1, length), run.BlendedWeight);
        }
        return BonePose.From(sum);
    }

    private Dictionary<string, ElementPose> Index(AnimationFrame frame)
    {
        if (frames.TryGetValue(frame, out var result)) return result;
        result = new(StringComparer.OrdinalIgnoreCase);
        void Visit(IEnumerable<ElementPose> poses)
        {
            foreach (var pose in poses)
            {
                if (pose?.ForElement?.Name is { } name) result[name] = pose;
                if (pose?.ChildElementPoses != null) Visit(pose.ChildElementPoses);
            }
        }
        if (frame.RootElementTransforms != null) Visit(frame.RootElementTransforms);
        frames[frame] = result;
        return result;
    }
}
