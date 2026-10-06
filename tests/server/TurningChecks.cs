using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using WarbandCombat;

internal static class TurningChecks
{
    private static void PauseAfterSmoothing(ClientMain __instance) => __instance.AllowCameraControl = false;

    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var entity = new Holder { Alive = true };
        var player = (ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
        var data = ClientWorldPlayerData.CreateNew(); data.EntityPlayer = entity;
        typeof(ClientPlayer).GetField("worlddata", fields)!.SetValue(player, data);
        long now = 1400;
        var world = Stub.Make<IClientWorldAccessor>((m, a) => m.Name switch
        { "get_Player" => player, "get_ElapsedMilliseconds" => now, _ => Stub.Default(m.ReturnType) });
        var api = Stub.Make<ICoreClientAPI>((m, a) => m.Name == "get_World" ? world : Stub.Default(m.ReturnType));
        var fighter = new Fighter();
        fighter.Apply(Command.Begin, Direction.Right, 1000, WeaponTiming.Overhaul("greatsword"));
        fighter.Apply(Command.Release, Direction.Right, 1400, fighter.Timing);
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        game.player = player;
        typeof(ClientMain).GetField("rotationspeed", fields)!.SetValue(game, 1f);
        bool ownsInput = true;
        using var turning = new CombatTurning(api, fighter, () => ownsInput);
        var harness = new Harmony("warbandcombat.turning-test");
        // Let the real engine consume its smoothed mouse deltas, then stop at
        // its normal camera-control gate before any window/OpenGL is needed.
        harness.Patch(AccessTools.Method(typeof(ClientMain), nameof(ClientMain.UpdateCameraYawPitch)),
            prefix: new HarmonyMethod(typeof(TurningChecks), nameof(PauseAfterSmoothing)) { priority = Priority.Last });
        int previous = ClientSettings.MouseSmoothing;
        try
        {
            foreach (int smoothing in new[] { 0, 30, 100 })
            foreach (int fps in new[] { 30, 60, 144, 240 })
            {
                ClientSettings.MouseSmoothing = smoothing;
                foreach (long time in new long[] { 1499, 1510, 1600, fighter.ContactEnds, fighter.ContactEnds + 1 })
                {
                    now = time;
                    game.AllowCameraControl = true;
                    game.MouseDeltaX = 1000000; game.MouseDeltaY = -1000000;
                    game.DelayedMouseDeltaX = game.DelayedMouseDeltaY = 0;
                    game.UpdateCameraYawPitch(1f / fps);
                    double angle = Math.Sqrt(game.DelayedMouseDeltaX * game.DelayedMouseDeltaX
                        + game.DelayedMouseDeltaY * game.DelayedMouseDeltaY) / 75;
                    if (fighter.TurnLimited(now))
                    {
                        Check(angle <= Math.PI / fps + 1e-7 && angle > 0,
                            "Real native smoothing respects the angular cap independently of FPS/settings");
                        Check(game.MouseDeltaX == game.DelayedMouseDeltaX && game.MouseDeltaY == game.DelayedMouseDeltaY,
                            "Excess movement is discarded, so ending an attack cannot replay queued rotation");
                    }
                    else Check(angle > 1, "Native windup and post-cut turning retain their original speed");
                }
            }
            ownsInput = false; now = 1600;
            game.AllowCameraControl = true; game.MouseDeltaX = 1000000; game.MouseDeltaY = 0;
            game.DelayedMouseDeltaX = game.DelayedMouseDeltaY = 0;
            game.UpdateCameraYawPitch(.02f);
            Check(game.DelayedMouseDeltaX / 75 > 1, "Uncaptured, paused or inactive input can bypass the limiter");
        }
        finally { ClientSettings.MouseSmoothing = previous; harness.UnpatchAll(harness.Id); }
        Console.WriteLine($"PASS: {checks} real engine turn smoothing and queue checks");
    }
}
