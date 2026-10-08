// Adapted from Overhaullib-Legacy, copyright (c) 2024 Maltiez, MIT.
// Source revision: 9bb564978967914c2d07fb13a60e5cc1749bb4ee.
// See third-party/overhaul-core/README.md and LICENSE.
using OpenTK.Mathematics;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Zornhau.Native.Animations;

public enum EnumAnimatedElement
{
    Unknown = 0,
    Custom = -1,
    HeldItem = -2,
    DetachedAnchor = 1,
    UpperTorso,
    LowerTorso,
    Neck,
    Head,
    UpperFootR,
    UpperFootL,
    LowerFootR,
    LowerFootL,
    ItemAnchor,
    LowerArmR,
    UpperArmR,
    ItemAnchorL,
    LowerArmL,
    UpperArmL,
}

public readonly struct PlayerItemFrame
{
    public readonly PlayerFrame Player;
    public readonly ItemFrame? Item;
    public readonly bool DetachedAnchor;
    public readonly bool SwitchArms;

    public PlayerItemFrame(PlayerFrame player, ItemFrame? item)
    {
        Player = player;
        Item = item;
        DetachedAnchor = player.DetachedAnchor;
        SwitchArms = player.SwitchArms;
    }

    public static readonly PlayerItemFrame Zero = new(PlayerFrame.Zero, null);
    public static readonly PlayerItemFrame Empty = new(PlayerFrame.Empty, null);

    public void Apply(ElementPose pose, EnumAnimatedElement element, Vector3 eyePosition, float eyeHeight, float cameraPitch = 0, bool applyCameraPitch = false, bool overrideTorso = true)
    {
        switch (element)
        {
            case EnumAnimatedElement.Unknown:
                Item?.Apply(pose);
                break;
            default:
                Player.Apply(pose, element, eyePosition, eyeHeight, cameraPitch, applyCameraPitch, overrideTorso);
                break;
        }
    }



    public static PlayerItemFrame Compose(IEnumerable<(PlayerItemFrame element, float weight)> frames)
    {
        IReadOnlyList<(PlayerItemFrame element, float weight)> materializedFrames = frames as IReadOnlyList<(PlayerItemFrame element, float weight)> ?? frames.ToArray();
        PlayerFrame player = PlayerFrame.Compose(materializedFrames);

        List<(ItemFrame element, float weight)> itemFrames = _itemFrameComposeScratch ??= new();
        itemFrames.Clear();
        foreach ((PlayerItemFrame element, float weight) in materializedFrames)
        {
            if (element.Item != null)
            {
                itemFrames.Add((element.Item.Value, weight));
            }
        }

        ItemFrame item = ItemFrame.Compose(itemFrames);

        return new(player, item);
    }

    [ThreadStatic]
    private static List<(ItemFrame element, float weight)>? _itemFrameComposeScratch;
}

public readonly struct SoundFrame
{
    public readonly string[] Code;
    public readonly float DurationFraction;
    public readonly bool RandomizePitch;
    public readonly float Range;
    public readonly float Volume;
    public readonly bool Synchronize;
    public readonly float Pitch;
    public readonly bool PitchFollowsAnimationSpeed;

    public SoundFrame(string[] code, float durationFraction, bool randomizePitch = false, float range = 32, float volume = 1, bool synchronize = true)
        : this(code, durationFraction, randomizePitch, range, volume, synchronize, 1, false)
    {
    }

    public SoundFrame(string[] code, float durationFraction, bool randomizePitch, float range, float volume, bool synchronize, float pitch, bool pitchFollowsAnimationSpeed)
    {
        Code = code;
        DurationFraction = durationFraction;
        RandomizePitch = randomizePitch;
        Range = range;
        Volume = volume;
        Synchronize = synchronize;
        Pitch = pitch;
        PitchFollowsAnimationSpeed = pitchFollowsAnimationSpeed;
    }

    public float GetPitch(float animationSpeed)
    {
        float pitch = PitchFollowsAnimationSpeed ? Pitch * animationSpeed : Pitch;
        return Math.Max(0.01f, pitch);
    }

}

public readonly struct ParticlesFrame
{
    public readonly string Code;
    public readonly float DurationFraction;
    public readonly Vector3 Position;
    public readonly Vector3 Velocity;
    public readonly float Intensity;

    public ParticlesFrame(string code, float durationFraction, Vector3 position, Vector3 velocity, float intensity)
    {
        Code = code;
        DurationFraction = durationFraction;
        Position = position;
        Velocity = velocity;
        Intensity = intensity;
    }

}

public readonly struct CallbackFrame
{
    public readonly string Code;
    public readonly float DurationFraction;

    public CallbackFrame(string code, float durationFraction)
    {
        Code = code;
        DurationFraction = durationFraction;
    }

}

public readonly struct ItemKeyFrame
{
    public readonly ItemFrame Frame;
    public readonly float DurationFraction;
    public readonly EasingFunctionType EasingFunction;

    public ItemKeyFrame(ItemFrame frame, float durationFraction, EasingFunctionType easeFunction)
    {
        Frame = frame;
        DurationFraction = durationFraction;
        EasingFunction = easeFunction;
    }

    public static readonly ItemKeyFrame Empty = new(ItemFrame.Empty, 0, EasingFunctionType.Linear);

    public ItemFrame Interpolate(ItemFrame frame, float frameProgress)
    {
        float interpolatedProgress = EasingFunctions.Get(EasingFunction).Invoke((float)frameProgress);
        return ItemFrame.Interpolate(frame, Frame, interpolatedProgress);
    }

    public bool Reached(float animationProgress) => animationProgress >= DurationFraction;


    public static List<ItemKeyFrame> FromVanillaAnimation(string code, Shape shape)
    {
        AnimationKeyFrame[] vanillaKeyFrames = GetVanillaKeyFrames(code, shape);
        return FromVanillaAnimation(vanillaKeyFrames);
    }

    public static List<ItemKeyFrame> FromVanillaAnimation(Vintagestory.API.Common.Animation animation)
    {
        return FromVanillaAnimation(animation.KeyFrames);
    }

    private static List<ItemKeyFrame> FromVanillaAnimation(AnimationKeyFrame[] vanillaKeyFrames)
    {
        if (vanillaKeyFrames == null || vanillaKeyFrames.Length == 0)
        {
            return new();
        }

        vanillaKeyFrames = vanillaKeyFrames.OrderBy(keyFrame => keyFrame.Frame).ToArray();

        HashSet<string> elements = new();
        foreach (AnimationKeyFrame keyFrame in vanillaKeyFrames)
        {
            if (keyFrame.Elements == null) continue;
            foreach ((string element, _) in keyFrame.Elements)
            {
                elements.Add(element);
            }
        }

        Dictionary<string, List<AnimationElement>> frames = new();
        foreach (string element in elements)
        {
            frames.Add(element, GetElementFrames(element, vanillaKeyFrames));
        }

        List<ItemKeyFrame> result = new();
        int lastFrame = vanillaKeyFrames[^1].Frame == 0 ? 1 : vanillaKeyFrames[^1].Frame;
        if (vanillaKeyFrames[0].Frame != 0)
        {
            result.Add(new ItemKeyFrame(new ItemFrame(frames.ToDictionary(entry => entry.Key, entry => entry.Value[0])), 0, EasingFunctionType.Linear));
        }

        for (int index = 0; index < vanillaKeyFrames.Length; index++)
        {
            float durationFraction = (float)vanillaKeyFrames[index].Frame / lastFrame;

            result.Add(new ItemKeyFrame(new ItemFrame(frames.ToDictionary(entry => entry.Key, entry => entry.Value[index])), durationFraction, EasingFunctionType.Linear));
        }

        return result;
    }

    private static uint ToCrc32(string value) => GameMath.Crc32(value.ToLowerInvariant()) & int.MaxValue;

    private static AnimationKeyFrame[] GetVanillaKeyFrames(string code, Shape shape)
    {
        Dictionary<uint, Vintagestory.API.Common.Animation> animations = shape.AnimationsByCrc32;
        uint crc32 = ToCrc32(code);
        if (!animations.TryGetValue(crc32, out Vintagestory.API.Common.Animation? value))
        {
            throw new InvalidOperationException($"Animation '{code}' not found");
        }

        return value.KeyFrames;
    }

    internal static List<AnimationElement> GetElementFrames(string elementName, AnimationKeyFrame[] vanillaFrames)
    {
        IEnumerable<AnimationKeyFrameElement> vanillaElementFrames = vanillaFrames
            .Where(element => element.Elements != null && element.Elements.ContainsKey(elementName))
            .Select(element => element.Elements[elementName]);

        AnimationElement firstFrame = AnimationElement.FromVanilla(vanillaElementFrames.First());
        AnimationElement lastFrame = AnimationElement.FromVanilla(vanillaElementFrames.Last());

        List<AnimationElement> result = new();
        int previousFrameFrame = 0;
        AnimationElement previousFoundElement = firstFrame;

        int startingIndex = 0;
        if (vanillaFrames[0].Frame == 0)
        {
            startingIndex = 1;
        }
        result.Add(firstFrame);

        bool reachedLast = false;

        for (int index = startingIndex; index < vanillaFrames.Length; index++)
        {
            if (reachedLast)
            {
                result.Add(lastFrame);
                continue;
            }

            if (vanillaFrames[index].Elements != null && vanillaFrames[index].Elements.ContainsKey(elementName))
            {
                AnimationElement frame = AnimationElement.FromVanilla(vanillaFrames[index].Elements[elementName]);
                result.Add(frame);
                previousFoundElement = frame;
                previousFrameFrame = vanillaFrames[index].Frame;
            }
            else
            {
                int nextFrameFrame = 0;
                AnimationElement nextFrameElement = previousFoundElement;
                bool found = false;

                for (int nextIndex = index + 1; nextIndex < vanillaFrames.Length; nextIndex++)
                {
                    if (vanillaFrames[nextIndex].Elements != null && vanillaFrames[nextIndex].Elements.ContainsKey(elementName))
                    {
                        nextFrameFrame = vanillaFrames[nextIndex].Frame;
                        nextFrameElement = AnimationElement.FromVanilla(vanillaFrames[nextIndex].Elements[elementName]);
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    reachedLast = true;
                    result.Add(lastFrame);
                    continue;
                }

                int currentFrameFrame = vanillaFrames[index].Frame;
                float interpolationProgress = (currentFrameFrame - previousFrameFrame) / (float)(nextFrameFrame - previousFrameFrame);
                result.Add(AnimationElement.Interpolate(previousFoundElement, nextFrameElement, interpolationProgress));
            }
        }

        return result;
    }
}

public readonly struct ItemFrame
{
    public readonly int ElementsHash = 0;
    private readonly string[] _elementNames = [];
    private readonly int[] _elementNameHashes = [];
    private readonly AnimationElement[] _elements = [];
    private readonly Dictionary<string, int>? _elementIndexes = null;

    public IEnumerable<KeyValuePair<string, AnimationElement>> Elements => EnumerateElements();
    public int Count => _elements.Length;

    public ItemFrame(Dictionary<string, AnimationElement> elements)
    {
        _elementNames = new string[elements.Count];
        _elementNameHashes = new int[elements.Count];
        _elements = new AnimationElement[elements.Count];
        _elementIndexes = new(elements.Count, StringComparer.Ordinal);

        int index = 0;
        foreach ((string code, AnimationElement value) in elements)
        {
            int hash = code.GetHashCode();
            _elementNames[index] = code;
            _elementNameHashes[index] = hash;
            _elements[index] = value;
            _elementIndexes.Add(code, index);
            ElementsHash = index == 0 ? hash : HashCode.Combine(ElementsHash, hash);
            index++;
        }
    }

    private ItemFrame(string[] elementNames, int[] elementNameHashes, AnimationElement[] elements, int elementsHash)
    {
        _elementNames = elementNames;
        _elementNameHashes = elementNameHashes;
        _elements = elements;
        _elementIndexes = null;
        ElementsHash = elementsHash;
    }

    public static readonly ItemFrame Empty = new(new Dictionary<string, AnimationElement>());

    public void Apply(ElementPose pose)
    {
        if (pose.ForElement?.Name is { } name && TryGetElement(name, out AnimationElement element))
        {
            element.Apply(pose);
        }
    }

    public static ItemFrame Interpolate(ItemFrame from, ItemFrame to, float progress)
    {
        if (from.Count == 0) return to;
        if (to.Count == 0) return Empty;

        AnimationElement[] elements = new AnimationElement[to._elements.Length];
        for (int index = 0; index < to._elements.Length; index++)
        {
            if (from.TryGetElement(to._elementNames[index], out AnimationElement fromElement))
            {
                elements[index] = AnimationElement.Interpolate(fromElement, to._elements[index], progress);
            }
            else
            {
                elements[index] = to._elements[index];
            }
        }

        return new(to._elementNames, to._elementNameHashes, elements, to.ElementsHash);
    }
    public static ItemFrame Compose(IEnumerable<(ItemFrame element, float weight)> frames)
    {
        if (frames is not IReadOnlyList<(ItemFrame element, float weight)> materializedFrames)
        {
            materializedFrames = frames.ToArray();
        }

        if (materializedFrames.Count == 0) return Empty;

        List<string> names = new();
        List<int> hashes = new();
        foreach ((ItemFrame frame, _) in materializedFrames)
        {
            for (int elementIndex = 0; elementIndex < frame._elementNames.Length; elementIndex++)
            {
                string name = frame._elementNames[elementIndex];
                if (ContainsName(names, name)) continue;

                names.Add(name);
                hashes.Add(frame._elementNameHashes[elementIndex]);
            }
        }

        if (names.Count == 0) return Empty;

        AnimationElement[] elements = new AnimationElement[names.Count];
        List<(AnimationElement element, float weight)> weightedElements = new(materializedFrames.Count);
        for (int nameIndex = 0; nameIndex < names.Count; nameIndex++)
        {
            weightedElements.Clear();
            string name = names[nameIndex];
            foreach ((ItemFrame frame, float weight) in materializedFrames)
            {
                if (frame.TryGetElement(name, out AnimationElement element))
                {
                    weightedElements.Add((element, weight));
                }
            }

            elements[nameIndex] = AnimationElement.Compose(weightedElements);
        }

        string[] elementNames = names.ToArray();
        int[] elementHashes = hashes.ToArray();
        int elementsHash = 0;
        for (int index = 0; index < elementHashes.Length; index++)
        {
            elementsHash = index == 0 ? elementHashes[index] : HashCode.Combine(elementsHash, elementHashes[index]);
        }

        return new(elementNames, elementHashes, elements, elementsHash);
    }

    private bool TryGetElement(string name, out AnimationElement element)
    {
        if (_elementIndexes != null && _elementIndexes.TryGetValue(name, out int mappedIndex))
        {
            element = _elements[mappedIndex];
            return true;
        }

        for (int index = 0; index < _elementNames.Length; index++)
        {
            if (!string.Equals(_elementNames[index], name, StringComparison.Ordinal)) continue;

            element = _elements[index];
            return true;
        }

        element = default;
        return false;
    }

    private bool TryGetElement(int nameHash, out AnimationElement element)
    {
        for (int index = 0; index < _elementNameHashes.Length; index++)
        {
            if (_elementNameHashes[index] != nameHash) continue;

            element = _elements[index];
            return true;
        }

        element = default;
        return false;
    }

    private IEnumerable<KeyValuePair<string, AnimationElement>> EnumerateElements()
    {
        for (int index = 0; index < _elementNames.Length; index++)
        {
            yield return new(_elementNames[index], _elements[index]);
        }
    }

    private static bool ContainsName(List<string> names, string name)
    {
        foreach (string existingName in names)
        {
            if (string.Equals(existingName, name, StringComparison.Ordinal)) return true;
        }

        return false;
    }

}

public readonly struct PLayerKeyFrame
{
    public readonly PlayerFrame Frame;
    public readonly TimeSpan Time;
    public readonly EasingFunctionType EasingFunction;
    public readonly EasingFunctionType EasingType;
    public readonly Vector2 FrameProgressRange;

    public PLayerKeyFrame(PlayerFrame frame, TimeSpan easingTime, EasingFunctionType easeFunction, EasingFunctionType easingType, Vector2 frameProgressRange)
    {
        Frame = frame;
        Time = easingTime;
        EasingFunction = easeFunction;
        EasingType = easingType;
        FrameProgressRange = frameProgressRange;
    }

    public PLayerKeyFrame(PlayerFrame frame, TimeSpan easingTime, EasingFunctionType easeFunction)
    {
        Frame = frame;
        Time = easingTime;
        EasingFunction = easeFunction;
        EasingType = easeFunction;
        FrameProgressRange = new(0, 1);
    }

    public static readonly PLayerKeyFrame Zero = new(PlayerFrame.Zero, TimeSpan.Zero, EasingFunctionType.Linear);

    private static readonly string[] PlayerElementNames =
    [
        "DetachedAnchor",
        "UpperTorso",
        "LowerTorso",
        "Neck",
        "Head",
        "UpperFootR",
        "UpperFootL",
        "LowerFootR",
        "LowerFootL",
        "ItemAnchor",
        "LowerArmR",
        "UpperArmR",
        "ItemAnchorL",
        "LowerArmL",
        "UpperArmL"
    ];

    public PlayerFrame Interpolate(PlayerFrame frame, float frameProgress)
    {
        float interpolatedProgress = GetInterpolatedProgress(frameProgress);


        return PlayerFrame.Interpolate(frame, Frame, interpolatedProgress);
    }

    public bool Reached(TimeSpan currentDuration) => currentDuration >= Time;

    public static List<PLayerKeyFrame> FromVanillaAnimation(Vintagestory.API.Common.Animation animation, out bool hasPlayerFrames)
    {
        hasPlayerFrames = false;
        if (animation.KeyFrames == null || animation.KeyFrames.Length == 0)
        {
            return [Zero];
        }

        AnimationKeyFrame[] vanillaKeyFrames = animation.KeyFrames.OrderBy(keyFrame => keyFrame.Frame).ToArray();
        HashSet<string> playerElements = new(StringComparer.OrdinalIgnoreCase);
        foreach (AnimationKeyFrame keyFrame in vanillaKeyFrames)
        {
            if (keyFrame.Elements == null) continue;

            foreach (string elementName in keyFrame.Elements.Keys)
            {
                string? canonicalName = PlayerElementNames.FirstOrDefault(name => string.Equals(name, elementName, StringComparison.OrdinalIgnoreCase));
                if (canonicalName != null)
                {
                    playerElements.Add(canonicalName);
                }
            }
        }

        TimeSpan duration = FromVanillaFrameNumber(vanillaKeyFrames[^1].Frame);
        if (playerElements.Count == 0)
        {
            return CreateDurationAnchorFrames(duration);
        }

        hasPlayerFrames = true;
        Dictionary<string, List<AnimationElement>> frames = playerElements.ToDictionary(
            element => element,
            element => ItemKeyFrame.GetElementFrames(element, vanillaKeyFrames));

        List<PLayerKeyFrame> result = new();
        if (vanillaKeyFrames[0].Frame != 0)
        {
            result.Add(new(BuildPlayerFrame(frames, 0), TimeSpan.Zero, EasingFunctionType.Linear));
        }

        for (int index = 0; index < vanillaKeyFrames.Length; index++)
        {
            result.Add(new(BuildPlayerFrame(frames, index), FromVanillaFrameNumber(vanillaKeyFrames[index].Frame), EasingFunctionType.Linear));
        }

        return result;
    }

    private static List<PLayerKeyFrame> CreateDurationAnchorFrames(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return [Zero];
        }

        return
        [
            Zero,
            new(PlayerFrame.Zero, duration, EasingFunctionType.Linear)
        ];
    }

    private static TimeSpan FromVanillaFrameNumber(int frame)
    {
        return TimeSpan.FromMilliseconds(Math.Max(0, frame) * 1000.0 / 30.0);
    }

    private static PlayerFrame BuildPlayerFrame(Dictionary<string, List<AnimationElement>> frames, int index)
    {
        bool hasRightHand = frames.ContainsKey("ItemAnchor") || frames.ContainsKey("LowerArmR") || frames.ContainsKey("UpperArmR");
        RightHandFrame? rightHand = hasRightHand
            ? new(
                GetFrameElement(frames, "ItemAnchor", index) ?? AnimationElement.Zero,
                GetFrameElement(frames, "LowerArmR", index) ?? AnimationElement.Zero,
                GetFrameElement(frames, "UpperArmR", index) ?? AnimationElement.Zero)
            : null;

        bool hasLeftHand = frames.ContainsKey("ItemAnchorL") || frames.ContainsKey("LowerArmL") || frames.ContainsKey("UpperArmL");
        LeftHandFrame? leftHand = hasLeftHand
            ? new(
                GetFrameElement(frames, "ItemAnchorL", index) ?? AnimationElement.Zero,
                GetFrameElement(frames, "LowerArmL", index) ?? AnimationElement.Zero,
                GetFrameElement(frames, "UpperArmL", index) ?? AnimationElement.Zero)
            : null;

        bool hasOtherParts = frames.ContainsKey("Neck") || frames.ContainsKey("Head") ||
            frames.ContainsKey("UpperFootR") || frames.ContainsKey("UpperFootL") ||
            frames.ContainsKey("LowerFootR") || frames.ContainsKey("LowerFootL");
        OtherPartsFrame? otherParts = hasOtherParts
            ? new(
                GetFrameElement(frames, "Neck", index) ?? AnimationElement.Zero,
                GetFrameElement(frames, "Head", index) ?? AnimationElement.Zero,
                GetFrameElement(frames, "UpperFootR", index) ?? AnimationElement.Zero,
                GetFrameElement(frames, "UpperFootL", index) ?? AnimationElement.Zero,
                GetFrameElement(frames, "LowerFootR", index) ?? AnimationElement.Zero,
                GetFrameElement(frames, "LowerFootL", index) ?? AnimationElement.Zero)
            : null;

        return new(
            rightHand,
            leftHand,
            otherParts,
            GetFrameElement(frames, "UpperTorso", index),
            GetFrameElement(frames, "DetachedAnchor", index),
            lowerTorso: GetFrameElement(frames, "LowerTorso", index));
    }

    private static AnimationElement? GetFrameElement(Dictionary<string, List<AnimationElement>> frames, string elementName, int index)
    {
        if (!frames.TryGetValue(elementName, out List<AnimationElement>? values) || index < 0 || index >= values.Count)
        {
            return null;
        }

        return values[index];
    }

    private float GetInterpolatedProgress(float frameProgress)
    {
        EasingFunctions.EasingFunctionDelegate easingFunction = EasingFunctions.Get(EasingFunction);
        float currentProgress = easingFunction.Invoke(GetAdjustedFrameProgress(frameProgress));
        float startProgress = easingFunction.Invoke(FrameProgressRange.X);
        float endProgress = easingFunction.Invoke(FrameProgressRange.Y);

        return (currentProgress - startProgress) / (endProgress - startProgress);
    }

    private float GetAdjustedFrameProgress(float frameProgress)
    {
        return FrameProgressRange.X + frameProgress * (FrameProgressRange.Y - FrameProgressRange.X);
    }

}

public readonly struct PlayerFrame
{
    public readonly RightHandFrame? RightHand;
    public readonly LeftHandFrame? LeftHand;
    public readonly OtherPartsFrame? OtherParts = null;
    public readonly AnimationElement? UpperTorso = null;
    public readonly AnimationElement? DetachedAnchorFrame = null;
    public readonly AnimationElement? LowerTorso = null;
    public readonly bool DetachedAnchor = false;
    public readonly bool SwitchArms = false;
    public readonly float PitchFollow = DefaultPitchFollow;
    public readonly float FovMultiplier = 1;
    public readonly float BobbingAmplitude = 1;
    public readonly float DetachedAnchorFollow = 1;

    public const float DefaultPitchFollow = 0.8f;
    public const float PerfectPitchFollow = 1.0f;
    public const float Epsilon = 1E-6f;
    public const float EyeHeightToAnimationDistanceMultiplier = 16.1f;
    public const float PitchAngleMin = -45;
    public const float PitchAngleMax = 75;

    public PlayerFrame(
        RightHandFrame? rightHand = null,
        LeftHandFrame? leftHand = null,
        OtherPartsFrame? otherParts = null,
        AnimationElement? upperTorso = null,
        AnimationElement? detachedAnchorFrame = null,
        bool detachedAnchor = false,
        bool switchArms = false,
        float pitchFollow = DefaultPitchFollow,
        float fovMultiplier = 1.0f,
        float bobbingAmplitude = 1.0f,
        float? detachedAnchorFollow = null,
        AnimationElement? lowerTorso = null)
    {
        RightHand = rightHand;
        LeftHand = leftHand;
        OtherParts = otherParts;
        UpperTorso = upperTorso;
        DetachedAnchorFrame = detachedAnchorFrame;
        DetachedAnchor = detachedAnchor;
        SwitchArms = switchArms;
        PitchFollow = pitchFollow;
        FovMultiplier = fovMultiplier;
        BobbingAmplitude = bobbingAmplitude;
        LowerTorso = lowerTorso;
        DetachedAnchorFollow = detachedAnchorFollow ?? (detachedAnchor ? 0 : 1);
    }

    public static readonly PlayerFrame Zero = new(RightHandFrame.Zero, LeftHandFrame.Zero, OtherPartsFrame.Zero);
    public static readonly PlayerFrame Empty = new(null, null, null);

    public void Apply(ElementPose pose, EnumAnimatedElement element, Vector3 eyePosition, float eyeHeight, float cameraPitch, bool applyCameraPitch, bool overrideTorso)
    {
        switch (element)
        {
            case EnumAnimatedElement.DetachedAnchor:
                DetachedAnchorFrame?.Apply(pose);
                break;
            case EnumAnimatedElement.UpperTorso:
                UpperTorso?.Apply(pose);
                if (applyCameraPitch)
                {
                    pose.degZ += GameMath.Clamp(cameraPitch * GameMath.RAD2DEG * PitchFollow * DetachedAnchorFollow, PitchAngleMin, PitchAngleMax);
                }
                break;
            case EnumAnimatedElement.Neck:
                OtherParts?.Apply(pose, element);
                if (applyCameraPitch)
                {
                    pose.degZ = -GameMath.Clamp(cameraPitch * GameMath.RAD2DEG * PitchFollow * DetachedAnchorFollow, PitchAngleMin, PitchAngleMax) / 2;
                }
                break;
            case EnumAnimatedElement.LowerTorso:
                if (overrideTorso)
                {
                    if (LowerTorso != null)
                    {
                        AnimationElement torso = new(LowerTorso.Value.OffsetX, (eyePosition.Y - eyeHeight) * EyeHeightToAnimationDistanceMultiplier, LowerTorso.Value.OffsetZ, LowerTorso.Value.RotationX, LowerTorso.Value.RotationY, LowerTorso.Value.RotationZ);
                        torso.Apply(pose);
                    }
                    else
                    {
                        AnimationElement torso = new(0, (eyePosition.Y - eyeHeight) * EyeHeightToAnimationDistanceMultiplier, 0, 0, 0, 0);
                        torso.Apply(pose);
                    }
                }
                else
                {
                    LowerTorso?.Apply(pose);
                    pose.translateY = (eyePosition.Y - eyeHeight) * EyeHeightToAnimationDistanceMultiplier / 16;
                }
                break;
            default:
                OtherParts?.Apply(pose, element);
                RightHand?.Apply(pose, element);
                LeftHand?.Apply(pose, element);
                break;
        }
    }


    public static PlayerFrame Interpolate(PlayerFrame from, PlayerFrame to, float progress)
    {
        RightHandFrame? righthand = null;
        if (from.RightHand == null && to.RightHand != null)
        {
            righthand = null;//to.RightHand;
        }
        else if (from.RightHand != null && to.RightHand == null)
        {
            righthand = null;//from.RightHand;
        }
        else if (from.RightHand != null && to.RightHand != null)
        {
            righthand = RightHandFrame.Interpolate(from.RightHand.Value, to.RightHand.Value, progress);
        }

        LeftHandFrame? leftHand = null;
        if (from.LeftHand == null && to.LeftHand != null)
        {
            leftHand = null;//to.LeftHand;
        }
        else if (from.LeftHand != null && to.LeftHand == null)
        {
            leftHand = null;//from.LeftHand;
        }
        else if (from.LeftHand != null && to.LeftHand != null)
        {
            leftHand = LeftHandFrame.Interpolate(from.LeftHand.Value, to.LeftHand.Value, progress);
        }

        OtherPartsFrame? otherParts = null;
        if (from.OtherParts == null && to.OtherParts != null)
        {
            otherParts = null;//to.LeftHand;
        }
        else if (from.OtherParts != null && to.OtherParts == null)
        {
            otherParts = null;//from.LeftHand;
        }
        else if (from.OtherParts != null && to.OtherParts != null)
        {
            otherParts = OtherPartsFrame.Interpolate(from.OtherParts.Value, to.OtherParts.Value, progress);
        }

        AnimationElement? anchor = AnimationElement.Interpolate(from.DetachedAnchorFrame ?? AnimationElement.Zero, to.DetachedAnchorFrame ?? AnimationElement.Zero, progress);
        AnimationElement? torso = AnimationElement.Interpolate(from.UpperTorso ?? AnimationElement.Zero, to.UpperTorso ?? AnimationElement.Zero, progress);
        AnimationElement? lowerTorso = AnimationElement.Interpolate(from.LowerTorso ?? AnimationElement.Zero, to.LowerTorso ?? AnimationElement.Zero, progress);

        if (from.DetachedAnchorFrame == null && to.DetachedAnchorFrame == null) anchor = null;
        if (from.UpperTorso == null && to.UpperTorso == null) torso = null;
        if (from.LowerTorso == null && to.LowerTorso == null) lowerTorso = null;

        float pitchFollow = from.PitchFollow + (to.PitchFollow - from.PitchFollow) * progress;
        float fov = from.FovMultiplier + (to.FovMultiplier - from.FovMultiplier) * progress;
        float bobbing = from.BobbingAmplitude + (to.BobbingAmplitude - from.BobbingAmplitude) * progress;
        float detachedAnchorFollow = from.DetachedAnchorFollow + (to.DetachedAnchorFollow - from.DetachedAnchorFollow) * progress;

        return new(righthand, leftHand, otherParts, torso, anchor, to.DetachedAnchor, to.SwitchArms, pitchFollow, fov, bobbing, detachedAnchorFollow, lowerTorso);
    }
    public static PlayerFrame Compose(IEnumerable<(PlayerFrame element, float weight)> frames)
    {
        PlayerFrameComposition composition = new();
        foreach ((PlayerFrame element, float weight) in frames)
        {
            composition.Add(element, weight);
        }

        return composition.ToFrame();
    }

    public static PlayerFrame Compose(IReadOnlyList<(PlayerItemFrame element, float weight)> frames)
    {
        PlayerFrameComposition composition = new();
        foreach ((PlayerItemFrame element, float weight) in frames)
        {
            composition.Add(element.Player, weight);
        }

        return composition.ToFrame();
    }

    private struct PlayerFrameComposition
    {
        private bool _hasAny;
        private bool _hasRightHand;
        private bool _hasLeftHand;
        private bool _hasOtherParts;
        private bool _hasUpperTorso;
        private bool _hasDetachedAnchorFrame;
        private bool _hasLowerTorso;
        private bool _detachedAnchor;
        private bool _switchArms;
        private bool _pitchFollowSet;
        private float _pitchFollow;
        private float _fovMultiplier;
        private float _bobbingAmplitude;
        private float _detachedAnchorFollow;

        private AnimationElement.Accumulator _rightItemAnchor;
        private AnimationElement.Accumulator _rightLowerArm;
        private AnimationElement.Accumulator _rightUpperArm;
        private AnimationElement.Accumulator _leftItemAnchor;
        private AnimationElement.Accumulator _leftLowerArm;
        private AnimationElement.Accumulator _leftUpperArm;
        private AnimationElement.Accumulator _neck;
        private AnimationElement.Accumulator _head;
        private AnimationElement.Accumulator _upperFootR;
        private AnimationElement.Accumulator _upperFootL;
        private AnimationElement.Accumulator _lowerFootR;
        private AnimationElement.Accumulator _lowerFootL;
        private AnimationElement.Accumulator _upperTorso;
        private AnimationElement.Accumulator _detachedAnchorFrame;
        private AnimationElement.Accumulator _lowerTorso;

        public void Add(PlayerFrame frame, float weight)
        {
            if (!_hasAny)
            {
                _fovMultiplier = frame.FovMultiplier;
                _bobbingAmplitude = frame.BobbingAmplitude;
                _detachedAnchorFollow = frame.DetachedAnchorFollow;
                _hasAny = true;
            }
            else
            {
                _fovMultiplier = Math.Min(_fovMultiplier, frame.FovMultiplier);
                _bobbingAmplitude = Math.Min(_bobbingAmplitude, frame.BobbingAmplitude);
                _detachedAnchorFollow = Math.Min(_detachedAnchorFollow, frame.DetachedAnchorFollow);
            }

            _detachedAnchor |= frame.DetachedAnchor;
            _switchArms |= frame.SwitchArms;
            if (!_pitchFollowSet && Math.Abs(frame.PitchFollow - DefaultPitchFollow) > 1E-6f)
            {
                _pitchFollow = frame.PitchFollow;
                _pitchFollowSet = true;
            }

            if (frame.RightHand is RightHandFrame rightHand)
            {
                _hasRightHand = true;
                _rightItemAnchor.Add(rightHand.ItemAnchor, weight);
                _rightLowerArm.Add(rightHand.LowerArmR, weight);
                _rightUpperArm.Add(rightHand.UpperArmR, weight);
            }

            if (frame.LeftHand is LeftHandFrame leftHand)
            {
                _hasLeftHand = true;
                _leftItemAnchor.Add(leftHand.ItemAnchorL, weight);
                _leftLowerArm.Add(leftHand.LowerArmL, weight);
                _leftUpperArm.Add(leftHand.UpperArmL, weight);
            }

            if (frame.OtherParts is OtherPartsFrame otherParts)
            {
                _hasOtherParts = true;
                _neck.Add(otherParts.Neck, weight);
                _head.Add(otherParts.Head, weight);
                _upperFootR.Add(otherParts.UpperFootR, weight);
                _upperFootL.Add(otherParts.UpperFootL, weight);
                _lowerFootR.Add(otherParts.LowerFootR, weight);
                _lowerFootL.Add(otherParts.LowerFootL, weight);
            }

            if (frame.UpperTorso is AnimationElement upperTorso)
            {
                _hasUpperTorso = true;
                _upperTorso.Add(upperTorso, weight);
            }

            if (frame.DetachedAnchorFrame is AnimationElement detachedAnchorFrame)
            {
                _hasDetachedAnchorFrame = true;
                _detachedAnchorFrame.Add(detachedAnchorFrame, weight);
            }

            if (frame.LowerTorso is AnimationElement lowerTorso)
            {
                _hasLowerTorso = true;
                _lowerTorso.Add(lowerTorso, weight);
            }
        }

        public PlayerFrame ToFrame()
        {
            if (!_hasAny) return Empty;

            RightHandFrame? rightHand = _hasRightHand
                ? new(_rightItemAnchor.ToElement(), _rightLowerArm.ToElement(), _rightUpperArm.ToElement())
                : null;
            LeftHandFrame? leftHand = _hasLeftHand
                ? new(_leftItemAnchor.ToElement(), _leftLowerArm.ToElement(), _leftUpperArm.ToElement())
                : null;
            OtherPartsFrame? otherParts = _hasOtherParts
                ? new(_neck.ToElement(), _head.ToElement(), _upperFootR.ToElement(), _upperFootL.ToElement(), _lowerFootR.ToElement(), _lowerFootL.ToElement())
                : null;

            return new(
                rightHand,
                leftHand,
                otherParts,
                _hasUpperTorso ? _upperTorso.ToElement() : null,
                _hasDetachedAnchorFrame ? _detachedAnchorFrame.ToElement() : null,
                _detachedAnchor,
                _switchArms,
                _pitchFollowSet ? _pitchFollow : DefaultPitchFollow,
                _fovMultiplier,
                _bobbingAmplitude,
                _detachedAnchorFollow,
                _hasLowerTorso ? _lowerTorso.ToElement() : null
            );
        }
    }
}

public readonly struct RightHandFrame
{
    public readonly AnimationElement ItemAnchor;
    public readonly AnimationElement LowerArmR;
    public readonly AnimationElement UpperArmR;

    public RightHandFrame(AnimationElement anchor, AnimationElement lower, AnimationElement upper)
    {
        ItemAnchor = anchor;
        LowerArmR = lower;
        UpperArmR = upper;
    }

    public void Apply(ElementPose pose, EnumAnimatedElement element)
    {
        switch (element)
        {
            case EnumAnimatedElement.ItemAnchor:
                ItemAnchor.Apply(pose);
                break;
            case EnumAnimatedElement.LowerArmR:
                LowerArmR.Apply(pose);
                break;
            case EnumAnimatedElement.UpperArmR:
                UpperArmR.Apply(pose);
                break;
        }
    }

    public static readonly RightHandFrame Zero = new(AnimationElement.Zero, AnimationElement.Zero, AnimationElement.Zero);


    public static RightHandFrame Interpolate(RightHandFrame from, RightHandFrame to, float progress)
    {
        return new(
            AnimationElement.Interpolate(from.ItemAnchor, to.ItemAnchor, progress),
            AnimationElement.Interpolate(from.LowerArmR, to.LowerArmR, progress),
            AnimationElement.Interpolate(from.UpperArmR, to.UpperArmR, progress)
            );
    }

    public static RightHandFrame Compose(IEnumerable<(RightHandFrame element, float weight)> frames)
    {
        AnimationElement.Accumulator itemAnchor = new();
        AnimationElement.Accumulator lowerArm = new();
        AnimationElement.Accumulator upperArm = new();
        foreach ((RightHandFrame element, float weight) in frames)
        {
            itemAnchor.Add(element.ItemAnchor, weight);
            lowerArm.Add(element.LowerArmR, weight);
            upperArm.Add(element.UpperArmR, weight);
        }

        return new(
            itemAnchor.ToElement(),
            lowerArm.ToElement(),
            upperArm.ToElement()
            );
    }
}

public readonly struct LeftHandFrame
{
    public readonly AnimationElement ItemAnchorL;
    public readonly AnimationElement LowerArmL;
    public readonly AnimationElement UpperArmL;

    public LeftHandFrame(AnimationElement anchor, AnimationElement lower, AnimationElement upper)
    {
        ItemAnchorL = anchor;
        LowerArmL = lower;
        UpperArmL = upper;
    }

    public void Apply(ElementPose pose, EnumAnimatedElement element)
    {
        switch (element)
        {
            case EnumAnimatedElement.ItemAnchorL:
                ItemAnchorL.Apply(pose);
                break;
            case EnumAnimatedElement.LowerArmL:
                LowerArmL.Apply(pose);
                break;
            case EnumAnimatedElement.UpperArmL:
                UpperArmL.Apply(pose);
                break;
        }
    }

    public static readonly LeftHandFrame Zero = new(AnimationElement.Zero, AnimationElement.Zero, AnimationElement.Zero);


    public static LeftHandFrame Interpolate(LeftHandFrame from, LeftHandFrame to, float progress)
    {
        return new(
            AnimationElement.Interpolate(from.ItemAnchorL, to.ItemAnchorL, progress),
            AnimationElement.Interpolate(from.LowerArmL, to.LowerArmL, progress),
            AnimationElement.Interpolate(from.UpperArmL, to.UpperArmL, progress)
            );
    }

    public static LeftHandFrame Compose(IEnumerable<(LeftHandFrame element, float weight)> frames)
    {
        AnimationElement.Accumulator itemAnchor = new();
        AnimationElement.Accumulator lowerArm = new();
        AnimationElement.Accumulator upperArm = new();
        foreach ((LeftHandFrame element, float weight) in frames)
        {
            itemAnchor.Add(element.ItemAnchorL, weight);
            lowerArm.Add(element.LowerArmL, weight);
            upperArm.Add(element.UpperArmL, weight);
        }

        return new(
            itemAnchor.ToElement(),
            lowerArm.ToElement(),
            upperArm.ToElement()
            );
    }
}

public readonly struct OtherPartsFrame
{
    public readonly AnimationElement Neck;
    public readonly AnimationElement Head;
    public readonly AnimationElement UpperFootR;
    public readonly AnimationElement UpperFootL;
    public readonly AnimationElement LowerFootR;
    public readonly AnimationElement LowerFootL;

    public OtherPartsFrame(
        AnimationElement neck,
        AnimationElement head,
        AnimationElement upperFootR,
        AnimationElement upperFootL,
        AnimationElement lowerFootR,
        AnimationElement lowerFootL)
    {
        Neck = neck;
        Head = head;
        UpperFootR = upperFootR;
        UpperFootL = upperFootL;
        LowerFootR = lowerFootR;
        LowerFootL = lowerFootL;
    }

    public void Apply(ElementPose pose, EnumAnimatedElement element)
    {
        switch (element)
        {
            case EnumAnimatedElement.Neck:
                Neck.Apply(pose);
                break;
            case EnumAnimatedElement.Head:
                Head.Apply(pose);
                break;
            case EnumAnimatedElement.UpperFootR:
                UpperFootR.Apply(pose);
                break;
            case EnumAnimatedElement.UpperFootL:
                UpperFootL.Apply(pose);
                break;
            case EnumAnimatedElement.LowerFootR:
                LowerFootR.Apply(pose);
                break;
            case EnumAnimatedElement.LowerFootL:
                LowerFootL.Apply(pose);
                break;
        }
    }

    public static readonly OtherPartsFrame Zero = new(AnimationElement.Zero, AnimationElement.Zero, AnimationElement.Zero, AnimationElement.Zero, AnimationElement.Zero, AnimationElement.Zero);


    public static OtherPartsFrame Interpolate(OtherPartsFrame from, OtherPartsFrame to, float progress)
    {
        return new(
            AnimationElement.Interpolate(from.Neck, to.Neck, progress),
            AnimationElement.Interpolate(from.Head, to.Head, progress),
            AnimationElement.Interpolate(from.UpperFootR, to.UpperFootR, progress),
            AnimationElement.Interpolate(from.UpperFootL, to.UpperFootL, progress),
            AnimationElement.Interpolate(from.LowerFootR, to.LowerFootR, progress),
            AnimationElement.Interpolate(from.LowerFootL, to.LowerFootL, progress)
            );
    }

    public static OtherPartsFrame Compose(IEnumerable<(OtherPartsFrame element, float weight)> frames)
    {
        AnimationElement.Accumulator neck = new();
        AnimationElement.Accumulator head = new();
        AnimationElement.Accumulator upperFootR = new();
        AnimationElement.Accumulator upperFootL = new();
        AnimationElement.Accumulator lowerFootR = new();
        AnimationElement.Accumulator lowerFootL = new();
        foreach ((OtherPartsFrame element, float weight) in frames)
        {
            neck.Add(element.Neck, weight);
            head.Add(element.Head, weight);
            upperFootR.Add(element.UpperFootR, weight);
            upperFootL.Add(element.UpperFootL, weight);
            lowerFootR.Add(element.LowerFootR, weight);
            lowerFootL.Add(element.LowerFootL, weight);
        }

        return new(
            neck.ToElement(),
            head.ToElement(),
            upperFootR.ToElement(),
            upperFootL.ToElement(),
            lowerFootR.ToElement(),
            lowerFootL.ToElement()
            );
    }
}

public readonly struct AnimationElement
{
    public readonly float? OffsetX;
    public readonly float? OffsetY;
    public readonly float? OffsetZ;
    public readonly float? RotationX;
    public readonly float? RotationY;
    public readonly float? RotationZ;

    public AnimationElement(float?[] values)
    {
        OffsetX = values[0];
        OffsetY = values[1];
        OffsetZ = values[2];
        RotationX = values[3];
        RotationY = values[4];
        RotationZ = values[5];
    }
    public AnimationElement(float? offsetX, float? offsetY, float? offsetZ, float? rotationX, float? rotationY, float? rotationZ)
    {
        OffsetX = offsetX;
        OffsetY = offsetY;
        OffsetZ = offsetZ;
        RotationX = rotationX;
        RotationY = rotationY;
        RotationZ = rotationZ;
    }

    public void Apply(ElementPose pose)
    {
        pose.translateX = OffsetX / 16 ?? 0;
        pose.translateY = OffsetY / 16 ?? 0;
        pose.translateZ = OffsetZ / 16 ?? 0;
        pose.degX = RotationX ?? 0;
        pose.degY = RotationY ?? 0;
        pose.degZ = RotationZ ?? 0;
    }

    public static readonly AnimationElement Zero = new(0, 0, 0, 0, 0, 0);


    public float?[] ToArray() => new float?[]
            {
                OffsetX,
                OffsetY,
                OffsetZ,
                RotationX,
                RotationY,
                RotationZ
            };

    public static AnimationElement Interpolate(AnimationElement from, AnimationElement to, float progress)
    {
        return new(
            from.OffsetX + (to.OffsetX - from.OffsetX) * progress,
            from.OffsetY + (to.OffsetY - from.OffsetY) * progress,
            from.OffsetZ + (to.OffsetZ - from.OffsetZ) * progress,
            from.RotationX + (to.RotationX - from.RotationX) * progress,
            from.RotationY + (to.RotationY - from.RotationY) * progress,
            from.RotationZ + (to.RotationZ - from.RotationZ) * progress
            );
    }
    public static AnimationElement Compose(IEnumerable<(AnimationElement element, float weight)> elements)
    {
        Accumulator accumulator = new();
        foreach ((AnimationElement element, float weight) in elements)
        {
            accumulator.Add(element, weight);
        }

        return accumulator.ToElement();
    }

    internal struct Accumulator
    {
        // Positive-weight contributions are resolved with "max weight wins" (replace); weight <= 0
        // contributions are additive layers applied on top of the replace winner. These two groups are
        // kept in separate fields and combined only in ToElement, so the result does not depend on the
        // order in which Add is called. A single shared field would let a positive-weight frame added
        // after an additive frame overwrite that additive contribution (e.g. the weight-0 "grip" layer
        // being clobbered by the main-hand animation when it composes later in iteration order).
        private float _offsetX;
        private float _offsetY;
        private float _offsetZ;
        private float _rotationX;
        private float _rotationY;
        private float _rotationZ;

        private float _offsetXMaxWeight;
        private float _offsetYMaxWeight;
        private float _offsetZMaxWeight;
        private float _rotationXMaxWeight;
        private float _rotationYMaxWeight;
        private float _rotationZMaxWeight;

        private float _offsetXAdditive;
        private float _offsetYAdditive;
        private float _offsetZAdditive;
        private float _rotationXAdditive;
        private float _rotationYAdditive;
        private float _rotationZAdditive;

        public void Add(AnimationElement element, float weight)
        {
            if (weight > 0)
            {
                if (weight >= _offsetXMaxWeight && element.OffsetX.HasValue) { _offsetXMaxWeight = weight; _offsetX = element.OffsetX.Value; }
                if (weight >= _offsetYMaxWeight && element.OffsetY.HasValue) { _offsetYMaxWeight = weight; _offsetY = element.OffsetY.Value; }
                if (weight >= _offsetZMaxWeight && element.OffsetZ.HasValue) { _offsetZMaxWeight = weight; _offsetZ = element.OffsetZ.Value; }
                if (weight >= _rotationXMaxWeight && element.RotationX.HasValue) { _rotationXMaxWeight = weight; _rotationX = element.RotationX.Value; }
                if (weight >= _rotationYMaxWeight && element.RotationY.HasValue) { _rotationYMaxWeight = weight; _rotationY = element.RotationY.Value; }
                if (weight >= _rotationZMaxWeight && element.RotationZ.HasValue) { _rotationZMaxWeight = weight; _rotationZ = element.RotationZ.Value; }
                return;
            }

            if (element.OffsetX.HasValue) _offsetXAdditive += element.OffsetX.Value;
            if (element.OffsetY.HasValue) _offsetYAdditive += element.OffsetY.Value;
            if (element.OffsetZ.HasValue) _offsetZAdditive += element.OffsetZ.Value;
            if (element.RotationX.HasValue) _rotationXAdditive += element.RotationX.Value;
            if (element.RotationY.HasValue) _rotationYAdditive += element.RotationY.Value;
            if (element.RotationZ.HasValue) _rotationZAdditive += element.RotationZ.Value;
        }

        public AnimationElement ToElement()
        {
            return new(
                _offsetX + _offsetXAdditive,
                _offsetY + _offsetYAdditive,
                _offsetZ + _offsetZAdditive,
                _rotationX + _rotationXAdditive,
                _rotationY + _rotationYAdditive,
                _rotationZ + _rotationZAdditive
            );
        }
    }
    public static AnimationElement FromVanilla(AnimationKeyFrameElement frame)
    {
        return new(
            (float?)frame.OffsetX ?? 0,
            (float?)frame.OffsetY ?? 0,
            (float?)frame.OffsetZ ?? 0,
            (float?)frame.RotationX ?? 0,
            (float?)frame.RotationY ?? 0,
            (float?)frame.RotationZ ?? 0);
    }

}
