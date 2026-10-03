using Vintagestory.API.Client;

namespace WarbandCombat;

// Opt-in, read-only diagnostics. Never writes pitch, yaw, mouse state or animations.
internal sealed class CameraDiagnostics(ICoreClientAPI api)
{
    private long lastReport;
    private long dx, up, down;
    private int events, handled;
    private int minDy, maxDy;
    private bool? grabbed;

    internal void MouseMove(MouseEvent e)
    {
        if (!api.Input.MouseGrabbed || api.IsGamePaused) return;
        dx += e.DeltaX;
        if (e.DeltaY < 0) up += e.DeltaY;
        else down += e.DeltaY;
        minDy = Math.Min(minDy, e.DeltaY); maxDy = Math.Max(maxDy, e.DeltaY);
        events++;
        if (e.Handled) handled++;
    }

    internal void OnFrame(long now, Fighter fighter)
    {
        var player = api.World.Player;
        if (player?.Entity == null) return;
        bool capture = api.Input.MouseGrabbed && !api.IsGamePaused;
        if (grabbed == capture && (!capture || now - lastReport < 1000)) return;
        grabbed = capture; lastReport = now;
        var entity = player.Entity;
        api.Logger.Notification(FormattableString.Invariant(
            $"[Warband Combat][Camera] t={now} captured={capture} mode={player.CameraMode} immersive={api.Settings.Bool["immersiveFpMode"]} pitch={entity.Pos.Pitch:F5} inputPitch={api.Input.MousePitch:F5} serverPitch={entity.ServerPos.Pitch:F5} yaw={entity.Pos.Yaw:F5} phase={fighter.Phase}/{fighter.Direction} events={events} handled={handled} dx={dx} up={up} down={down} dyMin={minDy} dyMax={maxDy} mounted={entity.MountedOn != null} eye={entity.LocalEyePos}"));
        dx = up = down = 0; events = handled = minDy = maxDy = 0;
    }

    internal void Reset()
    {
        lastReport = dx = up = down = 0;
        events = handled = minDy = maxDy = 0; grabbed = null;
    }
}
