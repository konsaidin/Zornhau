using System.Runtime.CompilerServices;
using CombatOverhaul;
using CombatOverhaul.Animations;
using CombatOverhaul.DamageSystems;
using CombatOverhaul.Implementations;
using CombatOverhaul.Inputs;
using CombatOverhaul.MeleeSystems;
using HarmonyLib;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.API.MathTools;

namespace WarbandCombat;

// Optional: this type is loaded only after the CO fork and its library exist.
internal sealed class OverhaulBridge : ICombatIntegration
{
    private readonly ICoreAPI api;
    private readonly CombatSystem combat;
    private readonly Harmony harmony;
    private readonly MeleeSystemServer? meleeServer;
    private readonly ConditionalWeakTable<DamageSource, Contact> contacts = new();
    private readonly Dictionary<int, MeleeWeaponStats> singleStats = new();
    private readonly Dictionary<int, MeleeWeaponModeCollectionStats> modeStats = new();
    private AnimationsManager? animations;
    private string? playing, shieldPlaying, collisionKey;
    private MeleeAttack? collisionAttack;
    private bool contactSent;
    private static OverhaulBridge? clientBridge, serverBridge;

    private sealed class Contact(Direction direction) : IWarbandDamage
    {
        public Direction AttackDirection => direction;
        public bool Blocked { get; set; }
    }

    public static ICombatIntegration Create(ICoreAPI api) => new OverhaulBridge(api);

    private OverhaulBridge(ICoreAPI api)
    {
        this.api = api;
        combat = api.ModLoader.GetModSystem<CombatSystem>();
        harmony = new Harmony("warbandcombat.overhaul." + api.Side);
        if (api is ICoreClientAPI)
        {
            clientBridge = this;
            // The native renderer has an immersive branch; keep the player's
            // chosen camera mode instead of CO forcing ordinary first person.
            var system = api.ModLoader.GetModSystem<CombatOverhaulSystem>();
            system.Settings.SwitchFromImmersiveFirstPerson = false;
            system.SettingsLoaded += PreserveImmersive;
            system.SettingsChanged += PreserveImmersive;
            Patch(typeof(ActionListener), "HandleMouseEvents", nameof(MousePrefix));
            Patch(typeof(ActionListener), "CallSubscriptions", nameof(ActionPrefix));
            Patch(typeof(DirectionCursorRenderer), "OnRenderFrame", nameof(CursorPrefix));
            Patch(typeof(ActionListener), "UpdateVanillaShieldRmbBridge", nameof(ShieldPrefix));
            Patch(typeof(MeleeWeaponClient), "OnMouseWheel", nameof(GripPrefix));
            Patch(typeof(MeleeWeaponClient), "Attack", nameof(NativeMeleePrefix));
            Patch(typeof(MeleeWeaponClient), "Block", nameof(NativeMeleePrefix));
            Patch(typeof(FirstPersonAnimationsBehavior), "IsImmersiveFirstPerson", nameof(ImmersivePrefix));
            harmony.Patch(AccessTools.Method(typeof(FirstPersonAnimationsBehavior), "ApplyFrame"),
                postfix: new HarmonyMethod(typeof(OverhaulBridge), nameof(FirstPersonAim)));
            harmony.Patch(AccessTools.Method(typeof(ThirdPersonAnimationsBehavior), "ApplyFrame"),
                postfix: new HarmonyMethod(typeof(OverhaulBridge), nameof(ThirdPersonAim)));
        }
        else
        {
            serverBridge = this;
            Patch(typeof(MeleeSystemServer), "Attack", nameof(AttackPrefix));
            var block = AccessTools.Method(typeof(MeleeBlockSystemServer), "HandlePacket",
                new[] { typeof(IServerPlayer), typeof(DamageBlockPacket) });
            harmony.Patch(block ?? throw new MissingMethodException("CO block handler"),
                prefix: new HarmonyMethod(typeof(OverhaulBridge), nameof(BlockPrefix)));
            meleeServer = api.ModLoader.GetModSystem<CombatOverhaulSystem>().ServerMeleeSystem
                ?? throw new InvalidOperationException("CO server melee system was not initialized");
            meleeServer.OnDealMeleeDamage += BeforeDamage;
            meleeServer.OnMeleeDamageResolved += DamageResolved;
        }
        api.Logger.Notification("[Warband Combat] CO adapter initialized; Warband controls attacks and guards.");
    }

    private static void PreserveImmersive(Settings settings)
        => settings.SwitchFromImmersiveFirstPerson = false;

    private void Patch(Type type, string method, string prefix)
        => harmony.Patch(AccessTools.Method(type, method) ?? throw new MissingMethodException(type.FullName, method),
            prefix: new HarmonyMethod(typeof(OverhaulBridge), prefix));

    private bool OwnsInput => combat.Controls?.OwnsMeleeInput == true;
    private bool OwnsShieldInput => combat.Controls?.OwnsShieldInput == true;
    private static bool MousePrefix(MouseEvent mouseEvent)
        => !(mouseEvent.Button == EnumMouseButton.Left && clientBridge?.OwnsInput == true
            || mouseEvent.Button == EnumMouseButton.Right
                && (clientBridge?.OwnsInput == true || clientBridge?.OwnsShieldInput == true));
    private static bool ActionPrefix(EnumEntityAction action, ref bool __result)
    {
        bool owned = clientBridge?.OwnsInput == true || clientBridge?.OwnsShieldInput == true
            && action is EnumEntityAction.RightMouseDown or EnumEntityAction.InWorldRightMouseDown;
        if (!owned || action is not (EnumEntityAction.LeftMouseDown
            or EnumEntityAction.RightMouseDown or EnumEntityAction.InWorldLeftMouseDown or EnumEntityAction.InWorldRightMouseDown)) return true;
        __result = false; return false;
    }
    private static bool CursorPrefix() => clientBridge?.OwnsInput != true;
    private static bool ShieldPrefix() => clientBridge?.OwnsInput != true && clientBridge?.OwnsShieldInput != true;
    private static bool GripPrefix(ref bool __result)
    {
        if (clientBridge?.OwnsInput != true || clientBridge.combat.Controls?.Fighter.Phase == Phase.Idle) return true;
        __result = false; return false;
    }
    private static bool NativeMeleePrefix(ref bool __result)
    {
        if (clientBridge?.OwnsInput != true) return true;
        __result = false; return false;
    }
    private static bool ImmersivePrefix(Entity entity, ref bool __result)
    {
        if (entity.Api is not ICoreClientAPI client || entity != client.World.Player?.Entity) return true;
        // Ready/idle requests bypass CO's cached camera-setting refresh. Read the
        // live option so equipping Armory and toggling the option both work.
        __result = client.Settings.Bool["immersiveFpMode"]
            && client.World.Player.CameraMode == EnumCameraMode.FirstPerson;
        return false;
    }
    private static bool BlockPrefix(IServerPlayer player)
        => !CombatSystem.Supported(player.Entity.RightHandItemSlot.Itemstack?.Collectible) && !CombatShields.Equipped(player.Entity);

    private static bool AimsArms(Entity entity, ElementPose pose, bool clearPose)
    {
        if (clearPose || clientBridge == null || entity is not EntityPlayer player
            || pose.ForElement?.Name is not ("UpperArmR" or "UpperArmL")
            || player.RightHandItemSlot.Itemstack is not { } stack
            || !clientBridge.UsesPhysicalContacts(stack.Collectible)) return false;
        var phase = player == (clientBridge.api as ICoreClientAPI)?.World.Player?.Entity
            ? clientBridge.combat.Controls?.Fighter.Phase ?? Phase.Idle
            : (Phase)player.WatchedAttributes.GetInt("warbandPhase");
        return phase is Phase.Windup or Phase.Swing or Phase.Recovery;
    }

    private static void FirstPersonAim(FirstPersonAnimationsBehavior __instance, ElementPose pose, bool clearPose)
    {
        if (!AimsArms(__instance.entity, pose, clearPose)
            || clientBridge?.api is not ICoreClientAPI client || !client.Settings.Bool["immersiveFpMode"]) return;
        // Ordinary FP already pitches the renderer's whole model matrix.
        // Immersive FP leaves that matrix upright; aim only the weapon arms.
        CombatAim.RotateArm(pose, CombatAim.LookPitch(__instance.entity.Pos.Pitch));
    }

    private static void ThirdPersonAim(ThirdPersonAnimationsBehavior __instance, PlayerItemFrame frame, ElementPose pose, bool clearPose)
    {
        if (!AimsArms(__instance.entity, pose, clearPose)) return;
        float nativePitch = GameMath.Clamp(__instance.entity.Pos.HeadPitch * frame.Player.PitchFollow
            * frame.Player.DetachedAnchorFollow, PlayerFrame.PitchAngleMin * GameMath.DEG2RAD,
            PlayerFrame.PitchAngleMax * GameMath.DEG2RAD);
        CombatAim.RotateArm(pose, CombatAim.LookPitch(__instance.entity.Pos.Pitch) - nativePitch);
    }

    public bool UsesPhysicalContacts(CollectibleObject item) => OverhaulCompat.IsMelee(item);
    public void ClearNativeBlock(EntityPlayer player)
    {
        if (CombatSystem.Supported(player.RightHandItemSlot.Itemstack?.Collectible) || CombatShields.Equipped(player))
        {
            var model = player.GetBehavior<PlayerDamageModelBehavior>();
            if (model != null) model.CurrentDamageBlock = null;
        }
    }
    public IWarbandDamage? GetValidatedDamage(DamageSource source)
        => contacts.TryGetValue(source, out var contact) ? contact : null;

    private static MeleeWeaponClientModesCollection? ClientModes(CollectibleObject item)
        => (item as MeleeWeapon)?.ClientModes
            ?? item.GetCollectibleBehavior<MeleeWeaponBehavior>(true)?.ClientModes;

    public string WeaponMode(EntityPlayer player)
    {
        var item = player.RightHandItemSlot.Itemstack?.Collectible;
        return item == null ? "" : ClientModes(item)?.CurrentModeCode ?? "";
    }

    private StanceStats? Stance(EntityPlayer player) => Stance(player, out _);

    private StanceStats? Stance(EntityPlayer player, out bool twoHanded)
    {
        twoHanded = false;
        var stack = player.RightHandItemSlot.Itemstack;
        if (stack == null || !UsesPhysicalContacts(stack.Collectible)) return null;
        MeleeWeaponStats? stats;
        if (stack.ItemAttributes.KeyExists("Modes"))
        {
            if (!modeStats.TryGetValue(stack.Collectible.Id, out var modes))
                modeStats[stack.Collectible.Id] = modes = stack.ItemAttributes.AsObject<MeleeWeaponModeCollectionStats>()
                    ?? new MeleeWeaponModeCollectionStats();
            if (modes == null) return null;
            string mode = api.Side == EnumAppSide.Client ? WeaponMode(player)
                : player.WatchedAttributes.GetString("warbandWeaponMode", "");
            if (mode.Length == 0) mode = modes.Modes.Keys.FirstOrDefault() ?? "";
            if (!modes.Modes.TryGetValue(mode, out var selected)) return null;
            stats = selected;
        }
        else
        {
            if (!singleStats.TryGetValue(stack.Collectible.Id, out var cached))
                singleStats[stack.Collectible.Id] = cached = stack.ItemAttributes.AsObject<MeleeWeaponStats>()
                    ?? new MeleeWeaponStats();
            stats = cached;
        }
        if (stats == null) return null;
        string other = player.LeftHandItemSlot.Itemstack?.Collectible.Code.ToString() ?? "";
        if (other.Length == 0 && stats.TwoHandedStance != null)
        { twoHanded = true; return stats.TwoHandedStance; }
        var dual = stats.MainHandDualWieldStances.FirstOrDefault(kv => WildcardUtil.Match(kv.Key, other)).Value;
        return other.Length > 0 && dual != null ? dual : stats.OneHandedStance;
    }

    private static AttackDirection NativeDirection(Direction direction) => direction switch
    {
        Direction.Right => AttackDirection.Right, Direction.Left => AttackDirection.Left,
        Direction.Thrust => AttackDirection.Bottom, _ => AttackDirection.Top
    };
    private static MeleeAttackStats? AttackStats(StanceStats? stance, Direction direction)
    {
        if (stance?.CanAttack != true) return null;
        if (stance.DirectionalAttacks?.TryGetValue(NativeDirection(direction).ToString(), out var attack) == true) return attack;
        return stance.Attack ?? stance.DirectionalAttacks?.Values.FirstOrDefault();
    }

    private AttackProfile? Profile(EntityPlayer attacker, Entity target, Direction direction, string? damageType)
    {
        var stats = AttackStats(Stance(attacker), direction);
        var stack = attacker.RightHandItemSlot.Itemstack;
        if (stats == null || stack == null || stats.DamageTypes.Length == 0) return null;
        var selected = damageType == null ? stats.DamageTypes.FirstOrDefault()
            : stats.DamageTypes.FirstOrDefault(d => d.Damage.DamageType == damageType);
        if (selected == null || !Enum.TryParse<EnumDamageType>(selected.Damage.DamageType, out var type)) return null;
        var buffs = ItemStackMeleeWeaponStats.FromItemStack(stack);
        float power = selected.Damage.Damage * attacker.Stats.GetBlended("meleeWeaponsDamage");
        if (target.Properties.Attributes?["isMechanical"].AsBool() == true)
            power *= attacker.Stats.GetBlended("mechanicalsDamage");
        power = (power + buffs.DamageBonus) * buffs.DamageMultiplier;
        int tier = Math.Max(0, selected.Damage.Tier + buffs.DamageTierBonus
            + (int)(attacker.Stats.GetBlended(MeleeDamageType.DamageTierPlayerStatPrefix + type) - 1));
        // MaxReach is a broad search radius in CO, often 9 even for short swords.
        // Bound claimed contacts by the actual configured blade plus arm extension.
        float extent = stats.DamageTypes.Max(d => d.Collider.Length == 6
            ? (float)Math.Sqrt(Enumerable.Range(0, 3).Sum(i => Math.Pow(d.Collider[i] + d.Collider[i + 3], 2))) : 0);
        float reach = Math.Clamp(Math.Min(stats.MaxReach, extent + 1.5f), 1.5f, 6);
        return new(power, reach, tier, type, selected.Knockback * buffs.KnockbackMultiplier,
            selected.DurabilityDamage, Math.Max(0, selected.Damage.ArmorPiercingTier + buffs.ArmorPiercingBonus));
    }

    private static bool AttackPrefix(IServerPlayer player, MeleeDamagePacket packet)
        => serverBridge?.ValidatePacket(player, packet) ?? true;

    private bool ValidatePacket(IServerPlayer player, MeleeDamagePacket packet)
    {
        var item = player.Entity.RightHandItemSlot.Itemstack?.Collectible;
        if (item == null || !CombatSystem.Supported(item)) return true;
        if (!UsesPhysicalContacts(item) || !packet.MainHand || packet.AttackerEntityId != player.Entity.EntityId
            || !combat.CanAcceptPhysicalContact(player)) return false;
        var target = api.World.GetEntityById(packet.TargetEntityId);
        if (target == null || target.Pos.Dimension != player.Entity.Pos.Dimension || !target.Alive
            || target.EntityId == player.Entity.EntityId || packet.Position == null || packet.Position.Length != 3
            || packet.Position.Any(x => !double.IsFinite(x))) return false;
        var profile = Profile(player.Entity, target, combat.PhysicalDirection(player), packet.DamageType);
        if (profile == null) return false;
        var p = profile.Value;
        var pos = player.Entity.Pos;
        double distanceSquared = Enumerable.Range(0, 3).Sum(i => Math.Pow(packet.Position[i]
            - (i == 0 ? pos.X : i == 1 ? pos.Y + player.Entity.LocalEyePos.Y * .65 : pos.Z), 2));
        if (distanceSquared > Math.Pow(p.Reach + .75, 2)) return false;
        var bounds = target.CollisionBox;
        if (bounds == null || packet.Position[0] < target.Pos.X + bounds.X1 - .75
            || packet.Position[0] > target.Pos.X + bounds.X2 + .75
            || packet.Position[1] < target.Pos.Y + bounds.Y1 - .75
            || packet.Position[1] > target.Pos.Y + bounds.Y2 + .75
            || packet.Position[2] < target.Pos.Z + bounds.Z1 - .75
            || packet.Position[2] > target.Pos.Z + bounds.Z2 + .75) return false;
        // The library retains terrain, equipment and typed-damage validation.
        // Replace client claims with stats from the authorized mode/direction.
        packet.Damage = p.Power; packet.Tier = p.Tier; packet.ArmorPiercingTier = p.ArmorPiercing;
        packet.Knockback = p.Knockback; packet.DurabilityDamage = p.Durability;
        packet.StaggerTimeMs = packet.StaggerTier = 0; packet.ImpaleEnabled = false;
        packet.AttackDirection = (int)NativeDirection(combat.PhysicalDirection(player));
        return true;
    }

    private void BeforeDamage(Entity target, DamageSource source, ItemSlot? slot, ref float damage)
    {
        if (source.SourceEntity is not EntityPlayer attacker || !CombatSystem.Supported(slot?.Itemstack?.Collectible)) return;
        if (attacker.Player is not IServerPlayer player || !combat.AcceptPhysicalContact(player, target))
        { damage = 0; source.KnockbackStrength = 0; return; }
        Direction direction = combat.PhysicalDirection(player);
        contacts.Add(source, new Contact(direction));
        if (direction == Direction.Overhead) damage *= 1.12f;
        if (direction == Direction.Thrust && slot?.Itemstack?.Collectible.Tool == EnumTool.Spear) damage *= 1.15f;
        ClearNativeBlock(attacker);
        if (target is EntityPlayer victim) ClearNativeBlock(victim);
    }
    private void DamageResolved(MeleeDamageResolvedEventArgs args)
    {
        if (!contacts.TryGetValue(args.DamageSource, out var contact)
            || args.DamageSource.SourceEntity is not EntityPlayer attacker || attacker.Player is not IServerPlayer player) return;
        combat.PhysicalFeedback(player, contact.Blocked, args.DamageReceived);
    }

    public void AssetsReady()
    {
        if (api is not ICoreClientAPI) return;
        animations = api.ModLoader.GetModSystem<CombatOverhaulAnimationsSystem>().PlayerAnimationsManager
            ?? throw new InvalidOperationException("CO animation manager was not initialized");
        var author = JsonConvert.DeserializeObject<Dictionary<string, AnimationJson>>(
            api.Assets.Get("warbandcombat:config/reanimated.json").ToText())!;
        var source = animations.Animations.ToDictionary(kv => kv.Key, kv => AnimationJson.FromAnimation(kv.Value));
        foreach (var (code, clip) in author.ToArray())
        {
            string alias = code.Replace("armory:", "combatoverhaul:");
            if (alias != code && source.ContainsKey(alias)) author[alias] = clip;
            if (code.EndsWith("-1", StringComparison.Ordinal) && source.ContainsKey(code[..^2]))
                author[code[..^2]] = clip;
        }
        foreach (var (code, clip) in author) source[code] = clip;
        int count = 0;
        foreach (var (code, clip) in source)
        {
            if (clip.PlayerKeyFrames.Length == 0) continue;
            // Override only matching original codes for ready/idle poses as well.
            if (author.ContainsKey(code)) animations.Animations[code] = clip.ToAnimation();
            foreach (var (phaseCode, phase) in OverhaulPhaseAnimations.Split(code, clip))
            { animations.Animations[phaseCode] = phase.ToAnimation(); count++; }
            if (code.EndsWith("-tp", StringComparison.Ordinal))
                foreach (var (phaseCode, phase) in OverhaulPhaseAnimations.Split(code[..^3],
                    author.GetValueOrDefault(code[..^3])
                        ?? (code.Contains("-2h") ? source.GetValueOrDefault(code[..^3]) : null) ?? clip))
                    animations.Animations[phaseCode + "-tp"] = phase.ToAnimation();
        }
        foreach (var (code, clip) in JsonConvert.DeserializeObject<Dictionary<string, AnimationJson>>(
            api.Assets.Get("warbandcombat:config/overhaul-fallback.json").ToText())!)
            animations.Animations[code] = clip.ToAnimation();
        api.Logger.Notification("[Warband Combat] Registered {0} CO phase clips and {1} Reanimated sources.", count, author.Count);
    }

    private string Source(StanceStats? stance, Direction direction, bool block)
    {
        if (block) return OverhaulAnimationResolver.Guard(direction);
        var attacks = stance?.AttackAnimation;
        if (attacks?.TryGetValue(NativeDirection(direction).ToString(), out var clips) == true && clips.Length > 0) return clips[0];
        var genericClips = attacks?.GetValueOrDefault("Main") ?? attacks?.Values.FirstOrDefault();
        string? generic = genericClips?.FirstOrDefault();
        if (genericClips != null)
        {
            string family = direction == Direction.Thrust ? "thrust" : "slash";
            var matching = genericClips.Where(c => c.Contains(family, StringComparison.Ordinal)).ToArray();
            if (matching.Length > 0 && direction is Direction.Left or Direction.Right or Direction.Thrust)
                return matching[direction == Direction.Left && matching.Length > 1 ? 1 : 0];
        }
        // Directionless native clips are frequently thrust-only or one-sided.
        // Use authored Warband poses for the directions absent from those assets.
        if (generic != null && ((direction == Direction.Thrust && generic.Contains("thrust"))
            || (direction == Direction.Right && generic.EndsWith("slash-1"))
            || (direction == Direction.Left && generic.EndsWith("slash-2")))) return generic;
        if (generic?.Contains("sword-falx") == true && direction is Direction.Right or Direction.Left)
            return "combatoverhaul:sword-falx-slash-" + (direction == Direction.Right ? "1" : "2");
        return "warbandcombat:warband-swing-" + direction.ToString().ToLowerInvariant();
    }

    private bool AnimateShield(EntityPlayer player, Fighter fighter, long now, string? action,
        FirstPersonAnimationsBehavior? fp, ThirdPersonAnimationsBehavior? tp)
    {
        bool guarding = action == "block" && fighter.ShieldGuard && CombatShields.Equipped(player);
        if (!guarding)
        {
            if (shieldPlaying != null)
            {
                if (!CombatShields.Equipped(player)) { fp?.Stop("mainOffhand"); tp?.Stop("mainOffhand"); }
                fp?.PlayReadyAnimation(false); tp?.PlayReadyAnimation(false);
                shieldPlaying = null;
            }
            return false;
        }
        var stack = player.LeftHandItemSlot.Itemstack!;
        if (!singleStats.TryGetValue(stack.Collectible.Id, out var stats))
            singleStats[stack.Collectible.Id] = stats = stack.ItemAttributes.AsObject<MeleeWeaponStats>() ?? new();
        string source = stats.OffHandStance?.BlockAnimation ?? stats.OffHandStance?.ParryAnimation
            ?? "combatoverhaul:shield-light-parry";
        string clip = OverhaulPhaseAnimations.Code(source, "windup");
        string key = stack.Collectible.Id + ":" + clip;
        if (shieldPlaying == key) return true;
        if (fp == null || tp == null || animations?.Animations.TryGetValue(clip, out var animation) != true) return false;
        // Cancel a feinted chamber, then leave the weapon in its normal grip.
        if (playing != null) { fp.PlayReadyAnimation(); tp.PlayReadyAnimation(); playing = null; }
        collisionKey = null; collisionAttack = null;
        double remaining = Math.Max(40, fighter.Started + CombatRules.ShieldRaise - now);
        var request = new AnimationRequestByCode(clip, (float)(animation!.TotalDuration.TotalMilliseconds / remaining), 1,
            "mainOffhand", TimeSpan.FromMilliseconds(35), TimeSpan.FromMilliseconds(80), false);
        fp.Play(request, false); tp.Play(request, false); shieldPlaying = key;
        return true;
    }

    public bool Animate(EntityPlayer player, Fighter fighter, long now, string? action)
    {
        bool physical = player.RightHandItemSlot.Itemstack is { } stack && UsesPhysicalContacts(stack.Collectible);
        if (api is not ICoreClientAPI client || player != client.World.Player?.Entity) return physical;
        var fp = player.GetBehavior<FirstPersonAnimationsBehavior>();
        var tp = player.GetBehavior<ThirdPersonAnimationsBehavior>();
        if (AnimateShield(player, fighter, now, action, fp, tp)) return true;
        if (!physical || action == null)
        {
            if (playing != null)
            {
                // Keep the composer's previous frame when replacing recovery with
                // ready. Removing "main" first would interpolate from a zero pose
                // and look like drawing the weapon again after every attack.
                if (!physical) { fp?.Stop("main"); tp?.Stop("main"); }
                fp?.PlayReadyAnimation(); tp?.PlayReadyAnimation(); playing = null;
            }
            collisionKey = null; collisionAttack = null;
            return physical;
        }
        if (fp == null || tp == null || animations == null) return false;
        var stance = Stance(player, out bool twoHanded);
        string? source = action == "block" ? OverhaulAnimationResolver.Guard(fighter.Direction)
            : twoHanded && stance != null
            ? OverhaulAnimationResolver.TwoHanded(stance, fighter.Direction, action == "block",
                player.RightHandItemSlot.Itemstack!.Collectible.Code.Path,
                code => animations.Animations.ContainsKey(OverhaulPhaseAnimations.Code(code, "windup")))
            : Source(stance, fighter.Direction, action == "block");
        // A missing optional package must never make a two-handed stance silently
        // animate with the old one-handed arms.
        if (source == null) return twoHanded;
        string clip;
        bool fallback = source.StartsWith("warbandcombat:warband-");
        if (fallback) clip = "warbandcombat:warband-" + (action == "recovery" ? "return" : action) + "-"
            + fighter.Direction.ToString().ToLowerInvariant();
        else clip = OverhaulPhaseAnimations.Code(source,
            action == "block" ? "block-" + fighter.Direction.ToString().ToLowerInvariant()
                : action == "recovery" ? "return" : action, fighter.Timing.Strike);
        string key = player.RightHandItemSlot.Itemstack!.Collectible.Id + ":" + WeaponMode(player) + ":" + clip;
        if (playing == key) return true;
        if (!animations.Animations.TryGetValue(clip, out var animation)) return false;
        playing = key;
        double remaining = action switch
        {
            "windup" => Math.Max(40, fighter.Started + fighter.Timing.Windup - now),
            "swing" => Math.Max(40, fighter.ContactEnds - now),
            "recovery" => Math.Max(40, fighter.Ends - now),
            _ => CombatRules.GuardRaise
        };
        var request = new AnimationRequestByCode(clip, (float)(animation.TotalDuration.TotalMilliseconds / remaining), 1,
            "main", TimeSpan.FromMilliseconds(35), TimeSpan.FromMilliseconds(20), false);
        fp.Play(request); tp.Play(request);
        if (action == "swing")
        {
            var stats = AttackStats(stance, fighter.Direction);
            if (stats != null)
            {
                var contactStats = JsonConvert.DeserializeObject<MeleeAttackStats>(JsonConvert.SerializeObject(stats))!;
                contactStats.HitOnlyOneEntity = true; contactStats.StopOnEntityHit = true;
                contactStats.CollideWithTerrain = true; contactStats.Impale = null;
                foreach (var d in contactStats.DamageTypes)
                {
                    d.StaggerTimeMs = 0; d.PushTier = 0;
                    d.Radius += CombatRules.WeaponContactPadding;
                }
                collisionAttack = new MeleeAttack(client, contactStats); collisionAttack.Start(player.Player);
                collisionKey = key; contactSent = false;
            }
        }
        return true;
    }

    public void Frame(EntityPlayer player, Fighter fighter, long now)
    {
        if (collisionAttack == null || collisionKey != playing || fighter.Phase != Phase.Swing || contactSent) return;
        var slot = player.RightHandItemSlot;
        if (slot.Itemstack == null) return;
        if (!fighter.CanHit(now)) { collisionAttack.PrepareColliders(player.Player, slot, true); return; }
        contactSent = collisionAttack.Attack(player.Player, slot, true, out _, out _,
            ItemStackMeleeWeaponStats.FromItemStack(slot.Itemstack), NativeDirection(fighter.Direction), true);
        if (contactSent) fighter.TakeHit(now);
    }

    public void Dispose()
    {
        if (meleeServer != null)
        { meleeServer.OnDealMeleeDamage -= BeforeDamage; meleeServer.OnMeleeDamageResolved -= DamageResolved; }
        harmony.UnpatchAll(harmony.Id);
        if (clientBridge == this)
        {
            var system = api.ModLoader.GetModSystem<CombatOverhaulSystem>();
            system.SettingsLoaded -= PreserveImmersive; system.SettingsChanged -= PreserveImmersive;
            clientBridge = null;
        }
        if (serverBridge == this) serverBridge = null;
    }
}
