using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using HarmonyLib;

namespace WarbandCombat;

public sealed class CombatSystem : ModSystem
{
    internal ICoreClientAPI? Client;
    internal CombatClient? Controls;
    internal ICombatIntegration? Integration;
    private ICoreServerAPI? server;
    private IServerNetworkChannel? channel;
    private long serverTick;
    private Harmony? movementPatch;
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
    }

    public override double ExecuteOrder() => 1.1;

    public override void Start(ICoreAPI api)
    {
        api.Network.RegisterChannel("warbandcombat").RegisterMessageType<InputPacket>().RegisterMessageType<StatePacket>();
        movementPatch = new Harmony("warbandcombat.movement." + api.Side);
        CombatMovement.Patch(movementPatch);
    }

    public override void AssetsFinalize(ICoreAPI api) => Integration?.AssetsReady();

    public override void StartClientSide(ICoreClientAPI api)
    {
        Client = api;
        Integration = OverhaulCompat.Create(api);
        Controls = new CombatClient(api);
        api.Logger.Notification("[Warband Combat] Client ready. K toggles combat; Shift uses vanilla interactions.");
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        server = api;
        Integration = OverhaulCompat.Create(api);
        channel = api.Network.GetChannel("warbandcombat").SetMessageHandler<InputPacket>(OnInput);
        api.Event.PlayerDisconnect += OnDisconnect;
        api.Event.OnEntitySpawn += AttachGuard;
        api.Event.OnEntityLoaded += AttachGuard;
        api.Event.PlayerNowPlaying += OnPlayerReady;
        serverTick = api.Event.RegisterGameTickListener(Tick, 20);
        api.Logger.Notification("[Warband Combat] Server ready. Directional damage validation enabled.");
    }

    private bool ForcePvpCombat => server?.Server.Config.AllowPvP == true || Integration != null;

    private void OnPlayerReady(IServerPlayer player)
    {
        AttachGuard(player.Entity);
        var session = GetSession(player);
        CombatMovement.Bind(player.Entity, session.Fighter);
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

    internal static WeaponTiming Timing(CollectibleObject item) => OverhaulCompat.IsMelee(item)
        ? WeaponTiming.Overhaul(item.Code?.Path ?? "") : item.Tool switch
    {
        EnumTool.Spear => new(360, 140, 420),
        EnumTool.Axe => new(420, 170, 480),
        EnumTool.Knife => new(180, 100, 260),
        _ when item.Code?.Path.Contains("pike") == true => new(360, 140, 420),
        _ when item.Code?.Path.Contains("great") == true => new(380, 170, 450),
        _ when item.Code?.Path.Contains("mace") == true || item.Code?.Path.Contains("hammer") == true => new(420, 170, 480),
        _ => WeaponTiming.Sword
    };

    private Session GetSession(IServerPlayer player)
    {
        if (!sessions.TryGetValue(player.PlayerUID, out var session))
            sessions[player.PlayerUID] = session = new Session();
        return session;
    }

    private void OnInput(IServerPlayer player, InputPacket packet)
    {
        if (server == null || !Enum.IsDefined((Command)packet.Command) || !Enum.IsDefined((Direction)packet.Direction)) return;
        long now = server.World.ElapsedMilliseconds;
        Session session = GetSession(player);
        if (packet.Sequence <= session.Sequence) return;
        session.Sequence = packet.Sequence;
        session.ClientSentAt = packet.SentAt;
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
            entity.WatchedAttributes.SetString("warbandWeaponMode", packet.WeaponMode);
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
        session.Fighter.Apply(command, (Direction)packet.Direction, now, slot.Itemstack != null ? Timing(slot.Itemstack.Collectible) : WeaponTiming.Sword);
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
            var slot = entity.RightHandItemSlot;
            Integration?.ClearNativeBlock(entity);
            if (!entity.Alive || entity.Controls.Sneak && !ForcePvpCombat || !ReferenceEquals(session.Weapon, slot.Itemstack)
                || session.Slot != player.InventoryManager.ActiveHotbarSlotNumber
                || !ReferenceEquals(session.Offhand, entity.LeftHandItemSlot.Itemstack))
                session.Fighter.Invalidate(now);
            session.Fighter.SetShield(CombatShields.Equipped(entity), now);
            bool windowExpired = session.Fighter.Phase == Phase.Swing && now > session.Fighter.ContactEnds;
            session.Fighter.Advance(now);
            CombatMovement.Bind(entity, session.Fighter);
            CombatMovement.Enforce(entity, session.Fighter, now);
            if (windowExpired) session.Feedback = "warbandcombat:miss";
            CombatAnimations.Update(entity, session.Fighter, now, integration: Integration);
            if (session.Fighter.CanHit(now)
                && !(slot.Itemstack != null && Integration?.UsesPhysicalContacts(slot.Itemstack.Collectible) == true))
                Strike(player, session, now);
            // Resolve a hit immediately on the client instead of waiting for the periodic sync.
            if (windowExpired || !string.IsNullOrEmpty(session.Feedback)) SendState(player, session, now);
            if (now - session.LastSync >= 150) SendState(player, session, now);
        }
    }

    private void Strike(IServerPlayer player, Session session, long now)
    {
        if (server == null || session.Weapon == null) return;
        var attacker = player.Entity;
        var item = session.Weapon.Collectible;
        float reach = Math.Clamp(item.GetAttackRange(session.Weapon), 1.5f, 4.5f);
        var origin = attacker.Pos.XYZ.Add(attacker.LocalEyePos);
        EntitySelection? selected = null;
        // A small sweep gives slashes a wider contact area; thrusts need accurate aim.
        float[] offsets = session.Fighter.Direction is Direction.Left or Direction.Right
            ? new[] { 0f, -.10f, .10f, -.20f, .20f } : new[] { 0f };
        foreach (float offset in offsets)
        {
            var view = EntityPos.GetViewVector(attacker.Pos.Pitch, attacker.Pos.Yaw + offset);
            var end = origin.AddCopy(view.X * reach, view.Y * reach, view.Z * reach);
            BlockSelection? block = null;
            EntitySelection? entity = null;
            server.World.RayTraceForSelection(origin, end, ref block, ref entity,
                (pos, candidate) => candidate.GetCollisionBoxes(server.World.BlockAccessor, pos)?.Length > 0,
                candidate => candidate is EntityAgent && candidate.EntityId != attacker.EntityId
                    && candidate.Alive && candidate.Pos.Dimension == attacker.Pos.Dimension);
            // Vintage Story already rejects an entity behind the first blocking surface.
            if (entity != null) { selected = entity; break; }
        }
        // Empty space is a probe, not a spent attack: later ticks use the new view direction.
        if (selected == null) return;
        var target = selected.Entity;
        if (target is EntityPlayer && (!server.Server.Config.AllowPvP || !player.HasPrivilege("attackplayers"))) return;
        if (target is not EntityPlayer && !player.HasPrivilege("attackcreatures")) return;
        // Consume before ReceiveDamage: a parry can extend this recovery, and
        // invulnerable targets must not allow this swing to hit a second entity.
        if (!session.Fighter.TakeHit(now)) return;
        var direction = session.Fighter.Direction;
        // A quick click still waits the full windup, so it deserves the same
        // damage as holding through that identical preparation time.
        float multiplier = 1;
        if (direction == Direction.Overhead) multiplier *= 1.12f;
        if (direction == Direction.Thrust && item.Tool == EnumTool.Spear) multiplier *= 1.15f;
        var damage = new DirectionalDamage
        {
            Direction = direction, Source = EnumDamageSource.Player, SourceEntity = attacker,
            Type = direction == Direction.Thrust ? EnumDamageType.PiercingAttack : EnumDamageType.SlashingAttack,
            DamageTier = item.GetToolTier(attacker.RightHandItemSlot),
            HitPosition = selected.HitPosition.Clone(),
            KnockbackStrength = .6f
        };
        bool hit = target.ReceiveDamage(damage, item.GetAttackPower(session.Weapon) * multiplier);
        // Consume normal attack durability for both hits and successful parries.
        item.OnAttackingWith(server.World, attacker, target, attacker.RightHandItemSlot);
        if (hit && !damage.Blocked && target is EntityAgent victim) attacker.DidAttack(damage, victim);
        if (!damage.Blocked) session.Feedback = hit ? "warbandcombat:hit" : "warbandcombat:miss";
    }

    internal void Guard(EntityPlayer defender, DamageSource source, ref float damage)
    {
        IWarbandDamage? directedDamage = source as IWarbandDamage ?? Integration?.GetValidatedDamage(source);
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
        session.Feedback = shield ? "warbandcombat:shield-block" : "warbandcombat:parry";
        if (directedDamage != null) directedDamage.Blocked = true;
        if (attacker is EntityPlayer enemy && enemy.Player is IServerPlayer enemyPlayer
            && sessions.TryGetValue(enemyPlayer.PlayerUID, out var enemySession))
        {
            enemySession.Fighter.Stun(now, shield ? CombatRules.ShieldBounce : CombatRules.ParryStun);
            enemySession.Feedback = "warbandcombat:blocked";
            SendState(enemyPlayer, enemySession, now);
        }
        var guardingSlot = shield ? defender.LeftHandItemSlot : defender.RightHandItemSlot;
        guardingSlot.Itemstack?.Collectible.DamageItem(server.World, defender, guardingSlot,
            shield ? CombatRules.ShieldWear(blockedDamage) : 1);
        if (shield && !CombatShields.Equipped(defender))
        {
            fighter.SetShield(false, now); fighter.Stun(now, CombatRules.ShieldBreak);
            session.Feedback = "warbandcombat:shield-break";
        }
        SendState(player, session, now);
    }

    private void SendState(IServerPlayer player, Session session, long now)
    {
        var f = session.Fighter;
        channel?.SendPacket(new StatePacket
        {
            Phase = (int)f.Phase, Direction = (int)f.Direction, Sequence = session.Sequence,
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
        player.Entity.WatchedAttributes.SetInt("warbandDirection", (int)f.Direction);
        player.Entity.WatchedAttributes.SetInt("warbandPhase", (int)f.Phase);
        CombatAnimations.Update(player.Entity, f, now, integration: Integration);
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

    internal Direction PhysicalDirection(IServerPlayer player) => GetSession(player).Fighter.Direction;

    internal bool AcceptPhysicalContact(IServerPlayer player, Entity target)
    {
        if (server == null || !CanAcceptPhysicalContact(player) || !target.Alive
            || target.EntityId == player.Entity.EntityId || target.Pos.Dimension != player.Entity.Pos.Dimension) return false;
        if (target is EntityPlayer && (!server.Server.Config.AllowPvP || !player.HasPrivilege("attackplayers"))) return false;
        if (target is not EntityPlayer && !player.HasPrivilege("attackcreatures")) return false;
        return GetSession(player).Fighter.TakeHit(server.World.ElapsedMilliseconds);
    }

    internal void PhysicalFeedback(IServerPlayer player, bool blocked, bool hit)
    {
        if (server == null) return;
        var session = GetSession(player);
        if (!blocked) session.Feedback = hit ? "warbandcombat:hit" : "warbandcombat:miss";
        SendState(player, session, server.World.ElapsedMilliseconds);
    }

    public override void Dispose()
    {
        Controls?.Dispose();
        Integration?.Dispose();
        if (movementPatch != null) movementPatch.UnpatchAll(movementPatch.Id);
        if (server != null)
        {
            server.Event.UnregisterGameTickListener(serverTick);
            server.Event.PlayerDisconnect -= OnDisconnect;
            server.Event.PlayerNowPlaying -= OnPlayerReady;
            server.Event.OnEntitySpawn -= AttachGuard;
            server.Event.OnEntityLoaded -= AttachGuard;
        }
        sessions.Clear();
    }
}

internal sealed class DirectionalDamage : DamageSource, IWarbandDamage
{
    public Direction Direction;
    public Direction AttackDirection => Direction;
    public bool Blocked { get; set; }
}

internal sealed class GuardBehavior(Entity entity, CombatSystem system) : EntityBehavior(entity)
{
    public override string PropertyName() => "warbandguard";
    public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
        => system.Guard((EntityPlayer)entity, damageSource, ref damage);
}
