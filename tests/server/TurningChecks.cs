using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using Zornhau;

internal static class TurningChecks
{
    private static bool Focused(ref bool __result) { __result = true; return false; }

    internal static void Run()
    {
        // Window types appear in the platform's getter signatures. Resolve
        // those managed game libraries without constructing a native window.
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            string game = Environment.GetEnvironmentVariable("VSPath") ?? Environment.GetEnvironmentVariable("VS_GAME_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "vintagestory");
            string path = Path.Combine(game, "Lib", name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        try { RunEngine(); }
        finally { AssemblyLoadContext.Default.Resolving -= Resolve; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunEngine()
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
        { "get_Player" or "PlayerByUid" => player, "get_ElapsedMilliseconds" => now, _ => Stub.Default(m.ReturnType) });
        var booleans = Stub.Make<ISettingsClass<bool>>((m, a) => Stub.Default(m.ReturnType));
        var settings = Stub.Make<ISettings>((m, a) => m.Name == "get_Bool" ? booleans : Stub.Default(m.ReturnType));
        var api = Stub.Make<ICoreClientAPI>((m, a) => m.Name switch
        { "get_World" => world, "get_Settings" => settings, _ => Stub.Default(m.ReturnType) });
        entity.Api = api; entity.World = world;
        var fighter = new Fighter();
        fighter.Apply(Command.Begin, Direction.Right, 1000, WeaponTiming.Overhaul("greatsword"));
        fighter.Apply(Command.Release, Direction.Right, 1400, fighter.Timing);
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        game.player = player;
        typeof(ClientMain).GetField("rotationspeed", fields)!.SetValue(game, 1f);
        game.Platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        game.MainCamera = (PlayerCamera)RuntimeHelpers.GetUninitializedObject(typeof(PlayerCamera));
        bool ownsInput = true;
        var harness = new Harmony("zornhau.turning-test");
        // Run the real native camera and character rotation, replacing only
        // desktop focus/mouse-grab queries so no window/OpenGL is needed.
        foreach (string property in new[] { "IsFocused", "MouseGrabbed" })
            harness.Patch(AccessTools.PropertyGetter(typeof(ClientPlatformWindows), property),
                prefix: new HarmonyMethod(typeof(TurningChecks), nameof(Focused)));
        void Mode(EnumCameraMode mode)
        {
            player.OverrideCameraMode = mode;
            AccessTools.Field(typeof(Camera), "CameraMode").SetValue(game.MainCamera, mode);
        }
        void Reset(float yaw = 0)
        {
            entity.Pos.Yaw = entity.BodyYaw = yaw; entity.Pos.Pitch = (float)Math.PI;
            entity.WalkYaw = (float)Math.PI;
            game.mouseYaw = yaw; game.mousePitch = (float)Math.PI;
            game.MouseDeltaX = 250; game.MouseDeltaY = 80;
            game.DelayedMouseDeltaX = game.DelayedMouseDeltaY = 0;
            game.AllowCameraControl = true;
        }
        Mode(EnumCameraMode.Overhead); Reset();
        game.UpdateCameraYawPitch(1f / 60);
        Check(entity.Pos.Yaw > Math.PI / 60,
            "Unpatched F5 character turns toward WalkYaw faster than the strike cap even without camera/body coupling");
        using var turning = new CombatTurning(api, fighter, () => ownsInput);
        var head = (Vintagestory.API.Common.PlayerHeadController)RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.API.Common.PlayerHeadController));
        AccessTools.Field(typeof(Vintagestory.API.Common.PlayerHeadController), "entityPlayer").SetValue(head, entity);
        AccessTools.Field(typeof(Vintagestory.API.Common.EntityHeadController), "entity").SetValue(head, entity);
        AccessTools.Field(typeof(Vintagestory.API.Common.PlayerHeadController), "clientApi").SetValue(head, api);
        AccessTools.Field(typeof(Vintagestory.API.Common.PlayerHeadController), "bodyFollowSpeedFactor").SetValue(head, 1f);
        var adjust = AccessTools.Method(typeof(Vintagestory.API.Common.PlayerHeadController), "AdjustAngles");
        int previous = ClientSettings.MouseSmoothing;
        try
        {
            var encode = AccessTools.Method(typeof(Vintagestory.Server.ServerPackets), "getEntityPositionPacket");
            var first = (Packet_EntityPosition)encode.Invoke(null, [entity.Pos, entity, 0])!;
            var second = (Packet_EntityPosition)encode.Invoke(null, [entity.Pos, entity, 0])!;
            Check(first.Tick > 0 && second.Tick == first.Tick + 1,
                "Actual native client position encoder supplies monotonically increasing sequence tags");
            var other = new Holder { EntityId = entity.EntityId, Api = api, World = world };
            var remotePacket = (Packet_EntityPosition)encode.Invoke(null, [other.Pos, other, 0])!;
            Check(remotePacket.Tick == 0, "Encoder tagging does not touch another entity/server representation");
            foreach (var camera in new[] { EnumCameraMode.FirstPerson, EnumCameraMode.ThirdPerson, EnumCameraMode.Overhead })
            foreach (bool jumping in new[] { false, true })
            foreach (int smoothing in new[] { 0, 30, 100 })
            foreach (int fps in new[] { 30, 60, 144, 240 })
            {
                ClientSettings.MouseSmoothing = smoothing;
                foreach (long time in new long[] { 1499, 1510, 1600, fighter.ContactEnds, fighter.ContactEnds + 1 })
                {
                    now = time; Mode(camera); Reset();
                    entity.Controls.Jump = jumping; entity.ServerControls.Jump = jumping; entity.OnGround = !jumping;
                    game.UpdateCameraYawPitch(1f / fps);
                    double yaw = Vintagestory.API.MathTools.GameMath.AngleRadDistance(0, entity.Pos.Yaw);
                    double pitch = entity.Pos.Pitch - Math.PI;
                    double angle = Math.Sqrt(yaw * yaw + pitch * pitch);
                    if (fighter.TurnLimited(now))
                    {
                        Check(angle <= Math.PI / fps + 1e-5 && angle > 0,
                            "Actual native character rotation respects the cap for every F5 mode, FPS, smoothing and airborne state");
                        Check(game.MouseDeltaX == game.DelayedMouseDeltaX && game.MouseDeltaY == game.DelayedMouseDeltaY,
                            "Discarded native smoothing residue cannot replay when the strike ends");
                        Check(entity.Controls.Jump == jumping && entity.ServerControls.Jump == jumping,
                            "Rotation constraints never disable jumping");
                        if (camera == EnumCameraMode.Overhead)
                        {
                            double nativeSmoothing = Math.Clamp(smoothing / 100f * Math.Clamp(75f / fps, 0, 3), .01, 1);
                            float nativeYaw = Vintagestory.API.MathTools.GameMath.Mod((float)(-250 * nativeSmoothing / 75),
                                Vintagestory.API.MathTools.GameMath.TWOPI);
                            Check(Math.Abs(Vintagestory.API.MathTools.GameMath.AngleRadDistance(nativeYaw, game.mouseYaw)) < 1e-5,
                                "F5 orbit camera retains the full native mouse rotation while the character is constrained");
                        }
                        else Check(game.mouseYaw == entity.Pos.Yaw && game.mousePitch == entity.Pos.Pitch,
                            "Linked FP/TP camera follows the accepted heading instead of snapping back next frame");
                        entity.BodyYaw = 0;
                        adjust.Invoke(head, [1f / fps]);
                        Check(Math.Abs(Vintagestory.API.MathTools.GameMath.AngleRadDistance(0, entity.BodyYaw)) <= Math.PI / fps + 1e-5,
                            "Native head-controller model catch-up also respects the cap");
                    }
                    else Check(angle > .001, "Windup and post-cut character rotation retain their native behavior");
                }
            }
            // Steering by movement keys alone was the old F5 bypass.
            now = 1600; Mode(EnumCameraMode.Overhead); Reset();
            game.MouseDeltaX = game.MouseDeltaY = 0;
            game.UpdateCameraYawPitch(1f / 60);
            Check(Math.Abs(Vintagestory.API.MathTools.GameMath.AngleRadDistance(0, entity.Pos.Yaw)) <= Math.PI / 60 + 1e-5,
                "WalkYaw-driven character turn is bounded even with zero mouse motion");
            // Continuous frames use the same limit, including the 2PI seam.
            Mode(EnumCameraMode.Overhead); Reset(6.27f); entity.WalkYaw = .2f;
            for (int i = 0; i < 15; i++)
            {
                now = 1600 + i * 10; float before = entity.Pos.Yaw;
                game.UpdateCameraYawPitch(.01f);
                Check(Math.Abs(Vintagestory.API.MathTools.GameMath.AngleRadDistance(before, entity.Pos.Yaw)) <= Math.PI * .01 + 1e-5,
                    "Continuous movement steering never accumulates a turn burst across the angle seam");
            }
            // Toggle modes while a strike is active: camera separation does not
            // grant an unlimited character snap on returning to first person.
            now = 1700; Reset(); Mode(EnumCameraMode.Overhead); game.UpdateCameraYawPitch(.016f);
            float beforeSwitch = entity.Pos.Yaw; Mode(EnumCameraMode.FirstPerson); game.UpdateCameraYawPitch(.016f);
            Check(Math.Abs(Vintagestory.API.MathTools.GameMath.AngleRadDistance(beforeSwitch, entity.Pos.Yaw)) <= Math.PI * .016 + 1e-5,
                "Switching F5 during a strike cannot snap the character to the free camera's heading");
            ownsInput = false; now = 1600; Mode(EnumCameraMode.Overhead); Reset();
            game.UpdateCameraYawPitch(.02f);
            Check(entity.Pos.Yaw > Math.PI * .02, "Inactive combat input retains native free movement steering");
        }
        finally { ClientSettings.MouseSmoothing = previous; harness.UnpatchAll(harness.Id); }
        Console.WriteLine($"PASS: {checks} real engine character turn, F5, jumping and smoothing checks");
    }
}
