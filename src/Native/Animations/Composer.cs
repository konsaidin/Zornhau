// Adapted from Overhaullib-Legacy, copyright (c) 2024 Maltiez, MIT.
// Source revision: 9bb564978967914c2d07fb13a60e5cc1749bb4ee.
// See third-party/overhaul-core/README.md and LICENSE.
using Vintagestory.API.Common;

namespace Zornhau.Native.Animations;

public delegate bool AnimationSpeedModifierDelegate(TimeSpan duration, ref TimeSpan delta);

public sealed class Composer
{
    public PlayerItemFrame Compose(TimeSpan delta)
    {
        while (_requestsQueue.Count > 0)
        {
            AnimationRequest request = _requestsQueue.Dequeue();
            ProcessRequest(request);
        }

        if (_speedModifierDelegate != null)
        {
            _speedModifierDuration += delta;
            if (!_speedModifierDelegate.Invoke(_speedModifierDuration, ref delta))
            {
                _speedModifierDelegate = null;
            }
        }

        if (_states.Count == 0) return PlayerItemFrame.Empty;

        foreach (AnimatorState state in _states.Values)
        {
            state.CurrentTime += delta;
            ProcessWeight(state);
        }

        _frameScratch.Clear();
        foreach (AnimatorState state in _states.Values)
        {
            PlayerItemFrame frame = state.Animator.Animate(delta, _callbackScratch);
            _frameScratch.Add((frame, state.CurrentWeight));

            foreach (string callbackId in _callbackScratch)
            {
                state.Request.CallbackHandler?.Invoke(callbackId);
            }

            if (state.Animator.Finished())
            {
                IEnumerable<string> unfiredCallbacks = state.Animator.GetUnfiredCallbacks();
                foreach (string callbackId in unfiredCallbacks)
                {
                    state.Request.CallbackHandler?.Invoke(callbackId);
                }
                state.Animator.ClearUnfiredCallbacks();
            }
        }

        PlayerItemFrame result = PlayerItemFrame.Compose(_frameScratch);

        _categoriesToRemove.Clear();
        foreach ((string category, AnimatorState state) in _states)
        {
            if (state.Animator.Finished() && state.WeightState == AnimatorWeightState.Finished)
            {
                Func<bool>? callback = state.Request.FinishCallback;
                bool removeCategory = true;
                if (callback != null && !state.CallbacksCalled)
                {
                    removeCategory = !callback.Invoke();
                    state.CallbacksCalled = true;
                }

                if (removeCategory)
                {
                    _categoriesToRemove.Add(category);
                }
            }

            if (state.Animator.Stopped() && state.Request.FinishCallback != null)
            {
                Func<bool>? callback = state.Request.FinishCallback;
                bool removeCategory = false;
                if (callback != null && !state.CallbacksCalled)
                {
                    removeCategory = !callback.Invoke();
                    state.CallbacksCalled = true;
                }

                if (removeCategory)
                {
                    _categoriesToRemove.Add(category);
                }
            }
        }

        foreach (string category in _categoriesToRemove)
        {
            _states.Remove(category);
        }

        return result;
    }

    public void Play(AnimationRequest request)
    {
        _requestsQueue.Enqueue(request);
    }

    public void Stop(string category)
    {
        _states.Remove(category);
    }

    public void StopAll()
    {
        _states.Clear();
    }

    public bool AnyActiveAnimations() => _states.Count > 0;

    public void SetSpeedModifier(AnimationSpeedModifierDelegate modifier)
    {
        _speedModifierDuration = TimeSpan.Zero;
        _speedModifierDelegate = modifier;
    }

    public void StopSpeedModifier()
    {
        _speedModifierDelegate = null;
    }

    public bool IsSpeedModifierActive() => _speedModifierDelegate != null;

    private enum AnimatorWeightState
    {
        EaseIn,
        Stay,
        EaseOut,
        Finished
    }

    private sealed class AnimatorState
    {
        public AnimatorState(AnimationRequest request, Animator animator)
        {
            Request = request;
            Animator = animator;
        }

        public Animator Animator { get; }
        public AnimationRequest Request { get; set; }
        public float PreviousWeight { get; set; }
        public float CurrentWeight { get; set; }
        public AnimatorWeightState WeightState { get; set; } = AnimatorWeightState.EaseIn;
        public TimeSpan CurrentTime { get; set; }
        public bool CallbacksCalled { get; set; }
    }

    private readonly Dictionary<string, AnimatorState> _states = new();
    private readonly List<(PlayerItemFrame frame, float weight)> _frameScratch = new();
    private readonly List<string> _callbackScratch = new();
    private readonly List<string> _categoriesToRemove = new();
    private readonly Queue<AnimationRequest> _requestsQueue = new();
    private TimeSpan _speedModifierDuration = TimeSpan.Zero;
    private AnimationSpeedModifierDelegate? _speedModifierDelegate;

    private void ProcessWeight(AnimatorState state)
    {
        switch (state.WeightState)
        {
            case AnimatorWeightState.EaseIn:
                float progress = Math.Clamp((float)(state.CurrentTime / state.Request.EaseInDuration), 0, 1);
                state.CurrentWeight = state.PreviousWeight + (state.Request.Weight - state.PreviousWeight) * progress;
                if (progress >= 1)
                {
                    state.CurrentWeight = state.Request.Weight;
                    state.WeightState = AnimatorWeightState.Stay;
                }
                break;
            case AnimatorWeightState.Stay:
                if (state.Request.EaseOut && state.Animator.Finished())
                {
                    state.WeightState = AnimatorWeightState.EaseOut;
                }
                break;
            case AnimatorWeightState.EaseOut:
                float progress2 = Math.Clamp((float)((state.CurrentTime - state.Request.Animation.TotalDuration / state.Request.AnimationSpeed) / state.Request.EaseOutDuration), 0, 1);
                state.CurrentWeight = state.Request.Weight * (1f - progress2);
                if (progress2 >= 1)
                {
                    state.CurrentWeight = 0;
                    state.WeightState = AnimatorWeightState.Finished;
                }
                break;
        }
    }
    private void ProcessRequest(AnimationRequest request)
    {
        if (_states.TryGetValue(request.Category, out AnimatorState? state))
        {
            state.Animator.Play(request.Animation, request.AnimationSpeed);
            state.Request = request;
            state.PreviousWeight = state.CurrentWeight;
            state.WeightState = AnimatorWeightState.EaseIn;
            state.CurrentTime = TimeSpan.Zero;
            state.CallbacksCalled = false;
        }
        else
        {
            Animator animator = new(request.Animation, request.AnimationSpeed);
            _states.Add(request.Category, new(request, animator));
        }
    }
}

public readonly struct AnimationRequest
{
    public readonly Animation Animation;
    public readonly float AnimationSpeed;
    public readonly float Weight;
    public readonly string Category;
    public readonly TimeSpan EaseOutDuration;
    public readonly TimeSpan EaseInDuration;
    public readonly bool EaseOut;
    public readonly Action<string>? CallbackHandler;
    public readonly System.Func<bool>? FinishCallback;

    public AnimationRequest(Animation animation, float animationSpeed, float weight, string category, TimeSpan easeOutDuration, TimeSpan easeInDuration, bool easeOut, System.Func<bool>? finishCallback = null, Action<string>? callbackHandler = null)
    {
        Animation = animation;
        AnimationSpeed = animationSpeed;
        Weight = weight;
        Category = category;
        EaseOutDuration = easeOutDuration;
        EaseInDuration = easeInDuration;
        EaseOut = easeOut;
        FinishCallback = finishCallback;
        CallbackHandler = callbackHandler;
    }

    public AnimationRequest(Animation animation, AnimationRequestByCode request)
    {
        Animation = animation;
        AnimationSpeed = request.AnimationSpeed;
        Weight = request.Weight;
        Category = request.Category;
        EaseOutDuration = request.EaseOutDuration;
        EaseInDuration = request.EaseInDuration;
        EaseOut = request.EaseOut;
        FinishCallback = request.FinishCallback;
        CallbackHandler = request.CallbackHandler;
    }

    public AnimationRequest(System.Func<bool> callback, AnimationRequest request)
    {
        Animation = request.Animation;
        AnimationSpeed = request.AnimationSpeed;
        Weight = request.Weight;
        Category = request.Category;
        EaseOutDuration = request.EaseOutDuration;
        EaseInDuration = request.EaseInDuration;
        EaseOut = request.EaseOut;
        FinishCallback = callback;
        CallbackHandler = request.CallbackHandler;
    }

    public AnimationRequest()
    {
        Animation = Animation.Zero;
        Category = "";
    }
}

public readonly struct AnimationRequestByCode
{
    public readonly string Animation;
    public readonly float AnimationSpeed;
    public readonly float Weight;
    public readonly string Category;
    public readonly TimeSpan EaseOutDuration;
    public readonly TimeSpan EaseInDuration;
    public readonly bool EaseOut;
    public readonly Action<string>? CallbackHandler;
    public readonly System.Func<bool>? FinishCallback;

    public AnimationRequestByCode(string animation, float animationSpeed, float weight, string category, TimeSpan easeOutDuration, TimeSpan easeInDuration, bool easeOut, System.Func<bool>? finishCallback = null, Action<string>? callbackHandler = null)
    {
        Animation = animation;
        AnimationSpeed = animationSpeed;
        Weight = weight;
        Category = category;
        EaseOutDuration = easeOutDuration;
        EaseInDuration = easeInDuration;
        EaseOut = easeOut;
        FinishCallback = finishCallback;
        CallbackHandler = callbackHandler;
    }

    public AnimationRequestByCode(AnimationRequestByCode request, float animationSpeed, System.Func<bool>? finishCallback = null, Action<string>? callbackHandler = null)
    {
        Animation = request.Animation;
        AnimationSpeed = animationSpeed;
        Weight = request.Weight;
        Category = request.Category;
        EaseOutDuration = request.EaseOutDuration;
        EaseInDuration = request.EaseInDuration;
        EaseOut = request.EaseOut;
        FinishCallback = finishCallback;
        CallbackHandler = callbackHandler;
    }
}
