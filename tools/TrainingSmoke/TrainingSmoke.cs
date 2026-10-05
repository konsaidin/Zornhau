using System.Reflection;
using OpenTK.Mathematics;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using WarbandCombat;

// Developer-only integration plugin. Never bundled with the released mod.
public sealed class TrainingSmoke : ModSystem
{
    public override void StartServerSide(ICoreServerAPI api)
    {
        if (Environment.GetEnvironmentVariable("WARBAND_TRAINING_SMOKE") != "1") return;
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () =>
        {
            var spawn = api.World.DefaultSpawnPosition;
            api.WorldManager.LoadChunkColumnPriority((int)spawn.X / 32, (int)spawn.Z / 32,
                new ChunkLoadOptions { KeepLoaded = true, OnLoaded = () =>
                    api.Event.EnqueueMainThreadTask(() => Check(api), "training-smoke") });
        });
    }
    private void Check(ICoreServerAPI api)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var system = api.ModLoader.GetModSystem<CombatSystem>();
            var fatalities = typeof(CombatSystem).GetField("Fatalities", flags)!.GetValue(system)!;
            var trainer = typeof(CombatSystem).GetField("dummies", flags)!.GetValue(system)!;
            var record = fatalities.GetType().GetMethod("RecordContact", flags)!;
            var restore = trainer.GetType().GetMethod("Restore", flags)!;
            var direction = typeof(CombatSystem).Assembly.GetType("WarbandCombat.Direction")!;
            var type = api.World.GetEntityType(new("game:warband-trainingdummy"));
            var vanilla = api.World.GetEntityType(new("game:playerbot"));
            Require(type != null && vanilla.Attributes["inventory"].AsArray<JsonItemStack>().Length > 0
                && type.Attributes["inventory"].AsArray<JsonItemStack>().Length == 0, "Cloning must preserve the vanilla bot");
            var target = (TrainingDummy)api.ClassRegistry.CreateEntity(type!);
            var spawn = api.World.DefaultSpawnPosition;
            target.Pos.SetPos(spawn.X + .5, spawn.Y + 1, spawn.Z + .5);
            target.WatchedAttributes.SetString("warbandDummyMode", "fatal");
            target.WatchedAttributes.SetFloat("warbandDummyHealth", 1);
            api.World.SpawnEntity(target);
            Require(api.World.GetEntityById(target.EntityId) == target && !target.StoreWithChunk,
                "Engine must spawn the temporary humanoid");
            Require(target.GetBehavior<EntityBehaviorExtraSkinnable>()!.AppliedSkinParts.Count > 0,
                "Bot must have an actual face and skin");
            Require(target.LeftHandItemSlot.Empty && target.RightHandItemSlot.Empty, "No default equipment");
            bool co = api.ModLoader.IsModEnabled("combatoverhaulfork");
            if (co) Require(target.SidedProperties.Behaviors.Any(b => b.PropertyName().Contains("collider", StringComparison.OrdinalIgnoreCase)),
                "Native CO colliders must be present on the dummy");
            var attacker = new TestAttacker { Api = api, World = api.World, Code = new("game:player") };
            attacker.WatchedAttributes.SetString("playerUID", "training-smoke");
            foreach (var test in new[] { ("Left", EnumDamageType.SlashingAttack, "Head", 1),
                ("Overhead", EnumDamageType.SlashingAttack, "Head", 0),
                ("Left", EnumDamageType.SlashingAttack, "UpperTorso", 0),
                ("Right", EnumDamageType.BluntAttack, "Head", 2) })
            {
                target.WatchedAttributes.SetString("warbandDummyMode", test.Item2 == EnumDamageType.BluntAttack ? "helmet" : "fatal");
                restore.Invoke(trainer, [target]);
                var source = new DamageSource { SourceEntity = attacker, Source = EnumDamageSource.Player,
                    Type = test.Item2, IgnoreInvFrames = true };
                var point = new Vector3d(target.Pos.X, target.Pos.InternalY + target.LocalEyePos.Y, target.Pos.Z);
                var inventory = target.GetBehavior<EntityBehaviorSeraphInventory>()!.Inventory;
                var helmet = inventory[(int)EnumCharacterDressType.ArmorHead].Itemstack;
                int before = helmet?.Collectible.GetRemainingDurability(helmet) ?? 0;
                record.Invoke(fatalities, [target, source, Enum.Parse(direction, test.Item1), point, test.Item3]);
                Require(target.ReceiveDamage(source, 1000) && !target.Alive, "Actual engine damage must kill the dummy");
                Require(target.WatchedAttributes.GetInt("warbandFatality") == test.Item4, "Correct lethal-head effect");
                Require(!target.ShouldDespawn && (long)typeof(TrainingDummy).GetField("ReviveAt", flags)!.GetValue(target)! > api.World.ElapsedMilliseconds,
                    "Corpse must remain until delayed revival");
                if (test.Item2 == EnumDamageType.BluntAttack)
                    Require(helmet != null && helmet.Collectible.GetRemainingDurability(helmet) < before, "Actual helmet durability must be reduced");
                restore.Invoke(trainer, [target]);
                Require(target.Alive && target.DespawnReason == null && target.GetBehavior<EntityBehaviorHealth>()!.Health == 1
                    && target.WatchedAttributes.GetInt("warbandFatality") == 0, "Revival must restore health, head and despawn state");
            }
            api.World.DespawnEntity(target, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
            api.Logger.Notification("[Training Smoke] PASS: actual spawn, face, empty hands, lethal head rules, helmet wear, corpse retention, revival and cleanup; co={0}", co);
        }
        catch (Exception ex) { api.Logger.Error("[Training Smoke] FAILED: {0}", ex); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class TestAttacker : EntityPlayer { public override string GetName() => "Training smoke attacker"; }
}
