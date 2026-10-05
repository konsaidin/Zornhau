using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace WarbandCombat;

// Use the game's account-free seraph, with the same skeleton and clothing
// renderer as a player. Keep game-domain references when cloning its config.
public sealed class TrainingDummy : EntityPlayerBot
{
    internal static readonly AssetLocation TypeCode = new("game:warband-trainingdummy");
    internal const string ClassName = "WarbandTrainingDummy";
    internal long ReviveAt;
    public override bool StoreWithChunk => false;
    public override bool ShouldDespawn => DespawnReason != null && DespawnReason.Reason != EnumDespawnReason.Death;

    public override void Die(EnumDespawnReason reason = EnumDespawnReason.Death, DamageSource? damageSourceForDeath = null)
    {
        bool wasAlive = Alive;
        base.Die(reason, damageSourceForDeath);
        if (!wasAlive || reason != EnumDespawnReason.Death || World.Side != EnumAppSide.Server) return;
        Api.ModLoader.GetModSystem<CombatSystem>().Fatalities?.OnDeath(this, damageSourceForDeath!);
        ReviveAt = World.ElapsedMilliseconds + 8000;
    }

    // Equipment is disposable test gear, never loot or an inventory interface.
    public override void OnInteract(EntityAgent byEntity, ItemSlot slot, Vec3d hitPosition, EnumInteractMode mode) { }

    internal static void Register(ICoreAPI api)
    {
        var original = api.World.GetEntityType(new("game:playerbot"))
            ?? throw new InvalidOperationException("Training dummy requires the base game's playerbot");
        var properties = original.Clone();
        properties.Code = TypeCode.Clone(); properties.Class = ClassName;
        properties.CanClimb = false; properties.Drops = []; properties.FallDamage = false;
        var attributes = properties.Attributes!.Token!.DeepClone();
        attributes["inventory"] = new JArray();
        properties.Attributes = new JsonObject(attributes);
        var player = api.World.GetEntityType(new("game:player"));
        foreach (var side in new EntitySidedProperties?[] { properties.Client, properties.Server })
        {
            if (side == null) continue;
            var behaviors = side.BehaviorsAsJsonObj
                .Where(b => b["code"].AsString() is not ("collectitems" or "breathe"))
                .Select(b => new JsonObject(b.Token!.DeepClone())).ToList();
            foreach (var behavior in behaviors.Where(b => b["code"].AsString() == "seraphinventory"))
                behavior.Token!["dropContentsOnDeath"] = false;
            var source = ReferenceEquals(side, properties.Client) ? player?.Client : (EntitySidedProperties?)player?.Server;
            var colliders = source?.BehaviorsAsJsonObj.FirstOrDefault(b => b["code"].AsString() == "CombatOverhaul:EntityColliders");
            if (colliders != null && behaviors.All(b => b["code"].AsString() != "CombatOverhaul:EntityColliders"))
                behaviors.Add(new JsonObject(colliders.Token!.DeepClone()));
            side.BehaviorsAsJsonObj = behaviors.ToArray();
        }
        api.RegisterEntityClass(ClassName, properties);
    }
}

internal sealed class TrainingDummies : IDisposable
{
    private readonly ICoreServerAPI api;
    private readonly CombatFatalities fatalities;
    private readonly Dictionary<string, TrainingDummy> owned = new();
    private readonly long tick;
    internal TrainingDummies(ICoreServerAPI api, CombatFatalities fatalities)
    {
        this.api = api; this.fatalities = fatalities;
        api.ChatCommands.Create("wbdummy").WithDescription("Spawn a renewable combat target: normal, fatal, helmet, reset, clear")
            .RequiresPrivilege(Privilege.controlserver).RequiresPlayer()
            .WithArgs(api.ChatCommands.Parsers.OptionalWord("mode"))
            .HandleWith(args => Execute((IServerPlayer)args.Caller.Player,
                string.IsNullOrWhiteSpace(args[0] as string) ? "normal" : (string)args[0]));
        api.Event.PlayerDisconnect += Disconnect;
        tick = api.Event.RegisterGameTickListener(Tick, 250);
    }

    internal TextCommandResult Execute(IServerPlayer player, string mode)
    {
        mode = mode.ToLowerInvariant();
        if (mode is not ("normal" or "fatal" or "helmet" or "reset" or "clear"))
            return TextCommandResult.Error("/wbdummy [normal|fatal|helmet|reset|clear]");
        if (mode == "clear") { Remove(player.PlayerUID); return TextCommandResult.Success("Болванчик удалён."); }
        if (mode == "reset")
        {
            if (!owned.TryGetValue(player.PlayerUID, out var target)) return TextCommandResult.Error("Сначала /wbdummy");
            Restore(target); return TextCommandResult.Success("Болванчик восстановлен.");
        }
        if (!player.Entity.Alive) return TextCommandResult.Error("Сначала возродись.");
        var position = player.Entity.Pos.Copy();
        position.X -= Math.Sin(position.Yaw) * 2.5; position.Z -= Math.Cos(position.Yaw) * 2.5;
        var collision = new CollisionTester();
        var box = new Cuboidf(-.3f, 0, -.3f, .3f, 1.85f, .3f);
        bool clear = false;
        for (int lift = 0; lift <= 3; lift++)
        {
            var point = position.XYZ.AddCopy(0, lift, 0);
            if (collision.IsColliding(api.World.BlockAccessor, box, point, false)) continue;
            // Stand on a surface, rather than spawning inside a slope or over a pit.
            for (int down = 0; down < 50; down++)
            {
                if (collision.IsColliding(api.World.BlockAccessor, box, point.AddCopy(0, -.1, 0), false))
                { clear = true; break; }
                point.Y -= .1;
            }
            if (clear) { position.SetPos(point); break; }
        }
        if (!clear) return TextCommandResult.Error("Нет свободного места впереди. Отойди на ровную площадку.");
        Remove(player.PlayerUID);
        var dummy = (TrainingDummy)api.ClassRegistry.CreateEntity(api.World.GetEntityType(TrainingDummy.TypeCode));
        position.Yaw = GameMath.Mod(position.Yaw + GameMath.PI, GameMath.TWOPI);
        position.Pitch = 0; position.Roll = 0;
        dummy.Pos.SetFrom(position);
        dummy.WatchedAttributes.SetString("warbandDummyMode", mode);
        dummy.WatchedAttributes.SetString("warbandDummyOwner", player.PlayerUID);
        dummy.WatchedAttributes.SetFloat("warbandDummyHealth", mode == "normal" ? 15 : 1);
        dummy.WatchedAttributes["nametag"] = new TreeAttribute();
        dummy.WatchedAttributes.GetTreeAttribute("nametag").SetString("name", mode == "helmet" ? "Болванчик · шлем" : "Болванчик");
        api.World.SpawnEntity(dummy);
        owned[player.PlayerUID] = dummy;
        Restore(dummy);
        return TextCommandResult.Success(mode == "normal" ? "Болванчик: 15 HP. Возрождение через 8 с. /wbdummy fatal — 1 HP, helmet — со шлемом, clear — удалить."
            : "Болванчик: 1 HP. Боковой рубящий удар в голову — отсечение; дробящий — смятие. Возрождение через 8 с.");
    }

    private void Restore(TrainingDummy dummy)
    {
        fatalities.Reset(dummy);
        dummy.Revive(); dummy.DespawnReason = null; dummy.ReviveAt = 0;
        var health = dummy.GetBehavior<EntityBehaviorHealth>()
            ?? throw new InvalidOperationException("Training dummy health behavior missing");
        float amount = dummy.WatchedAttributes.GetFloat("warbandDummyHealth", 15);
        health.BaseMaxHealth = health.MaxHealth = health.Health = amount;
        health.ActiveDoTEffects.Clear();
        var gear = dummy.GetBehavior<EntityBehaviorSeraphInventory>()
            ?? throw new InvalidOperationException("Training dummy inventory behavior missing");
        var inventory = gear.Inventory;
        inventory.DiscardAll();
        if (dummy.WatchedAttributes.GetString("warbandDummyMode") == "helmet")
        {
            var helmet = api.World.GetItem(new AssetLocation("game:armor-head-plate-iron"));
            if (helmet == null) throw new InvalidOperationException("Training helmet item missing");
            var slot = inventory[(int)EnumCharacterDressType.ArmorHead];
            slot.Itemstack = new ItemStack(helmet); slot.MarkDirty();
        }
        gear.storeInv();
        dummy.WatchedAttributes.MarkAllDirty();
    }

    private void Tick(float dt)
    {
        foreach (var (uid, dummy) in owned.ToArray())
        {
            var player = api.World.PlayerByUid(uid);
            if (player?.Entity == null || dummy.Pos.Dimension != player.Entity.Pos.Dimension
                || dummy.Pos.SquareDistanceTo(player.Entity.Pos) > 96 * 96) { Remove(uid); continue; }
            if (!dummy.Alive && dummy.ReviveAt > 0 && api.World.ElapsedMilliseconds >= dummy.ReviveAt) Restore(dummy);
            var health = dummy.GetBehavior<EntityBehaviorHealth>();
            string name = dummy.Alive ? $"Болванчик · {health!.Health:0.#}/{health.MaxHealth:0.#} HP"
                : $"Возрождение · {Math.Max(0, (dummy.ReviveAt - api.World.ElapsedMilliseconds + 999) / 1000)} с";
            var nametag = dummy.WatchedAttributes.GetTreeAttribute("nametag");
            if (nametag.GetString("name") != name)
            { nametag.SetString("name", name); dummy.WatchedAttributes.MarkPathDirty("nametag"); }
        }
    }
    private void Disconnect(IServerPlayer player) => Remove(player.PlayerUID);
    private void Remove(string uid)
    {
        if (owned.Remove(uid, out var dummy))
            api.World.DespawnEntity(dummy, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
    }
    public void Dispose()
    {
        api.Event.UnregisterGameTickListener(tick); api.Event.PlayerDisconnect -= Disconnect;
        foreach (var uid in owned.Keys.ToArray()) Remove(uid);
    }
}
