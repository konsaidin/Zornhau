using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using HarmonyLib;

namespace Zornhau;

public sealed class CombatSystem : ModSystem
{
    internal ICoreClientAPI? Client;
    internal CombatClient? Controls;
    internal ICombatIntegration? Integration;
    internal StandalonePhysics? Physics;
    internal StandaloneAnimations? NativeAnimations;
    internal CombatFatalities? Fatalities;
    private ICoreServerAPI? server;
    private IServerNetworkChannel? channel;
    private long serverTick;
    private TrainingDummies? dummies;
    private Harmony? movementPatch;
    private long contactSequence;
    private readonly Dictionary<string, Session> sessions = new();

    private sealed class Session
    {
        public Fighter Fighter = new();
        public ItemStack? Weapon;
        public ItemStack? Offhand;
        public int Slot;
        public int Sequence;
        public long LastSync;
        public long LastPacket;
        public int PacketCount;
        public string Feedback = "";
        public string WeaponMode = "";
        public long ClientSentAt, InputReceivedAt;
        public long SwingSoundAt;
        public int AttackId;
        public bool HitDiagnostics;
        public int PoseRevision;
        public long DiagnosticWindow;
        public int DiagnosticCount;
        public long StateRevision;
        public bool AuthoritativeClock;
        public Action? PendingContact;
        public int PendingAttack;
        public long ContactDue;
        public long InterruptedAt = long.MinValue;
    }

    public override double ExecuteOrder() => 1.1;

    public override void Start(ICoreAPI api)
    {
        api.RegisterEntity(TrainingDummy.ClassName, typeof(TrainingDummy));
        api.Network.RegisterChannel("zornhau").RegisterMessageType<InputPacket>().RegisterMessageType<StatePacket>()
            .RegisterMessageType<ContactPacket>().RegisterMessageType<DiagnosticsSettingsPacket>().RegisterMessageType<HitDiagnosticPacket>()
            .RegisterMessageType<BladeContactPacket>().RegisterMessageType<FatalityPacket>().RegisterMessageType<FatalityFrame>()
            .RegisterMessageType<OverhaulContactPacket>().RegisterMessageType<UseInterruptPacket>();
        movementPatch = new Harmony("zornhau.movement." + api.Side);
        CombatMovement.Patch(movementPatch);
        CombatUseInterrupt.Patch(movementPatch);
        CombatShieldProtection.Patch(movementPatch);
    }

    public override void AssetsFinalize(ICoreAPI api)
    { TrainingDummy.Register(api); CombatBalance.Load(api); Integration?.AssetsReady(); NativeAnimations?.Load(); Physics?.AssetsReady(); }

    public override void StartClientSide(ICoreClientAPI api)
    {
        Client = api;
        Fatalities = new CombatFatalities(api);
        if (movementPatch != null)
        {
            CombatPrediction.Patch(movementPatch);
            ImmersiveView.Patch(movementPatch);
        }
        Integration = OverhaulCompat.Create(api);
        NativeAnimations = new(api, this);
        Physics = new StandalonePhysics(api, this);
        Controls = new CombatClient(api);
        api.Logger.Notification("[Zornhau] Client ready. K toggles combat; Shift uses vanilla interactions.");
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        server = api;
        if (movementPatch != null) CombatTurnAuthority.Patch(movementPatch);
        CombatTurnAuthority.Rejected = OnTurnRejected;
        CombatUseInterrupt.ServerHit = InterruptVictim;
        Fatalities = new CombatFatalities(api);
        Integration = OverhaulCompat.Create(api);
        NativeAnimations = new(api, this);
        channel = api.Network.GetChannel("zornhau").SetMessageHandler<InputPacket>(OnInput)
            .SetMessageHandler<DiagnosticsSettingsPacket>((player, packet) => GetSession(player).HitDiagnostics = packet.Enabled);
        Physics = new StandalonePhysics(api, this);
        channel.SetMessageHandler<BladeContactPacket>(Physics.OnContact);
        dummies = new TrainingDummies(api, Fatalities);
        api.Event.PlayerDisconnect += OnDisconnect;
        api.Event.OnEntitySpawn += AttachGuard;
        api.Event.OnEntityLoaded += AttachGuard;
        api.Event.PlayerNowPlaying += OnPlayerReady;
        serverTick = api.Event.RegisterGameTickListener(Tick, 20);
        api.Logger.Notification("[Zornhau] Server ready. Directional damage validation enabled.");
    }

    private bool ForcePvpCombat => server?.Server.Config.AllowPvP == true || Integration != null;

    private void OnPlayerReady(IServerPlayer player)
    {
        AttachGuard(player.Entity);
        var session = GetSession(player);
        CombatMovement.Bind(player.Entity, session.Fighter);
        CombatTurnAuthority.Bind(player.Entity, session.Fighter);
        if (server != null) SendState(player, session, server.World.ElapsedMilliseconds);
    }
    private void OnDisconnect(IServerPlayer player) => sessions.Remove(player.PlayerUID);

    private void AttachGuard(Entity entity)
    {
        if (entity is not EntityPlayer || entity.SidedProperties.Behaviors.Any(b => b is GuardBehavior)) return;
        // Runs before health and armour behaviors consume the damage value.
        entity.SidedProperties.Behaviors.Insert(0, new GuardBehavior(entity, this));
        entity.CacheServerBehaviors();
    }

    internal static bool Supported(CollectibleObject? item) => item?.Tool is
        EnumTool.Sword or EnumTool.Spear or EnumTool.Axe or EnumTool.Knife || OverhaulCompat.IsMelee(item);

    internal static WeaponTiming Timing(EntityPlayer player, string? mode = null)
        => CombatBalance.For(player, mode)?.Timing
            ?? (player.RightHandItemSlot.Itemstack is { } stack ? Timing(stack.Collectible) : WeaponTiming.Sword);

    internal static WeaponTiming Timing(CollectibleObject item) => CombatBalance.DefaultTiming(item) ?? WeaponTiming.Overhaul(
        OverhaulCompat.IsMelee(item) ? item.Code?.Path ?? "" : item.Tool switch
        {
            EnumTool.Spear => "spear",
            EnumTool.Axe => "axe",
            EnumTool.Knife => "knife",
            _ => (item.Code?.Path ?? "").Replace("claymore", "great", StringComparison.Ordinal)
        });

    private Session GetSession(IServerPlayer player)
    {
        if (!sessions.TryGetValue(player.PlayerUID, out var session))
            sessions[player.PlayerUID] = session = new Session();
        CombatTurnAuthority.Bind(player.Entity, session.Fighter);
        return session;
    }

    private void OnTurnRejected(EntityPlayer entity)
    {
        if (entity.Player is not IServerPlayer player || !sessions.TryGetValue(player.PlayerUID, out var session)) return;
        session.PendingContact = null;
        session.AuthoritativeClock = true;
        ReportHitDiagnostic(player, session.AttackId, 0, "turn-limit", "Strike rotation exceeds the server angular budget");
        if (server != null) SendState(player, session, server.World.ElapsedMilliseconds);
    }

    internal bool CheckTurnProof(IServerPlayer player, float[]? pose)
        => CombatTurnAuthority.Proof(player.Entity, pose);

    private void OnInput(IServerPlayer player, InputPacket packet)
    {
        if (server == null || !Enum.IsDefined((Command)packet.Command) || !Enum.IsDefined((Direction)packet.Direction)) return;
        long now = server.World.ElapsedMilliseconds;
        Session session = GetSession(player);
        if (packet.Sequence <= session.Sequence) return;
        session.Sequence = packet.Sequence;
        session.ClientSentAt = packet.SentAt;
        session.HitDiagnostics = packet.HitDiagnostics;
        session.InputReceivedAt = now;
        // Bound work per player even when a modified client sends excessive packets.
        if (now - session.LastPacket > 1000) { session.LastPacket = now; session.PacketCount = 0; }
        if (++session.PacketCount > 100) return;
        if (packet.WeaponMode == null || packet.WeaponMode.Length > 64) return;
        var entity = player.Entity;
        CombatMovement.Bind(entity, session.Fighter);
        AttachGuard(entity);
        var slot = entity.RightHandItemSlot;
        var command = (Command)packet.Command;
        if (session.WeaponMode != packet.WeaponMode)
        {
            session.Fighter.Invalidate(now);
            session.WeaponMode = packet.WeaponMode;
            entity.WatchedAttributes.SetString("zornhauWeaponMode", packet.WeaponMode);
        }
        if (!entity.Alive || !CombatShields.AllowsInput(command, Supported(slot.Itemstack?.Collectible),
            CombatShields.Equipped(entity), session.Fighter.Phase) || entity.Controls.Sneak && !ForcePvpCombat)
        {
            session.Fighter.Invalidate(now); SendState(player, session, now); return;
        }
        if (!ReferenceEquals(session.Weapon, slot.Itemstack) || session.Slot != player.InventoryManager.ActiveHotbarSlotNumber
            || !ReferenceEquals(session.Offhand, entity.LeftHandItemSlot.Itemstack))
        {
            session.Fighter.Invalidate(now);
            session.Weapon = slot.Itemstack;
            session.Offhand = entity.LeftHandItemSlot.Itemstack;
            session.Slot = player.InventoryManager.ActiveHotbarSlotNumber;
        }
        session.Fighter.SetShield(CombatShields.Equipped(entity), now);
        if (command == Command.Begin && !CombatBalance.CanAttack(entity, session.WeaponMode))
        { SendState(player, session, now); return; }
        if (session.Fighter.Apply(command, (Direction)packet.Direction, now, Timing(entity, session.WeaponMode)))
        {
            if (command == Command.Begin) session.AttackId = packet.Sequence;
            if (command is Command.Begin or Command.Block && session.Fighter.Phase != Phase.Recovery)
                session.AuthoritativeClock = false;
        }
        CombatMovement.Enforce(entity, session.Fighter, now);
        SendState(player, session, now);
    }

    private void Tick(float dt)
    {
        if (server == null) return;
        long now = server.World.ElapsedMilliseconds;
        foreach (var player in server.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (!sessions.TryGetValue(player.PlayerUID, out var session)) continue;
            var entity = player.Entity;
            CombatTurnAuthority.Observe(entity);
            var slot = entity.RightHandItemSlot;
            Integration?.ClearNativeBlock(entity);
            if (!entity.Alive || entity.Controls.Sneak && !ForcePvpCombat || !ReferenceEquals(session.Weapon, slot.Itemstack)
                || session.Slot != player.InventoryManager.ActiveHotbarSlotNumber
                || !ReferenceEquals(session.Offhand, entity.LeftHandItemSlot.Itemstack))
                session.Fighter.Invalidate(now);
            session.Fighter.SetShield(CombatShields.Equipped(entity), now);
            if (session.PendingContact is { } pending && now >= session.ContactDue)
            {
                session.PendingContact = null;
                if (session.PendingAttack == session.AttackId && CanAcceptPhysicalContact(player)) pending();
            }
            bool windowExpired = session.Fighter.Phase == Phase.Swing && now > session.Fighter.ContactEnds;
            PlaySwing(player, session, now);
            session.Fighter.Advance(now);
            CombatMovement.Bind(entity, session.Fighter);
            CombatMovement.Enforce(entity, session.Fighter, now);
            if (windowExpired) session.Feedback = "zornhau:miss";
            CombatAnimations.Update(entity, session.Fighter, now, integration: Integration, standalone: NativeAnimations);
            // Resolve a hit immediately on the client instead of waiting for the periodic sync.
            if (windowExpired || !string.IsNullOrEmpty(session.Feedback)) SendState(player, session, now);
            if (now - session.LastSync >= 150) SendState(player, session, now);
        }
    }

    internal void Guard(EntityPlayer defender, DamageSource source, ref float damage)
    {
        IZornhauDamage? directedDamage = source as IZornhauDamage ?? Integration?.GetValidatedDamage(source);
        Integration?.ClearNativeBlock(defender);
        // Every PvP melee hit must have passed our server-side state machine.
        // Projectile entities and creature attacks remain separate damage sources.
        if (CombatRules.RejectVanillaPvpMelee(ForcePvpCombat, directedDamage != null,
            source.SourceEntity is EntityPlayer,
            source.Type is EnumDamageType.SlashingAttack or EnumDamageType.PiercingAttack or EnumDamageType.BluntAttack))
        {
            damage = 0; source.KnockbackStrength = 0; return;
        }
        if (server == null || damage <= 0 || defender.Player is not IServerPlayer player
            || !sessions.TryGetValue(player.PlayerUID, out var session)) return;
        var fighter = session.Fighter;
        long now = server.World.ElapsedMilliseconds;
        fighter.Advance(now);
        if (!fighter.CanGuard(now) || !defender.Alive || defender.Controls.Sneak && !ForcePvpCombat
            || !ReferenceEquals(session.Weapon, defender.RightHandItemSlot.Itemstack)
            || !ReferenceEquals(session.Offhand, defender.LeftHandItemSlot.Itemstack)
            || session.Slot != player.InventoryManager.ActiveHotbarSlotNumber) return;
        // This first shield implementation covers melee contacts.
        if (source.Type is not (EnumDamageType.SlashingAttack or EnumDamageType.PiercingAttack or EnumDamageType.BluntAttack)
            || source.SourceEntity is not EntityAgent attacker) return;
        // Neutral PLAYER pitch is PI. Pitch 0 points behind the player's model.
        bool shield = fighter.ShieldGuard && CombatShields.Equipped(defender);
        var forward = Fighter.PlayerForward(defender.Pos.Yaw);
        if (attacker.Pos.Dimension != defender.Pos.Dimension
            || !Fighter.Faces(forward.X, forward.Z, attacker.Pos.X - defender.Pos.X,
                attacker.Pos.Z - defender.Pos.Z, shield ? .5 : .35)) return;
        var incoming = directedDamage?.AttackDirection ?? Direction.Overhead;
        if (!CombatRules.GuardCovers(shield, fighter.Direction, incoming)) return;
        float blockedDamage = damage;
        damage = 0; source.KnockbackStrength = 0;
        session.Feedback = shield ? "zornhau:shield-block" : "zornhau:parry";
        if (directedDamage != null) directedDamage.Blocked = true;
        if (attacker is EntityPlayer enemy && enemy.Player is IServerPlayer enemyPlayer
            && sessions.TryGetValue(enemyPlayer.PlayerUID, out var enemySession))
        {
            enemySession.Fighter.Stun(now, shield ? CombatRules.ShieldBounce : CombatRules.ParryStun);
            enemySession.AuthoritativeClock = true;
            enemySession.Feedback = "zornhau:blocked";
            SendState(enemyPlayer, enemySession, now);
        }
        var guardingSlot = shield ? defender.LeftHandItemSlot : defender.RightHandItemSlot;
        var guardItem = guardingSlot.Itemstack?.Collectible;
        guardingSlot.Itemstack?.Collectible.DamageItem(server.World, defender, guardingSlot,
            shield ? CombatRules.ShieldWear(blockedDamage) : 1);
        if (shield && !CombatShields.Equipped(defender))
        {
            fighter.SetShield(false, now); fighter.Stun(now, CombatRules.ShieldBreak);
            session.AuthoritativeClock = true;
            session.Feedback = "zornhau:shield-break";
        }
        EmitContact(attacker, defender, session.Feedback == "zornhau:shield-break" ? ContactKind.ShieldBreak
            : shield ? ContactKind.Shield : ContactKind.Parry, guardItem, blockedDamage >= 6);
        SendState(player, session, now);
    }

    private void PlaySwing(IServerPlayer player, Session session, long now)
    {
        var f = session.Fighter;
        if (server == null || f.Phase != Phase.Swing || now < f.StrikeStarts || session.SwingSoundAt == f.ContactEnds) return;
        session.SwingSoundAt = f.ContactEnds;
        var pos = player.Entity.Pos;
        // The owner predicts the same sound locally; only observers need this copy.
        server.World.PlaySoundAt(CombatFeedback.SwingSound, pos.X, pos.Y + 1, pos.Z, player, true, 16, .45f);
    }

    private void EmitContact(Entity attacker, Entity defender, ContactKind kind,
        CollectibleObject? guard = null, bool heavy = false)
    {
        if (server == null) return;
        var pos = defender.Pos;
        // DestroyItem already emits the break sound; do not duplicate it.
        if (kind != ContactKind.ShieldBreak)
            server.World.PlaySoundAt(CombatFeedback.ContactSound(kind, guard, heavy), pos.X,
                pos.Y + defender.LocalEyePos.Y * .65, pos.Z, null, true, 20, kind == ContactKind.Hit ? .65f : .8f);
        var packet = new ContactPacket { Id = ++contactSequence, Attacker = attacker.EntityId,
            Defender = defender.EntityId, Kind = kind };
        if ((attacker as EntityPlayer)?.Player is IServerPlayer attacking) channel?.SendPacket(packet, attacking);
        if (defender != attacker && (defender as EntityPlayer)?.Player is IServerPlayer defending)
            channel?.SendPacket(packet, defending);
    }

    private void SendState(IServerPlayer player, Session session, long now)
    {
        var f = session.Fighter;
        channel?.SendPacket(new StatePacket
        {
            Phase = (int)f.Phase, Direction = (int)f.Direction, Sequence = session.Sequence,
            Revision = ++session.StateRevision, AttackId = session.AttackId,
            ContactConsumed = f.HitDelivered, AuthoritativeClock = session.AuthoritativeClock,
            Elapsed = (int)Math.Clamp(now - f.Started, 0, 10000),
            HitIn = (int)Math.Clamp(f.HitAt - now, -10000, 10000),
            EndsIn = (int)Math.Clamp(f.Ends - now, -10000, 10000), Charge = f.Charge,
            Feedback = session.Feedback, ForcePvpCombat = ForcePvpCombat,
            RecoveryAttackPose = f.RecoveryAttackPose,
            ContactEndsIn = (int)Math.Clamp(f.ContactEnds - now, -10000, 10000),
            SprintIn = (int)Math.Clamp(f.SprintUntil - now, 0, 10000),
            EchoSentAt = session.ClientSentAt, InputReceivedAt = session.InputReceivedAt, ServerSentAt = now
        }, player);
        session.LastSync = now; session.Feedback = "";
        player.Entity.WatchedAttributes.SetInt("zornhauDirection", (int)f.Direction);
        player.Entity.WatchedAttributes.SetInt("zornhauPhase", (int)f.Phase);
        player.Entity.WatchedAttributes.SetInt("zornhauAttackId", session.AttackId);
        player.Entity.WatchedAttributes.SetInt("zornhauElapsed", (int)Math.Clamp(now - f.Started, 0, 10000));
        player.Entity.WatchedAttributes.SetInt("zornhauHitIn", (int)Math.Clamp(f.HitAt - now, -10000, 10000));
        player.Entity.WatchedAttributes.SetInt("zornhauEndsIn", (int)Math.Clamp(f.Ends - now, -10000, 10000));
        player.Entity.WatchedAttributes.SetInt("zornhauContactEndsIn", (int)Math.Clamp(f.ContactEnds - now, -10000, 10000));
        player.Entity.WatchedAttributes.SetBool("zornhauRecoveryAttackPose", f.RecoveryAttackPose);
        player.Entity.WatchedAttributes.SetInt("zornhauPoseRevision", ++session.PoseRevision);
        CombatAnimations.Update(player.Entity, f, now, integration: Integration, standalone: NativeAnimations);
    }

    internal bool CanAcceptPhysicalContact(IServerPlayer player)
    {
        if (server == null || !sessions.TryGetValue(player.PlayerUID, out var session)) return false;
        var entity = player.Entity;
        long now = server.World.ElapsedMilliseconds;
        session.Fighter.Advance(now);
        return entity.Alive && session.Fighter.CanHit(now)
            && ReferenceEquals(session.Weapon, entity.RightHandItemSlot.Itemstack)
            && ReferenceEquals(session.Offhand, entity.LeftHandItemSlot.Itemstack)
            && session.Slot == player.InventoryManager.ActiveHotbarSlotNumber;
    }

    internal bool TracksPhysicalContacts(IServerPlayer player)
        => sessions.TryGetValue(player.PlayerUID, out var session) && session.Fighter.Timing.PhysicalContacts
            && session.Fighter.Phase is Phase.Windup or Phase.Swing;

    // Hold one early proof until the required server windup finishes.
    internal bool DeferPhysicalContact(IServerPlayer player, int attackId, Action deliver)
    {
        if (server == null || !sessions.TryGetValue(player.PlayerUID, out var session)
            || attackId != session.AttackId || session.Fighter.Phase != Phase.Swing
            || session.Fighter.HitDelivered) return false;
        long delay = session.Fighter.HitAt - server.World.ElapsedMilliseconds;
        if (delay <= 0 || delay > CombatRules.ContactJitterBuffer) return false;
        if (session.PendingContact == null)
        {
            session.PendingContact = deliver; session.PendingAttack = attackId;
            session.ContactDue = session.Fighter.HitAt;
        }
        return true;
    }

    internal int DiagnosticAttackId(IServerPlayer player)
        => sessions.TryGetValue(player.PlayerUID, out var session) && session.HitDiagnostics ? session.AttackId : 0;

    internal int PhysicalAttackId(IServerPlayer player)
        => sessions.TryGetValue(player.PlayerUID, out var session) ? session.AttackId : 0;

    internal string PhysicalContactState(IServerPlayer player)
    {
        if (server == null || !sessions.TryGetValue(player.PlayerUID, out var session)) return "no-session";
        long now = server.World.ElapsedMilliseconds;
        var f = session.Fighter;
        return $"phase={f.Phase} hitIn={f.HitAt - now}ms endIn={f.ContactEnds - now}ms consumed={f.HitDelivered} "
            + $"main={ReferenceEquals(session.Weapon, player.Entity.RightHandItemSlot.Itemstack)} "
            + $"offhand={ReferenceEquals(session.Offhand, player.Entity.LeftHandItemSlot.Itemstack)} "
            + $"slot={session.Slot == player.InventoryManager.ActiveHotbarSlotNumber}";
    }

    internal void ReportHitDiagnostic(IServerPlayer player, int attackId, long target, string result, string detail)
    {
        if (server == null || channel == null || attackId <= 0 || !sessions.TryGetValue(player.PlayerUID, out var session)
            || !session.HitDiagnostics) return;
        long now = server.World.ElapsedMilliseconds;
        if (now - session.DiagnosticWindow >= 1000) { session.DiagnosticWindow = now; session.DiagnosticCount = 0; }
        if (++session.DiagnosticCount > 20) return;
        result = CleanDiagnostic(result, 80); detail = CleanDiagnostic(detail, 320);
        server.Logger.Notification("[Zornhau Hit] server player={0} attack={1} target={2} result={3} {4}",
            player.PlayerName, attackId, target, result, detail);
        channel.SendPacket(new HitDiagnosticPacket { AttackId = attackId, Target = target, Result = result, Detail = detail }, player);
    }

    private static string CleanDiagnostic(string text, int length)
        => new string(text.Take(length).Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    internal Direction PhysicalDirection(IServerPlayer player) => GetSession(player).Fighter.Direction;

    internal bool AcceptPhysicalContact(IServerPlayer player, Entity target)
    {
        if (server == null || !CanAcceptPhysicalContact(player) || !target.Alive
            || target.EntityId == player.Entity.EntityId || target.Pos.Dimension != player.Entity.Pos.Dimension) return false;
        if (target is EntityPlayer && (!server.Server.Config.AllowPvP || !player.HasPrivilege("attackplayers"))) return false;
        if (target is not EntityPlayer && !player.HasPrivilege("attackcreatures")) return false;
        var session = GetSession(player);
        PlaySwing(player, session, server.World.ElapsedMilliseconds);
        return session.Fighter.TakeHit(server.World.ElapsedMilliseconds);
    }

    internal void PhysicalFeedback(IServerPlayer player, Entity target, bool blocked, bool hit)
    {
        if (server == null) return;
        var session = GetSession(player);
        if (!blocked) session.Feedback = hit ? "zornhau:hit" : "zornhau:miss";
        if (hit && !blocked)
        {
            EmitContact(player.Entity, target, ContactKind.Hit);
            if (target is EntityPlayer { Alive: true } victim) InterruptVictim(victim);
        }
        SendState(player, session, server.World.ElapsedMilliseconds);
    }

    private void InterruptVictim(EntityPlayer victim)
    {
        if (server == null || victim.Player is not IServerPlayer defending) return;
        var defender = GetSession(defending);
        long now = server.World.ElapsedMilliseconds;
        // OnHurt and the resolved melee event can report the same contact.
        if (defender.InterruptedAt == now && defender.Fighter.Phase is not (Phase.Windup or Phase.Swing)) return;
        defender.InterruptedAt = now;
        CombatUseInterrupt.Cancel(victim);
        Integration?.InterruptUse(victim);
        channel?.SendPacket(new UseInterruptPacket { Id = ++contactSequence }, defending);
        defender.Fighter.Advance(now);
        if (defender.Fighter.Phase is not (Phase.Windup or Phase.Swing)) return;
        defender.PendingContact = null;
        defender.Fighter.Stun(now, CombatRules.HitStun);
        defender.AuthoritativeClock = true;
        SendState(defending, defender, now);
    }

    public override void Dispose()
    {
        dummies?.Dispose();
        Controls?.Dispose();
        Fatalities?.Dispose();
        Physics?.Dispose();
        NativeAnimations?.Clear();
        Integration?.Dispose();
        if (movementPatch != null) movementPatch.UnpatchAll(movementPatch.Id);
        if (server != null)
        {
            if (CombatTurnAuthority.Rejected?.Target == this) CombatTurnAuthority.Rejected = null;
            if (CombatUseInterrupt.ServerHit?.Target == this) CombatUseInterrupt.ServerHit = null;
            server.Event.UnregisterGameTickListener(serverTick);
            server.Event.PlayerDisconnect -= OnDisconnect;
            server.Event.PlayerNowPlaying -= OnPlayerReady;
            server.Event.OnEntitySpawn -= AttachGuard;
            server.Event.OnEntityLoaded -= AttachGuard;
        }
        sessions.Clear();
    }
}

internal sealed class DirectionalDamage : DamageSource, IZornhauDamage
{
    public Direction Direction;
    public Direction AttackDirection => Direction;
    public bool Blocked { get; set; }
}

internal sealed class GuardBehavior(Entity entity, CombatSystem system) : EntityBehavior(entity)
{
    public override string PropertyName() => "zornhauguard";
    public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
        => system.Guard((EntityPlayer)entity, damageSource, ref damage);
    public override void OnEntityDeath(DamageSource damageSource)
        => system.Fatalities?.OnDeath((EntityPlayer)entity, damageSource);
    public override void OnEntityRevive() => system.Fatalities?.Reset((EntityPlayer)entity);
}
