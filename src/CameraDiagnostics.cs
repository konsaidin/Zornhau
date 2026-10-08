using Vintagestory.API.Client;

namespace Zornhau;

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
            $"[Zornhau][Camera] t={now} captured={capture} mode={player.CameraMode} immersive={api.Settings.Bool["immersiveFpMode"]} pitch={entity.Pos.Pitch:F5} inputPitch={api.Input.MousePitch:F5} headPitch={entity.Pos.HeadPitch:F5} yaw={entity.Pos.Yaw:F5} phase={fighter.Phase}/{fighter.Direction} sprint={entity.Controls.Sprint} sprintLocked={fighter.SprintLocked(now)} sprintIn={Math.Max(0, fighter.SprintUntil-now)} shield={fighter.ShieldGuard} main={entity.RightHandItemSlot.Itemstack?.Collectible.Code} offhand={entity.LeftHandItemSlot.Itemstack?.Collectible.Code} events={events} handled={handled} dx={dx} up={up} down={down} dyMin={minDy} dyMax={maxDy} mounted={entity.MountedOn != null} eye={entity.LocalEyePos}"));
        dx = up = down = 0; events = handled = minDy = maxDy = 0;
    }

    internal void Reset()
    {
        lastReport = dx = up = down = 0;
        events = handled = minDy = maxDy = 0; grabbed = null;
    }
}
