using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using WarbandCombat.Native.Animations;
using NativeAnimation = WarbandCombat.Native.Animations.Animation;

namespace WarbandCombat;

// The CO composer and frame application run in our assembly, with our phase
// selection. Native callbacks never start attacks or bypass server windups.
internal sealed class StandaloneAnimations(ICoreAPI api, CombatSystem combat) : ICombatAnimation
{
    private readonly Dictionary<string, NativeAnimation> clips = new();
    private readonly ConditionalWeakTable<EntityPlayer, Playback> players = new();
    private sealed class Playback
    {
        internal Composer Composer = new();
        internal PlayerItemFrame Frame = PlayerItemFrame.Empty;
        internal ItemStack? Item, Offhand;
        internal string? Key;
        internal long At, RemoteAt;
        internal int RemoteRevision = -1;
        internal Fighter Remote = new();
        internal bool Active;
    }

    internal bool Own(CollectibleObject? item) => item != null && CombatSystem.Supported(item)
        && combat.Integration?.UsesPhysicalContacts(item) != true;

    internal void Load()
    {
        if (api is not ICoreClientAPI) return;
        clips.Clear();
        var sources = JsonConvert.DeserializeObject<Dictionary<string, AnimationJson>>(
            api.Assets.Get("warbandcombat:config/reanimated.json").ToText())!;
        foreach (var (code, source) in sources)
        {
            var ready = JsonConvert.DeserializeObject<AnimationJson>(JsonConvert.SerializeObject(source))!;
            ready.Hold = true; ready.CallbackFrames = Array.Empty<CallbackFrameJson>();
            ready.SoundFrames = Array.Empty<SoundFrameJson>();
            clips[code] = ready.ToAnimation();
            foreach (var (name, phase) in NativePhaseAnimations.Split(code, source)) clips[name] = phase.ToAnimation();
        }
        foreach (var (code, source) in JsonConvert.DeserializeObject<Dictionary<string, AnimationJson>>(
            api.Assets.Get("warbandcombat:config/overhaul-fallback.json").ToText())!) clips[code] = source.ToAnimation();
        api.Logger.Notification("[Warband Combat] Embedded CO composer ready: {0} native phase clips; no CO mod required.", clips.Count);
    }

    internal static bool TwoHanded(EntityPlayer player)
        => player.LeftHandItemSlot.Empty && (player.RightHandItemSlot.Itemstack?.Collectible.Tool == EnumTool.Spear
            || player.RightHandItemSlot.Itemstack?.Collectible.Code.Path is { } code
            && (code.Contains("claymore") || code.Contains("great") || code.Contains("pike")));

    internal static string Source(CollectibleObject item, Direction direction, bool twoHanded)
    {
        string code = item.Code?.Path ?? "";
        if (twoHanded && (code.Contains("claymore") || code.Contains("great"))) return direction switch
        {
            Direction.Left => "armory:sword-great-slash-2h-left",
            Direction.Right => "armory:sword-great-slash-2h-right",
            Direction.Overhead => "armory:sword-great-slash-2h-top-1",
            _ => "armory:sword-great-thrust-2h-bottom-1"
        };
        if (twoHanded && item.Tool == EnumTool.Spear && direction == Direction.Thrust)
            return "combatoverhaul:spear-thrust-2h-bottom";
        if (item.Tool != EnumTool.Spear && direction is Direction.Left or Direction.Right)
            return "combatoverhaul:sword-falx-slash-" + (direction == Direction.Right ? "1" : "2");
        return "warbandcombat:warband-" + direction.ToString().ToLowerInvariant();
    }

    public bool Animate(EntityPlayer player, Fighter fighter, long now, string? action)
        => Animate(player, fighter, now, action, 0);

    private bool Animate(EntityPlayer player, Fighter fighter, long now, string? action, int attackId)
    {
        bool own = Own(player.RightHandItemSlot.Itemstack?.Collectible);
        if (api is not ICoreClientAPI client) return own && !(action == "block" && fighter.ShieldGuard);
        var state = players.GetOrCreateValue(player);
        if (player == client.World.Player?.Entity) attackId = combat.Controls?.CurrentAttackId ?? attackId;
        if (action == "block" && fighter.ShieldGuard) { state.Active = false; state.Key = null; return false; }
        if (!own || player == client.World.Player?.Entity && combat.Controls?.Available != true || clips.Count == 0)
        {
            state.Active = false; state.Composer.StopAll(); state.Key = null; return false;
        }
        var stack = player.RightHandItemSlot.Itemstack!;
        if (!ReferenceEquals(state.Item, stack) || !ReferenceEquals(state.Offhand, player.LeftHandItemSlot.Itemstack))
        {
            state.Composer = new(); state.Frame = PlayerItemFrame.Empty; state.Key = null; state.At = now;
            state.Item = stack; state.Offhand = player.LeftHandItemSlot.Itemstack;
        }
        string direction = fighter.Direction.ToString().ToLowerInvariant();
        bool twoHanded = TwoHanded(player);
        string source = Source(stack.Collectible, fighter.Direction, twoHanded);
        string clip;
        if (action == "block") clip = CombatGuardAnimations.Code(fighter.Direction, twoHanded);
        else if (action == null)
        {
            clip = twoHanded && stack.Collectible.Tool == EnumTool.Spear ? "combatoverhaul:spear-ready-2h"
                : twoHanded ? NativePhaseAnimations.Code(Source(stack.Collectible, Direction.Right, true), "return")
                : stack.Collectible.Tool == EnumTool.Spear ? "warbandcombat:warband-return-thrust" : "combatoverhaul:sword-falx-ready";
        }
        else if (source.StartsWith("warbandcombat:warband-", StringComparison.Ordinal))
            clip = "warbandcombat:warband-" + (action == "recovery" ? "return" : action) + "-" + direction;
        else clip = NativePhaseAnimations.Code(source, action == "recovery" ? "return" : action, fighter.Timing.Strike);
        if (!clips.TryGetValue(clip, out var animation)) return false;
        string key = clip + (action is "windup" or "swing" or "recovery" ? ":" + attackId : "");
        state.Active = true;
        if (state.Key == key) return true;
        Advance(state, now); // Replacements begin from the actual composed pose, including early contacts.
        double duration = action switch
        {
            "windup" => Math.Max(40, fighter.Started + fighter.Timing.Windup - now),
            "swing" => Math.Max(40, fighter.ContactEnds - now),
            "recovery" => Math.Max(40, fighter.Ends - now),
            "block" => fighter.ShieldGuard ? CombatRules.ShieldRaise : CombatRules.GuardRaise,
            _ => 220
        };
        state.Composer.Play(new AnimationRequest(animation, (float)(animation.TotalDuration.TotalMilliseconds / duration),
            1, "main", TimeSpan.FromMilliseconds(35), TimeSpan.FromMilliseconds(20), false));
        state.Key = key;
        return true;
    }

    private static void Advance(Playback state, long now)
    {
        state.Frame = state.Composer.Compose(TimeSpan.FromMilliseconds(Math.Clamp(now - state.At, 0, 125)));
        state.At = now;
    }

    internal PlayerItemFrame? Frame(EntityPlayer player, long now)
    {
        if (api is not ICoreClientAPI client || !Own(player.RightHandItemSlot.Itemstack?.Collectible)) return null;
        var state = players.GetOrCreateValue(player);
        if (player != client.World.Player?.Entity)
        {
            var attrs = player.WatchedAttributes;
            int revision = attrs.GetInt("warbandPoseRevision");
            if (state.RemoteRevision != revision)
            {
                state.RemoteRevision = revision; state.RemoteAt = now;
                state.Remote.Reset(); state.Remote.Apply(Command.Begin, Direction.Right, now, CombatSystem.Timing(player));
                // Equipment changes invalidate a Fighter. Apply them before the
                // authoritative pose, so a remote shield block survives its first snapshot.
                state.Remote.SetShield(CombatShields.Equipped(player), now);
                state.Remote.Restore((Phase)attrs.GetInt("warbandPhase"), (Direction)attrs.GetInt("warbandDirection"), now,
                    attrs.GetInt("warbandElapsed"), attrs.GetInt("warbandHitIn"), attrs.GetInt("warbandEndsIn"), 0,
                    attrs.GetBool("warbandRecoveryAttackPose"), attrs.GetInt("warbandContactEndsIn"));
            }
            state.Remote.SetShield(CombatShields.Equipped(player), now);
            state.Remote.Advance(now);
            Animate(player, state.Remote, now, CombatPresentation.CombatAnimationsAction(state.Remote, now), attrs.GetInt("warbandAttackId"));
        }
        if (!state.Active) return null;
        Advance(state, now);
        return state.Frame;
    }

    internal static void Apply(PlayerItemFrame frame, ShapeElement element, ElementPose pose)
    {
        // Keep the saved torso/head impact and game camera anchors. CO owns the
        // six grip/arm bones here; pitch is applied to the same upper arms afterwards.
        if (element.Name is not ("UpperArmR" or "LowerArmR" or "ItemAnchor" or "UpperArmL" or "LowerArmL" or "ItemAnchorL")) return;
        var name = Enum.Parse<EnumAnimatedElement>(element.Name);
        frame.Apply(pose, name, Vector3.Zero, 0, overrideTorso: false);
    }

    internal void Clear() { players.Clear(); clips.Clear(); }
}
