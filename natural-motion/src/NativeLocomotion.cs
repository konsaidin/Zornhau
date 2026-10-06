using Vintagestory.API.Common;

namespace NaturalMotion;

// Remove precisely what the engine just added for locomotion, including its
// easing-out clips. Other contributors (idle, combat, hurt, head offsets) stay.
// Read-only: the original animation list, frames and running states are intact.
internal sealed class NativeLocomotion
{
    private readonly Dictionary<AnimationFrame, Dictionary<string, ElementPose>> frames = new();
    private readonly ElementPose sum = new();

    internal static bool Owns(string? code, bool walk, bool air) =>
        walk && code is ("walk" or "sprint" or "walkback" or "walkleft" or "walkright")
        || air && code is ("newjump" or "creativefly");

    internal BonePose Contribution(IAnimator? animator, string bone, bool walk, bool air)
    {
        sum.Clear();
        if (animator is not AnimatorBase engine) return default;
        for (int i = 0; i < engine.ActiveAnimationCount; i++)
        {
            var run = engine.CurAnims[i]; var clip = run.Animation;
            if (!Owns(clip.Code, walk, air) || clip.PrevNextKeyFrameByFrame == null || clip.QuantityFrames <= 0) continue;
            int at = Math.Clamp((int)run.CurrentFrame, 0, clip.QuantityFrames - 1);
            var pair = clip.PrevNextKeyFrameByFrame[at];
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
                if (pose.ForElement.Name is { } name) result[name] = pose;
                Visit(pose.ChildElementPoses);
            }
        }
        Visit(frame.RootElementTransforms); frames[frame] = result;
        return result;
    }
}
