using HarmonyLib;
using Vintagestory.API.Client;

namespace WarbandCombat;

// Limit the pending native mouse movement before it reaches camera, model and
// weapon transforms. Consume excess input instead of replaying it after a hit.
internal sealed class CombatTurning : IDisposable
{
    private static CombatTurning? active;
    private readonly Harmony harmony = new("warbandcombat.turning");
    private readonly ICoreClientAPI api;
    private readonly Fighter fighter;
    private readonly Func<bool> ownsInput;
    private readonly Func<int> mouseSmoothing;

    internal CombatTurning(ICoreClientAPI api, Fighter fighter, Func<bool> ownsInput)
    {
        this.api = api; this.fighter = fighter; this.ownsInput = ownsInput;
        var settings = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientSettings");
        mouseSmoothing = AccessTools.PropertyGetter(settings, "MouseSmoothing").CreateDelegate<Func<int>>();
        active = this;
        harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientMain"), "UpdateCameraYawPitch"),
            prefix: new HarmonyMethod(typeof(CombatTurning), nameof(BeforeTurn)),
            postfix: new HarmonyMethod(typeof(CombatTurning), nameof(AfterTurn)));
    }

    private static void BeforeTurn(float dt, bool ___AllowCameraControl, float ___rotationspeed,
        float ___swimmingMouseSmoothing, ref double ___MouseDeltaX, ref double ___MouseDeltaY,
        double ___DelayedMouseDeltaX, double ___DelayedMouseDeltaY, out bool __state)
    {
        __state = false;
        var manager = active;
        if (manager == null || !___AllowCameraControl || !manager.ownsInput()) return;
        long now = manager.api.World.ElapsedMilliseconds;
        if (!manager.fighter.TurnLimited(now)) return;
        __state = true;
        // Match the engine's smoothing conversion so the cap is in radians per
        // second regardless of FPS, DPI, sensitivity, smoothing or inversion.
        double smoothing = Math.Clamp(manager.mouseSmoothing() / 100f * Math.Clamp(dt * 75, 0, 3), .01, 1);
        if (manager.api.World.Player.Entity.Swimming && manager.api.World.Player.CameraMode == EnumCameraMode.FirstPerson)
            if (___swimmingMouseSmoothing > 0) smoothing = Math.Clamp(1f - ___swimmingMouseSmoothing, 0, 1);
        double radiansPerUnit = Math.Abs(___rotationspeed) / 75d * smoothing;
        double seconds = Math.Min(dt, (now - manager.fighter.StrikeStarts) / 1000d);
        var turn = CombatRules.LimitSwingTurn((___MouseDeltaX - ___DelayedMouseDeltaX) * radiansPerUnit,
            (___MouseDeltaY - ___DelayedMouseDeltaY) * radiansPerUnit, seconds);
        ___MouseDeltaX = ___DelayedMouseDeltaX + (radiansPerUnit > 0 ? turn.X / radiansPerUnit : 0);
        ___MouseDeltaY = ___DelayedMouseDeltaY + (radiansPerUnit > 0 ? turn.Y / radiansPerUnit : 0);
    }

    private static void AfterTurn(bool __state, ref double ___MouseDeltaX, ref double ___MouseDeltaY,
        double ___DelayedMouseDeltaX, double ___DelayedMouseDeltaY)
    {
        if (!__state) return;
        ___MouseDeltaX = ___DelayedMouseDeltaX;
        ___MouseDeltaY = ___DelayedMouseDeltaY;
    }

    public void Dispose()
    {
        harmony.UnpatchAll(harmony.Id);
        if (ReferenceEquals(active, this)) active = null;
    }
}
