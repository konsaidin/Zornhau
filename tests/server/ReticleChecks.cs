using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using Zornhau;

internal static class ReticleChecks
{
    private static bool nativeReached;
    private static bool StopBeforeOpenGl() { nativeReached = true; return false; }

    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        var fighter = new Fighter(); var timing = WeaponTiming.Overhaul("greatsword");
        fighter.Apply(Command.Begin, Direction.Left, 1000, timing);
        Check(CombatHud.PhaseKey(fighter, 1000) == "phase-windup-held", "Held preparation is explicitly windup");
        fighter.Apply(Command.Release, Direction.Right, 1001, timing);
        Check(fighter.Direction == Direction.Left && CombatHud.PhaseKey(fighter, 1001) == "phase-windup",
            "A quick release keeps the windup label and locked direction");
        Check(CombatHud.PhaseKey(fighter, fighter.StrikeStarts - 1) == "phase-windup"
            && CombatHud.PhaseKey(fighter, fighter.StrikeStarts) == "phase-swing",
            "Strike label begins exactly when the real weapon swing starts");
        Check(CombatHud.Color(fighter, fighter.StrikeStarts).G < CombatHud.Color(fighter, fighter.StrikeStarts - 1).G,
            "Preparation and released strike have distinct ray colors");
        fighter.Advance(fighter.ContactEnds + 1);
        Check(CombatHud.PhaseKey(fighter, fighter.ContactEnds + 1) == "phase-recovery", "Miss recovery has its own phase");
        fighter.Reset(); fighter.Apply(Command.Block, Direction.Right, 2000, timing);
        Check(CombatHud.PhaseKey(fighter, 2000) == "guard-raise"
            && CombatHud.PhaseKey(fighter, 2000 + CombatRules.GuardRaise) == "phase-block", "Guard raising and ready guard remain distinct");
        fighter.Reset(); fighter.SetShield(true, 3000); fighter.Apply(Command.Block, Direction.Left, 3000, timing);
        Check(CombatHud.PhaseKey(fighter, 3000) == "shield-raise"
            && CombatHud.PhaseKey(fighter, 3000 + CombatRules.ShieldRaise) == "shield-guard", "Omnidirectional shields retain their correct labels");
        Check(CombatReticle.Region(16, 5, 32, 32) == (int)Direction.Overhead
            && CombatReticle.Region(26, 16, 32, 32) == (int)Direction.Right
            && CombatReticle.Region(16, 26, 32, 32) == (int)Direction.Thrust
            && CombatReticle.Region(5, 16, 32, 32) == (int)Direction.Left
            && CombatReticle.Region(16, 16, 32, 32) == 4, "Native plus arms match all four combat directions and leave its center neutral");
        Check(CombatHud.Pulse(-1) == 0 && CombatHud.Pulse(0) == 1 && CombatHud.Pulse(80) < .15f
            && CombatHud.Pulse(160) > .6f && CombatHud.Pulse(320) > .3f
            && CombatHud.Pulse(480) == 0 && CombatHud.Pulse(10000) == 0, "Confirmed contact produces three bounded fading flashes and expires");
        Check(CombatHud.ContactColor(ContactKind.Hit).G > CombatHud.ContactColor(ContactKind.Hit).B
            && CombatHud.ContactColor(ContactKind.Parry).B > CombatHud.ContactColor(ContactKind.Parry).R
            && CombatHud.ContactColor(ContactKind.ShieldBreak).R > CombatHud.ContactColor(ContactKind.ShieldBreak).G,
            "Hits, successful blocks and broken shields have distinct confirmation colors");

        bool visible = true;
        var api = Stub.Make<ICoreClientAPI>((m, a) => Stub.Default(m.ReturnType));
        var native = RuntimeHelpers.GetUninitializedObject(typeof(SystemRenderAim));
        var game = RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        var draw = AccessTools.Method(typeof(SystemRenderAim), "DrawAim");
        var harness = new Harmony("zornhau.reticle-test");
        using var reticle = new CombatReticle(api, () => visible);
        // The native draw would access the window/OpenGL. This last prefix is
        // reached only if production code allows the native renderer through.
        harness.Patch(draw, prefix: new HarmonyMethod(typeof(ReticleChecks), nameof(StopBeforeOpenGl)) { priority = Priority.Last });
        try
        {
            nativeReached = false; draw.Invoke(native, [game]);
            Check(!nativeReached, "Combat reticle suppresses the actual native draw, preventing a second white plus over its colors");
            visible = false; nativeReached = false; draw.Invoke(native, [game]);
            Check(nativeReached, "Hidden, paused, unarmed or inactive HUD permits the native reticle");
            visible = true; nativeReached = false; draw.Invoke(native, [game]);
            Check(!nativeReached, "Returning to combat suppresses native drawing again");
            reticle.Dispose(); nativeReached = false; draw.Invoke(native, [game]);
            Check(nativeReached, "Disposing the combat HUD restores the original draw without a persistent engine setting");
        }
        finally { harness.UnpatchAll(harness.Id); }
        Console.WriteLine($"PASS: {checks} production reticle phases, flashes and native draw ownership checks");
    }
}
