using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Zornhau;

// The server cannot verify the 35% mouse sensitivity multiplier. It can bound
// the accumulated heading/model rotation at 180 degrees/sec, with one shared
// 75ms allowance per strike for batched/delayed position packets.
internal static class CombatTurnAuthority
{
    internal const int PacketAllowanceMs = 75;
    private static readonly ConditionalWeakTable<EntityPlayer, Budget> budgets = new();
    internal static Action<EntityPlayer>? Rejected;

    internal static float[] Capture(EntityPlayer player, bool server = false)
        => [player.Pos.Yaw, player.Pos.Pitch, server ? player.BodyYawServer : player.BodyYaw];

    internal static void Bind(EntityPlayer player, Fighter fighter)
    {
        if (budgets.TryGetValue(player, out var current) && ReferenceEquals(current.Fighter, fighter)) return;
        budgets.Remove(player); budgets.Add(player, new Budget(fighter, Capture(player, true))
            { Dimension = player.Pos.Dimension, MountYaw = player.MountedOn?.SeatPosition.Yaw });
    }

    internal static void Patch(Harmony harmony)
    {
        var type = AccessTools.TypeByName("Vintagestory.Server.Systems.EntityPosExtensions");
        harmony.Patch(AccessTools.Method(type, "SetFromPacket"),
            postfix: new HarmonyMethod(typeof(CombatTurnAuthority), nameof(PositionReceived)));
    }

    private static System.Reflection.FieldInfo? packetTick;

    private static void PositionReceived(Entity entity, object packet)
    {
        if (entity is not EntityPlayer player || player.Api?.Side != EnumAppSide.Server) return;
        if (!budgets.TryGetValue(player, out var budget)) return;
        int sequence = (int)(packetTick ??= AccessTools.Field(packet.GetType(), "Tick")).GetValue(packet)!;
        if (sequence > 0 && sequence <= budget.LastNativeSequence)
        { Apply(player, budget.Pose); return; }
        if (sequence <= 0 && budget.Fighter.TurnLimited(player.World.ElapsedMilliseconds))
        { Reject(player, budget, player.World.ElapsedMilliseconds); Apply(player, budget.Pose); return; }
        budget.LastNativeSequence = Math.Max(sequence, budget.LastNativeSequence);
        // Some native sanity failures occur after angles have already been
        // written. Validate them even when SetFromPacket returned false.
        Observe(player);
    }

    internal static void Observe(EntityPlayer player)
    {
        if (!budgets.TryGetValue(player, out var budget)) return;
        long now = player.World.ElapsedMilliseconds;
        budget.Context(player, now);
        if (!budget.Try(Capture(player, true), now, proof: false)) Reject(player, budget, now);
        Apply(player, budget.Pose);
    }

    internal static bool Proof(EntityPlayer player, float[]? pose)
    {
        if (!budgets.TryGetValue(player, out var budget)) return false;
        long now = player.World.ElapsedMilliseconds;
        budget.Context(player, now);
        if (!budget.Try(pose, now, proof: true))
        { Reject(player, budget, now); Apply(player, budget.Pose); return false; }
        Apply(player, budget.Pose);
        return true;
    }

    private static void Apply(EntityPlayer player, float[] pose)
    { player.Pos.Yaw = pose[0]; player.Pos.Pitch = pose[1]; player.BodyYawServer = pose[2]; }

    private static void Reject(EntityPlayer player, Budget budget, long now)
    {
        if (budget.Fighter.Phase != Phase.Swing) return;
        budget.Fighter.Stun(now, CombatRules.HitStun);
        Rejected?.Invoke(player);
    }

    internal sealed class Budget(Fighter fighter, float[] initial)
    {
        internal Fighter Fighter => fighter;
        internal float[] Pose { get; private set; } = initial;
        private readonly List<float[]> history = new();
        private long window = long.MinValue, sampledAt;
        private double headingCredit, bodyCredit;
        private float[]? anchor;
        internal int Dimension;
        internal float? MountYaw;
        internal int LastNativeSequence;

        internal void Context(EntityPlayer player, long now)
        {
            float? mount = player.MountedOn?.SeatPosition.Yaw;
            if (Dimension != player.Pos.Dimension || player.IsTeleport || mount.HasValue != MountYaw.HasValue)
            {
                fighter.Invalidate(now);
                Pose = Capture(player, true); window = long.MinValue; history.Clear(); anchor = null;
            }
            else if (MountYaw is { } before && mount is { } after)
            {
                // Authoritative seat rotation is external to the rider's turn.
                // Apply it to every unique cached array once, not to the budget.
                float delta = GameMath.AngleRadDistance(before, after);
                foreach (var p in history.Append(Pose).Concat(anchor == null ? [] : new[] { anchor }).Distinct())
                {
                    p[0] = GameMath.Mod(p[0] + delta, GameMath.TWOPI);
                    p[2] = GameMath.Mod(p[2] + delta, GameMath.TWOPI);
                }
            }
            Dimension = player.Pos.Dimension; MountYaw = mount;
        }

        internal bool Try(float[]? pose, long now, bool proof)
        {
            if (pose?.Length != 3 || pose.Any(v => !float.IsFinite(v))) return false;
            var normalized = pose.Select(v => GameMath.Mod(v, GameMath.TWOPI)).ToArray();
            if (!fighter.TurnLimited(now))
            {
                if (proof) return false;
                Pose = normalized; window = long.MinValue; history.Clear(); anchor = null; return true;
            }
            if (window != fighter.StrikeStarts)
            {
                window = fighter.StrikeStarts; sampledAt = now; history.Clear(); history.Add(Pose);
                anchor = Pose;
                headingCredit = bodyCredit = (now - window + PacketAllowanceMs) / 1000d * CombatRules.SwingTurnRadiansPerSecond + .0002;
            }
            else
            {
                double elapsed = Math.Max(0, now - sampledAt) / 1000d;
                headingCredit += elapsed * CombatRules.SwingTurnRadiansPerSecond;
                bodyCredit += elapsed * CombatRules.SwingTurnRadiansPerSecond;
                sampledAt = Math.Max(now, sampledAt);
            }
            // UDP poses and reliable contact proofs may arrive in either order.
            // An already accepted pose neither spends credit twice nor rewinds
            // the current heading. Retain samples only for this same strike.
            // A reliable proof can describe the render frame between two UDP
            // samples. Accept that small historical neighborhood without
            // charging reverse/forward rotation or rewinding the avatar.
            double lag = PacketAllowanceMs / 1000d * CombatRules.SwingTurnRadiansPerSecond + .0002;
            if (proof && anchor != null)
            {
                double x = GameMath.AngleRadDistance(anchor[0], normalized[0]);
                double y = GameMath.AngleRadDistance(anchor[1], normalized[1]);
                double total = (now - window) / 1000d * CombatRules.SwingTurnRadiansPerSecond + lag;
                if (Math.Sqrt(x * x + y * y) > total + 1e-8
                    || Math.Abs(GameMath.AngleRadDistance(anchor[2], normalized[2])) > total + 1e-8) return false;
            }
            bool known = history.Any(p =>
            {
                double x = GameMath.AngleRadDistance(p[0], normalized[0]);
                double y = GameMath.AngleRadDistance(p[1], normalized[1]);
                return Math.Sqrt(x * x + y * y) <= lag
                    && Math.Abs(GameMath.AngleRadDistance(p[2], normalized[2])) <= lag;
            });
            if (proof && known)
                return true;
            double yaw = GameMath.AngleRadDistance(Pose[0], normalized[0]);
            double pitch = GameMath.AngleRadDistance(Pose[1], normalized[1]);
            double heading = Math.Sqrt(yaw * yaw + pitch * pitch);
            double body = Math.Abs(GameMath.AngleRadDistance(Pose[2], normalized[2]));
            // Float quantization is reserved once at window start. A free
            // tolerance per message would let packet spam buy extra rotation.
            if (heading > headingCredit + 1e-8 || body > bodyCredit + 1e-8)
                return false;
            headingCredit = Math.Max(0, headingCredit - heading);
            bodyCredit = Math.Max(0, bodyCredit - body);
            Pose = normalized; history.Add(Pose);
            if (history.Count > 64) history.RemoveAt(0);
            return true;
        }
    }
}
