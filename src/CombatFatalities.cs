using System.Runtime.CompilerServices;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace WarbandCombat;

public sealed class FatalityConfig
{
    public bool Enabled = true;
    public bool DeathCamera = true;
    public bool BloodParticles = true;
    public bool BloodStains = true;
    public bool FleshSounds = true;
    public float HelmetDurabilityLoss = .35f;
}

internal sealed class CombatFatalities : IDisposable
{
    private readonly ICoreAPI api;
    private readonly ICoreServerAPI? server;
    private readonly IServerNetworkChannel? channel;
    private readonly FatalityConfig config;
    private readonly FatalityRenderer? renderer;
    private readonly ConditionalWeakTable<DamageSource, Contact> contacts = new();
    private readonly Dictionary<long, Effect> effects = new();
    private readonly long tick;
    private long sequence, lastFrame;
    private sealed record Contact(EntityAgent Target, FatalityKind Kind, Direction Direction,
        ItemSlot? HelmetSlot, ItemStack? Helmet, string HelmetPrefix, double Yaw, Vector3d Point);
    private sealed record Effect(FatalityPacket Packet, long Created, FatalityBody[] Bodies)
    {
        internal readonly HashSet<string> Viewers = new();
    }

    internal CombatFatalities(ICoreAPI api)
    {
        this.api = api;
        config = api.LoadModConfig<FatalityConfig>("warband-fatalities.json") ?? new();
        if (!float.IsFinite(config.HelmetDurabilityLoss)) config.HelmetDurabilityLoss = .35f;
        config.HelmetDurabilityLoss = Math.Clamp(config.HelmetDurabilityLoss, 0, 1);
        if (api is ICoreServerAPI sapi)
        {
            server = sapi; channel = sapi.Network.GetChannel("warbandcombat");
            sapi.StoreModConfig(config, "warband-fatalities.json");
            sapi.Event.PlayerNowPlaying += OnJoin;
            tick = sapi.Event.RegisterGameTickListener(Tick, 20);
        }
        else if (api is ICoreClientAPI capi)
        {
            renderer = new FatalityRenderer(capi, config.DeathCamera, config.BloodParticles, config.FleshSounds, config.BloodStains);
            capi.Network.GetChannel("warbandcombat").SetMessageHandler<FatalityPacket>(renderer.Receive)
                .SetMessageHandler<FatalityFrame>(renderer.Receive);
        }
    }

    internal static bool Eligible(Entity entity) => entity is EntityPlayer or TrainingDummy;

    internal void RecordContact(Entity target, DamageSource source, Direction direction, Vector3d point, string? element)
    {
        if (server == null || !config.Enabled || target is not EntityAgent victim || !Eligible(victim) || !victim.Alive
            || source.SourceEntity is not EntityPlayer attacker) return;
        var origin = new Vector3d(victim.Pos.X, victim.Pos.InternalY, victim.Pos.Z);
        var eye = new Vector3d(victim.LocalEyePos.X, victim.LocalEyePos.Y, victim.LocalEyePos.Z);
        bool head = FatalityRules.HeadElement(element) && FatalityRules.HeadPoint(point - origin, eye, victim.Properties.Client?.Size ?? 1);
        var inventory = victim is EntityPlayer player ? player.Player?.InventoryManager.GetOwnInventory("character")
            : victim.GetBehavior<EntityBehaviorContainer>()?.Inventory;
        var slot = inventory != null && inventory.Count > (int)EnumCharacterDressType.ArmorHead
            ? inventory[(int)EnumCharacterDressType.ArmorHead] : null;
        var helmet = slot?.Itemstack;
        var kind = FatalityRules.Choose(head, source.Type, direction);
        if (kind == FatalityKind.None) return;
        string prefix = helmet == null ? "" : IAttachableToEntity.FromCollectible(helmet.Collectible)?.GetTexturePrefixCode(helmet) ?? "";
        contacts.Remove(source); contacts.Add(source, new(victim, kind, direction, slot, helmet, prefix, attacker.Pos.Yaw, point));
    }

    internal void OnDeath(EntityAgent victim, DamageSource source)
    {
        if (server == null || !config.Enabled || victim.Alive || source == null || !contacts.TryGetValue(source, out var contact)
            || !ReferenceEquals(contact.Target, victim) || source is IWarbandDamage { Blocked: true }
            || effects.Values.Any(e => e.Packet.Victim == victim.EntityId && e.Created == api.World.ElapsedMilliseconds)) return;
        contacts.Remove(source);
        long now = api.World.ElapsedMilliseconds;
        victim.WatchedAttributes.SetInt(FatalityRules.Attribute, (int)contact.Kind);
        victim.WatchedAttributes.SetString("warbandCrushedHelmet", contact.HelmetPrefix);
        if (contact.Kind == FatalityKind.HeadCrush && contact.Helmet != null && contact.HelmetSlot != null
            && ReferenceEquals(contact.HelmetSlot.Itemstack, contact.Helmet) && config.HelmetDurabilityLoss > 0)
        {
            int wear = (int)Math.Ceiling(contact.Helmet.Collectible.GetMaxDurability(contact.Helmet) * config.HelmetDurabilityLoss);
            if (wear > 0) contact.Helmet.Collectible.DamageItem(api.World, victim, contact.HelmetSlot, wear);
        }
        var position = new Vector3d(victim.Pos.X + victim.LocalEyePos.X,
            victim.Pos.InternalY + victim.LocalEyePos.Y, victim.Pos.Z + victim.LocalEyePos.Z);
        var impulse = FatalityRules.Impulse(contact.Direction, contact.Yaw, contact.Kind == FatalityKind.HeadCrush);
        int count = contact.Kind == FatalityKind.Decapitation ? 1 : contact.HelmetPrefix.Length > 0 ? 3 : 0;
        var bodies = Enumerable.Range(0, count).Select(i => new FatalityBody(position + new Vector3d(i * .025, .08, 0),
            impulse + new Vector3d((count == 1 ? 0 : i - 1) * .8, i * .35, (count == 1 ? 0 : i - 1) * .4),
            contact.Kind == FatalityKind.Decapitation ? FatalityRules.HeadRadius : FatalityRules.FragmentRadius)).ToArray();
        var packet = new FatalityPacket { Id = ++sequence, Victim = victim.EntityId, Kind = contact.Kind,
            Position = [position.X, position.Y, position.Z], Dimension = victim.Pos.Dimension, HelmetPrefix = contact.HelmetPrefix,
            Impulse = [impulse.X, impulse.Y, impulse.Z], SprayPosition = [contact.Point.X, contact.Point.Y, contact.Point.Z] };
        if (effects.Count >= FatalityRules.MaxEffects) effects.Remove(effects.MinBy(e => e.Value.Created).Key);
        var effect = new Effect(packet, now, bodies); effects.Add(packet.Id, effect);
        foreach (var player in Nearby(packet)) SendEffect(effect, player, now);
        SendFrames();
        api.Logger.Notification("[Warband Fatality] victim={0} kind={1} direction={2} headContact=true", victim is EntityPlayer real ? real.PlayerUID : victim.EntityId.ToString(), contact.Kind, contact.Direction);
    }

    internal void Reset(EntityAgent victim)
    {
        if (server != null)
        {
            victim.WatchedAttributes.SetInt(FatalityRules.Attribute, 0);
            victim.WatchedAttributes.SetString("warbandCrushedHelmet", "");
        }
    }

    private IEnumerable<IServerPlayer> Nearby(FatalityPacket packet)
        => server!.World.AllOnlinePlayers.OfType<IServerPlayer>().Where(p => p.Entity.Pos.Dimension == packet.Dimension
            && (p.Entity.EntityId == packet.Victim || p.Entity.Pos.SquareDistanceTo(packet.Position[0], packet.Position[1], packet.Position[2]) < 128 * 128));

    private void OnJoin(IServerPlayer player)
    {
        if (player.Entity.Alive) Reset(player.Entity);
        long now = api.World.ElapsedMilliseconds;
        foreach (var effect in effects.Values)
        {
            if (!Nearby(effect.Packet).Contains(player)) continue;
            SendEffect(effect, player, now);
        }
        SendFrames();
    }

    private void SendEffect(Effect effect, IServerPlayer player, long now)
    {
        var original = effect.Packet;
        channel!.SendPacket(new FatalityPacket { Id = original.Id, Victim = original.Victim, Kind = original.Kind,
            Dimension = original.Dimension, Position = original.Position, HelmetPrefix = original.HelmetPrefix,
            AgeMs = (int)(now - effect.Created), Impulse = original.Impulse, SprayPosition = original.SprayPosition }, player);
        effect.Viewers.Add(player.PlayerUID);
    }

    private void Tick(float dt)
    {
        long now = api.World.ElapsedMilliseconds;
        foreach (var (id, effect) in effects.ToArray())
        {
            if (now - effect.Created > FatalityRules.LifetimeMs) { effects.Remove(id); continue; }
            foreach (var body in effect.Bodies) body.Advance(dt, (point, radius) => Solid(point, radius, effect.Packet.Dimension));
        }
        if (effects.Count > 0 && now - lastFrame >= 100) { lastFrame = now; SendFrames(); }
    }

    private bool Solid(Vector3d point, double radius, int dimension)
    {
        var accessor = api.World.BlockAccessor;
        for (int x = (int)Math.Floor(point.X - radius); x <= (int)Math.Floor(point.X + radius); x++)
        for (int y = (int)Math.Floor(point.Y - radius); y <= (int)Math.Floor(point.Y + radius); y++)
        for (int z = (int)Math.Floor(point.Z - radius); z <= (int)Math.Floor(point.Z + radius); z++)
        {
            var pos = new BlockPos(x, y, z); if (pos.dimension != dimension) return true;
            var boxes = accessor.GetBlock(pos, BlockLayersAccess.MostSolid).GetCollisionBoxes(accessor, pos);
            if (boxes == null) continue;
            foreach (var box in boxes)
                if (point.X + radius > x + box.X1 && point.X - radius < x + box.X2
                    && point.Y + radius > y + box.Y1 && point.Y - radius < y + box.Y2
                    && point.Z + radius > z + box.Z1 && point.Z - radius < z + box.Z2) return true;
        }
        return false;
    }

    private void SendFrames()
    {
        if (server == null) return;
        foreach (var player in server.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            var visible = effects.Values.Where(e => Nearby(e.Packet).Contains(player)).ToArray();
            foreach (var effect in visible)
                if (!effect.Viewers.Contains(player.PlayerUID)) SendEffect(effect, player, api.World.ElapsedMilliseconds);
            var bodies = visible.SelectMany(e => e.Bodies.Select((b, i) =>
                new FatalityMotion { Id = e.Packet.Id, Part = i, Position = [b.Position.X, b.Position.Y, b.Position.Z],
                    Rotation = [b.Rotation.X, b.Rotation.Y, b.Rotation.Z], Impact = b.Impact, ImpactSpeed = b.ImpactSpeed,
                    ImpactPosition = b.Impact == 0 ? [] : [b.ImpactPosition.X, b.ImpactPosition.Y, b.ImpactPosition.Z] })).ToArray();
            if (bodies.Length > 0) channel!.SendPacket(new FatalityFrame { Bodies = bodies }, player);
        }
    }

    public void Dispose()
    {
        renderer?.Dispose();
        if (server != null) { server.Event.UnregisterGameTickListener(tick); server.Event.PlayerNowPlaying -= OnJoin; }
        effects.Clear(); contacts.Clear();
    }
}
