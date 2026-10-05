using System.Runtime.CompilerServices;
using CombatOverhaul;
using CombatOverhaul.Animations;
using CombatOverhaul.DamageSystems;
using CombatOverhaul.Colliders;
using OpenTK.Mathematics;
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
    private readonly OverhaulContactBounds? contactBounds;
    private readonly long contactTrackingTick;
    private readonly ConditionalWeakTable<DamageSource, Contact> contacts = new();
    private readonly Dictionary<int, MeleeWeaponStats> singleStats = new();
    private readonly Dictionary<int, MeleeWeaponModeCollectionStats> modeStats = new();
    private AnimationsManager? animations;
    private string? playing, shieldPlaying, collisionKey;
    private MeleeAttack? collisionAttack;
    private bool contactSent;
    private bool debugTerrainBlocked;
    private readonly List<(LineSegmentCollider current, LineSegmentCollider previous, float radius)> debugColliders = new();
    private long debugUntil;
    private ItemStack? debugWeapon;
    [ThreadStatic] private static AttackTrace? activeTrace;

    private sealed class AttackTrace(IServerPlayer player, MeleeDamagePacket packet, int id, AttackTrace? previous)
    {
        internal readonly IServerPlayer Player = player;
        internal readonly MeleeDamagePacket Packet = packet;
        internal readonly int Id = id;
        internal readonly AttackTrace? Previous = previous;
        internal bool Reported;
        internal string BoundDetail = "";
        internal void Report(string result, string detail)
        {
            if (Reported) return;
            Reported = true;
            serverBridge?.combat.ReportHitDiagnostic(Player, Id, Packet.TargetEntityId, result, detail);
        }
    }
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
            contactBounds = new OverhaulContactBounds(api);
            contactTrackingTick = api.Event.RegisterGameTickListener(TrackContactTargets, 50);
            harmony.Patch(AccessTools.Method(typeof(MeleeSystemServer), "Attack"),
                prefix: new HarmonyMethod(typeof(OverhaulBridge), nameof(AttackPrefix)),
                finalizer: new HarmonyMethod(typeof(OverhaulBridge), nameof(AttackFinished)));
            Patch(typeof(MeleeSystemServer), "LogRejectedAttackPacket", nameof(NativeRejected));
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
        if (direction is Direction.Right or Direction.Left && stance.DirectionalAttacks != null)
            foreach (string diagonal in new[] { "Bottom" + NativeDirection(direction), "Top" + NativeDirection(direction) })
                if (stance.DirectionalAttacks.TryGetValue(diagonal, out attack)) return attack;
        return stance.Attack ?? stance.DirectionalAttacks?.Values.FirstOrDefault();
    }

    private AttackProfile? Profile(EntityPlayer attacker, Entity target, Direction direction, string? damageType, bool balanced = false)
    {
        var stats = AttackStats(Stance(attacker), direction);
        var stack = attacker.RightHandItemSlot.Itemstack;
        if (stats == null || stack == null || stats.DamageTypes.Length == 0) return null;
        var selected = damageType == null ? stats.DamageTypes.FirstOrDefault()
            : stats.DamageTypes.FirstOrDefault(d => d.Damage.DamageType == damageType);
        if (selected == null || !Enum.TryParse<EnumDamageType>(selected.Damage.DamageType, out var type)) return null;
        var buffs = ItemStackMeleeWeaponStats.FromItemStack(stack);
        float basePower = selected.Damage.Damage;
        if (balanced) basePower = CombatBalance.Power(attacker, direction, basePower,
            api.Side == EnumAppSide.Client ? WeaponMode(attacker) : attacker.WatchedAttributes.GetString("warbandWeaponMode", ""));
        float power = basePower * attacker.Stats.GetBlended("meleeWeaponsDamage");
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

    private static bool AttackPrefix(IServerPlayer player, MeleeDamagePacket packet, out AttackTrace? __state)
    {
        var bridge = serverBridge;
        int id = bridge?.combat.DiagnosticAttackId(player) ?? 0;
        __state = id > 0 || activeTrace != null ? new AttackTrace(player, packet, id, activeTrace) : null;
        if (__state != null) activeTrace = __state;
        return bridge?.ValidatePacket(player, packet) ?? true;
    }

    private static Exception? AttackFinished(Exception? __exception, AttackTrace? __state)
    {
        if (__state == null) return __exception;
        try
        {
            if (!__state.Reported)
            {
                var target = serverBridge?.api.World.GetEntityById(__state.Packet.TargetEntityId);
                string reason = __exception != null ? "server-exception"
                    : target is EntityPlayer && serverBridge?.api is ICoreServerAPI api && !api.Server.Config.AllowPvP ? "pvp-disabled"
                    : target is EntityPlayer && !__state.Player.HasPrivilege("attackplayers") ? "no-player-privilege"
                    : target is EntityAgent && !__state.Player.HasPrivilege("attackcreatures") ? "no-creature-privilege"
                    : "native-no-result";
                __state.Report(reason, __exception?.GetType().Name ?? "No resolved damage event from CO");
            }
        }
        finally { activeTrace = __state.Previous; }
        return __exception;
    }

    private static void NativeRejected(IServerPlayer player, MeleeDamagePacket packet, string reason, string details)
    {
        if (activeTrace?.Packet == packet && activeTrace.Player == player)
            activeTrace.Report("co-" + reason, details);
    }

    private bool RejectPacket(string reason, string detail = "")
    {
        activeTrace?.Report(reason, detail);
        return false;
    }

    private bool ValidatePacket(IServerPlayer player, MeleeDamagePacket packet)
    {
        var item = player.Entity.RightHandItemSlot.Itemstack?.Collectible;
        if (item == null || !CombatSystem.Supported(item)) return true;
        if (!UsesPhysicalContacts(item)) return RejectPacket("not-physical-weapon");
        if (!packet.MainHand) return RejectPacket("wrong-hand");
        if (packet.AttackerEntityId != player.Entity.EntityId) return RejectPacket("wrong-attacker");
        if (!combat.CanAcceptPhysicalContact(player)) return RejectPacket("state-or-equipment", combat.PhysicalContactState(player));
        var target = api.World.GetEntityById(packet.TargetEntityId);
        if (target == null || target.Pos.Dimension != player.Entity.Pos.Dimension || !target.Alive
            || target.EntityId == player.Entity.EntityId || packet.Position == null || packet.Position.Length != 3
            || packet.Position.Any(x => !double.IsFinite(x))) return RejectPacket("invalid-target-or-position");
        var profile = Profile(player.Entity, target, combat.PhysicalDirection(player), packet.DamageType);
        if (profile == null) return RejectPacket("missing-attack-profile");
        var p = profile.Value;
        var pos = player.Entity.Pos;
        double distanceSquared = Enumerable.Range(0, 3).Sum(i => Math.Pow(packet.Position[i]
            - (i == 0 ? pos.X : i == 1 ? pos.Y + player.Entity.LocalEyePos.Y * .65 : pos.Z), 2));
        if (distanceSquared > Math.Pow(p.Reach + .75, 2))
            return RejectPacket("reach", FormattableString.Invariant($"distance={Math.Sqrt(distanceSquared):F3} allowed={p.Reach + .75:F3}"));
        var point = new Vec3d(packet.Position[0], packet.Position[1], packet.Position[2]);
        if (contactBounds != null)
        {
            if (!contactBounds.Validate(target, point, api.World.ElapsedMilliseconds, player.Ping, out string boundDetail))
                return RejectPacket("outside-target-bounds", boundDetail);
            if (activeTrace != null) activeTrace.BoundDetail = boundDetail;
        }
        // The library retains terrain, equipment and typed-damage validation.
        // Replace client claims with stats from the authorized mode/direction.
        packet.Damage = p.Power; packet.Tier = p.Tier; packet.ArmorPiercingTier = p.ArmorPiercing;
        packet.Knockback = p.Knockback; packet.DurabilityDamage = p.Durability;
        packet.StaggerTimeMs = packet.StaggerTier = 0; packet.ImpaleEnabled = false;
        packet.AttackDirection = (int)NativeDirection(combat.PhysicalDirection(player));
        return true;
    }

    private void TrackContactTargets(float dt)
    {
        if (api is not ICoreServerAPI server || contactBounds == null) return;
        long now = api.World.ElapsedMilliseconds;
        var recorded = new HashSet<long>();
        foreach (var player in server.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (!combat.TracksPhysicalContacts(player)) continue;
            foreach (var target in api.World.GetEntitiesAround(player.Entity.Pos.XYZ, 10, 10))
            {
                if (!target.Alive || !target.IsCreature || target.Pos.Dimension != player.Entity.Pos.Dimension
                    || !recorded.Add(target.EntityId)) continue;
                contactBounds.Record(target, now);
            }
        }
        contactBounds.Prune(now);
    }

    private void BeforeDamage(Entity target, DamageSource source, ItemSlot? slot, ref float damage)
    {
        if (source.SourceEntity is not EntityPlayer attacker || !CombatSystem.Supported(slot?.Itemstack?.Collectible)) return;
        if (attacker.Player is not IServerPlayer player || !combat.AcceptPhysicalContact(player, target))
        {
            if (activeTrace?.Packet.AttackerEntityId == attacker.EntityId && activeTrace.Packet.TargetEntityId == target.EntityId)
                activeTrace.Report("contact-refused", attacker.Player is IServerPlayer p ? combat.PhysicalContactState(p) : "not-server-player");
            damage = 0; source.KnockbackStrength = 0; return;
        }
        Direction direction = combat.PhysicalDirection(player);
        // CO validates its native damage ceiling before this event. Normalize
        // only after that validation, retaining flat bonuses after base scaling.
        var profile = Profile(attacker, target, direction, source.Type.ToString(), balanced: true);
        if (profile == null) { damage = 0; source.KnockbackStrength = 0; return; }
        contacts.Add(source, new Contact(direction));
        damage = profile.Value.Power * CombatBalance.DirectionBonus(slot?.Itemstack?.Collectible.Tool, direction);
        if (source is DirectionalTypedDamageSource typed)
            combat.Fatalities?.RecordContact(target, source, direction, typed.Position, typed.Collider);
        ClearNativeBlock(attacker);
        if (target is EntityPlayer victim) ClearNativeBlock(victim);
    }
    private void DamageResolved(MeleeDamageResolvedEventArgs args)
    {
        if (!contacts.TryGetValue(args.DamageSource, out var contact)
            || args.DamageSource.SourceEntity is not EntityPlayer attacker || attacker.Player is not IServerPlayer player) return;
        combat.PhysicalFeedback(player, args.Target, contact.Blocked, args.DamageReceived);
        if (activeTrace?.Player == player && activeTrace.Packet.TargetEntityId == args.Target.EntityId)
            activeTrace.Report(contact.Blocked ? "blocked" : args.DamageReceived ? "hit" : "no-damage",
                $"target={args.Target.Code} collider={activeTrace.Packet.Collider} damageReceived={args.DamageReceived} {activeTrace.BoundDetail}");
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
        string? source = action == "block" ? OverhaulAnimationResolver.Guard(fighter.Direction, twoHanded)
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
        if (fallback) clip = action == "block" ? source
            : "warbandcombat:warband-" + (action == "recovery" ? "return" : action) + "-"
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
                collisionKey = key; contactSent = false; debugTerrainBlocked = false;
                if (combat.Controls?.HitDiagnosticsEnabled == true)
                    api.Logger.Notification("[Warband Hit] collider clip={0} source={1} direction={2} maxReach={3} radii={4}",
                        clip, source, fighter.Direction, contactStats.MaxReach,
                        string.Join(",", contactStats.DamageTypes.Select(d => d.Radius.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))));
                debugColliders.Clear(); debugWeapon = null;
            }
            else
            {
                collisionAttack = null; debugColliders.Clear();
                combat.Controls?.RecordMissingCollider();
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
        IEnumerable<(Block block, Vector3d point)> terrain;
        IEnumerable<(Entity entity, Vector3d point)> entities;
        using (CombatPrediction.Begin())
        {
            contactSent = collisionAttack.Attack(player.Player, slot, true, out terrain, out entities,
                ItemStackMeleeWeaponStats.FromItemStack(slot.Itemstack), NativeDirection(fighter.Direction), true);
        }
        if (combat.Controls is { HitDiagnosticsEnabled: true } controls)
        {
            // Capture the exact samples used above. Rendering never prepares or transforms
            // the live collider a second time, which would destroy its sweep history.
            debugColliders.Clear(); debugWeapon = slot.Itemstack; debugUntil = now + 350;
            foreach (var type in collisionAttack.DamageTypes)
                debugColliders.Add((type.InWorldCollider, type.PreviousInWorldCollider, type.Radius));
            bool terrainHit = terrain.Any();
            string blockedDetail = "";
            if (terrainHit && !contactSent && !debugTerrainBlocked)
            {
                collisionAttack.TryCollideWithTerrain(out _, out double maximumParameter);
                var client = (ICoreClientAPI)api;
                float range = client.ModLoader.GetModSystem<CombatOverhaulSystem>().Settings.CollisionRadius + collisionAttack.MaxReach;
                foreach (var candidate in api.World.GetEntitiesAround(player.Pos.XYZ, range, range))
                {
                    if (!candidate.Alive || !candidate.IsCreature || candidate.EntityId == player.EntityId
                        || candidate.EntityId == player.MountedOn?.Entity?.EntityId) continue;
                    foreach (var type in collisionAttack.DamageTypes)
                    {
                        if (!type.Collide(candidate, out string collider, out _, out double parameter, out _)
                            || parameter <= maximumParameter) continue;
                        debugTerrainBlocked = true;
                        blockedDetail = FormattableString.Invariant($"target={candidate.Code} id={candidate.EntityId} collider={collider} targetParameter={parameter:F4} terrainParameter={maximumParameter:F4}");
                        break;
                    }
                    if (debugTerrainBlocked) break;
                }
            }
            var terrainContact = terrain.FirstOrDefault();
            string terrainDetail = terrainHit ? FormattableString.Invariant($"block={terrainContact.block.Code} point=({terrainContact.point.X:F3},{terrainContact.point.Y:F3},{terrainContact.point.Z:F3})") : "";
            controls.RecordHitFrame(terrainHit, entities.Any(), debugTerrainBlocked, blockedDetail, terrainDetail);
            if (contactSent)
            {
                var packet = collisionAttack.LastDamagePackets.First();
                controls.RecordHitSent(FormattableString.Invariant($"target={api.World.GetEntityById(packet.TargetEntityId)?.Code} id={packet.TargetEntityId} collider={packet.Collider} position=({packet.Position[0]:F3},{packet.Position[1]:F3},{packet.Position[2]:F3})"));
            }
        }
        if (contactSent) fighter.TakeHit(now);
    }

    public void RenderDiagnostics(EntityPlayer player, long now)
    {
        if (api is not ICoreClientAPI client || now > debugUntil || !ReferenceEquals(debugWeapon, player.RightHandItemSlot.Itemstack)) return;
        int currentColor = debugTerrainBlocked ? ColorUtil.ColorFromRgba(255, 100, 60, 255) : ColorUtil.ColorFromRgba(80, 255, 110, 255);
        int previousColor = ColorUtil.ColorFromRgba(95, 160, 255, 180);
        foreach (var (current, previous, radius) in debugColliders)
        {
            current.Render(client, player, currentColor);
            previous.Render(client, player, previousColor);
            new LineSegmentCollider(previous.Position, current.Position - previous.Position).Render(client, player, previousColor);
            new LineSegmentCollider(previous.Position + previous.Direction,
                current.Position + current.Direction - previous.Position - previous.Direction).Render(client, player, previousColor);
            RenderRadius(client, player, current, radius, currentColor);
        }
    }

    private static void RenderRadius(ICoreClientAPI client, EntityPlayer player, LineSegmentCollider segment, float radius, int color)
    {
        if (segment.Direction.LengthSquared < 1e-10 || radius <= 0) return;
        var axis = segment.Direction.Normalized();
        var side = Vector3d.Cross(axis, Math.Abs(axis.Y) < .9 ? Vector3d.UnitY : Vector3d.UnitX).Normalized();
        var up = Vector3d.Cross(axis, side);
        const int sides = 12;
        // Circular end caps and four rails expose the configured radius. Blue is
        // the previous tested sample; the connecting lines illustrate the sweep.
        for (int i = 0; i < sides; i++)
        {
            double a = i * Math.PI * 2 / sides, b = (i + 1) * Math.PI * 2 / sides;
            var offset = (side * Math.Cos(a) + up * Math.Sin(a)) * radius;
            var next = (side * Math.Cos(b) + up * Math.Sin(b)) * radius;
            foreach (var end in new[] { segment.Position, segment.Position + segment.Direction })
                new LineSegmentCollider(end + offset, next - offset).Render(client, player, color);
            if (i % 3 == 0)
                new LineSegmentCollider(segment.Position + offset, segment.Direction).Render(client, player, color);
        }
        // Two perpendicular semicircles at each end complete the capsule caps.
        foreach (int sign in new[] { -1, 1 })
        {
            var end = sign < 0 ? segment.Position : segment.Position + segment.Direction;
            foreach (var normal in new[] { side, up })
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 8, b = (i + 1) * Math.PI / 8;
                var from = end + radius * (normal * Math.Cos(a) + axis * Math.Sin(a) * sign);
                var to = end + radius * (normal * Math.Cos(b) + axis * Math.Sin(b) * sign);
                new LineSegmentCollider(from, to - from).Render(client, player, color);
            }
        }
    }

    public void Dispose()
    {
        if (api is ICoreServerAPI) api.Event.UnregisterGameTickListener(contactTrackingTick);
        contactBounds?.Clear();
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
