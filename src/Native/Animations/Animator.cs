// Adapted from Overhaullib-Legacy, copyright (c) 2024 Maltiez, MIT.
// Source revision: 9bb564978967914c2d07fb13a60e5cc1749bb4ee.
// See third-party/overhaul-core/README.md and LICENSE.
using Vintagestory.API.Common;

namespace WarbandCombat.Native.Animations;

public class Animator
{
    public Animator(Animation animation, float animationSpeed = 1)
    {
        _currentAnimation = animation;
        _animationSpeed = animationSpeed;
    }

    public bool FinishOverride { get; set; } = false;

    public void Play(Animation animation, TimeSpan duration) => Play(animation, (float)(animation.TotalDuration / duration));
    public void Play(Animation animation, float animationSpeed)
    {
        _currentAnimation = animation;
        _animationSpeed = animationSpeed;
        _currentDuration = TimeSpan.Zero;
        _previousAnimationFrame = _lastFrame;
        _unfiredCallbacks.Clear();
        _unfiredCallbacks.AddRange(animation.CallbackFrames.OrderBy(frame => frame.DurationFraction).Select(frame => frame.Code));
    }

    public PlayerItemFrame Animate(TimeSpan delta, List<string> callbacks)
    {
        TimeSpan previousDuration = _currentDuration * _animationSpeed;
        _currentDuration += delta;
        TimeSpan adjustedDuration = _currentDuration * _animationSpeed;


        callbacks.Clear();
        _currentAnimation.GetCallbacks(previousDuration, adjustedDuration, callbacks);
        foreach (string callback in callbacks)
        {
            _unfiredCallbacks.Remove(callback);
        }

        _lastFrame = _currentAnimation.Interpolate(_previousAnimationFrame, adjustedDuration);
        return _lastFrame;
    }
    public bool Stopped() => _currentAnimation.TotalDuration <= _currentDuration * _animationSpeed;
    public bool Finished() => FinishOverride || (Stopped() && !_currentAnimation.Hold);
    public IEnumerable<string> GetUnfiredCallbacks() => _unfiredCallbacks;
    public void ClearUnfiredCallbacks() => _unfiredCallbacks.Clear();

    private PlayerItemFrame _previousAnimationFrame = PlayerItemFrame.Zero;
    private PlayerItemFrame _lastFrame = PlayerItemFrame.Zero;
    private TimeSpan _currentDuration = TimeSpan.Zero;
    private float _animationSpeed;
    private Animation _currentAnimation;
    private readonly List<string> _unfiredCallbacks = [];
}
