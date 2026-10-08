using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Zornhau;

// Constrain the character's actual heading after native camera/movement input.
// Overhead/F5 keeps its independent orbit camera; walking cannot bypass the cap.
internal sealed class CombatTurning : IDisposable
{
    private static CombatTurning? active;
    private readonly Harmony harmony = new("zornhau.turning");
    private readonly ICoreClientAPI api;
    private readonly Fighter fighter;
    private readonly Func<bool> ownsInput;
    private int positionSequence;
    private static System.Reflection.FieldInfo? packetTick;
    private readonly record struct TurnFrame(EntityPlayer Player, float Yaw, float Pitch,
        EnumCameraMode Camera, double Seconds, float? MountYaw);

    internal CombatTurning(ICoreClientAPI api, Fighter fighter, Func<bool> ownsInput)
    {
        this.api = api; this.fighter = fighter; this.ownsInput = ownsInput;
        active = this;
        harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientMain"), "UpdateCameraYawPitch"),
            prefix: new HarmonyMethod(typeof(CombatTurning), nameof(BeforeTurn)),
            postfix: new HarmonyMethod(typeof(CombatTurning), nameof(AfterTurn)));
        harmony.Patch(AccessTools.Method(typeof(PlayerHeadController), "AdjustAngles"),
            prefix: new HarmonyMethod(typeof(CombatTurning), nameof(BeforeBody)),
            postfix: new HarmonyMethod(typeof(CombatTurning), nameof(AfterBody)));
        harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Vintagestory.Server.ServerPackets"), "getEntityPositionPacket"),
            postfix: new HarmonyMethod(typeof(CombatTurning), nameof(PositionPacket)));
    }

    private static void PositionPacket(Entity __1, object __result)
    {
        var manager = active;
        // Native client packets always use Tick=0. Supply a monotonic ordering
        // tag so delayed UDP poses cannot masquerade as a fresh body turn.
        if (manager != null && ReferenceEquals(__1, manager.api.World.Player?.Entity))
            (packetTick ??= AccessTools.Field(__result.GetType(), "Tick")).SetValue(__result, ++manager.positionSequence);
    }

    private static void BeforeTurn(float dt, bool ___AllowCameraControl, float ___mousePitch, out TurnFrame? __state)
    {
        __state = null;
        var manager = active;
        if (manager == null || !___AllowCameraControl || !manager.ownsInput()) return;
        long now = manager.api.World.ElapsedMilliseconds;
        if (!manager.fighter.TurnLimited(now)) return;
        var player = manager.api.World.Player;
        var entity = player.Entity;
        double seconds = Math.Min(dt, (now - manager.fighter.StrikeStarts) / 1000d);
        __state = new(entity, entity.Pos.Yaw, entity.Pos.Pitch, player.CameraMode, seconds,
            entity.MountedOn?.SeatPosition.Yaw);
        // Native Overhead uses Pos.Pitch as camera scratch space. Restore the
        // independent orbit pitch before it runs, then keep the limited weapon
        // pitch separately in the postfix.
        if (player.CameraMode == EnumCameraMode.Overhead && float.IsFinite(___mousePitch))
            entity.Pos.Pitch = ___mousePitch;
    }

    private static void AfterTurn(TurnFrame? __state, ref float ___mouseYaw, ref float ___mousePitch,
        ref double ___MouseDeltaX, ref double ___MouseDeltaY, double ___DelayedMouseDeltaX, double ___DelayedMouseDeltaY)
    {
        if (__state is not { } frame) return;
        var entity = frame.Player;
        float mountTurn = frame.MountYaw is { } mount && entity.MountedOn is { } seat
            ? GameMath.AngleRadDistance(mount, seat.SeatPosition.Yaw) : 0;
        var turn = CombatRules.LimitSwingTurn(GameMath.AngleRadDistance(frame.Yaw + mountTurn, entity.Pos.Yaw),
            GameMath.AngleRadDistance(frame.Pitch, entity.Pos.Pitch), frame.Seconds);
        entity.Pos.Yaw = GameMath.Mod(frame.Yaw + mountTurn + (float)turn.X, GameMath.TWOPI);
        entity.Pos.Pitch = frame.Pitch + (float)turn.Y;
        if (frame.Camera != EnumCameraMode.Overhead)
        {
            ___mouseYaw = entity.Pos.Yaw;
            ___mousePitch = entity.Pos.Pitch;
        }
        // Consume native smoothing residue. A completed strike must not replay
        // mouse movement discarded by the character's rotation constraint.
        ___MouseDeltaX = ___DelayedMouseDeltaX;
        ___MouseDeltaY = ___DelayedMouseDeltaY;
    }

    private static void BeforeBody(EntityPlayer ___entityPlayer, out float? __state)
    {
        var manager = active;
        __state = manager != null && ReferenceEquals(___entityPlayer, manager.api.World.Player?.Entity)
            && manager.ownsInput() && manager.fighter.TurnLimited(manager.api.World.ElapsedMilliseconds)
            ? ___entityPlayer.BodyYaw : null;
    }

    private static void AfterBody(float dt, EntityPlayer ___entityPlayer, float? __state)
    {
        if (__state is not { } yaw) return;
        // Heading is already slowed above. Only cap the model's native catch-up
        // here, so a camera-mode switch cannot snap the weapon's body matrix.
        float maximum = (float)(CombatRules.SwingTurnRadiansPerSecond * Math.Max(0, dt));
        float delta = GameMath.AngleRadDistance(yaw, ___entityPlayer.BodyYaw);
        ___entityPlayer.BodyYaw = GameMath.Mod(yaw + Math.Clamp(delta, -maximum, maximum), GameMath.TWOPI);
    }

    public void Dispose()
    {
        harmony.UnpatchAll(harmony.Id);
        if (ReferenceEquals(active, this)) active = null;
    }
}
