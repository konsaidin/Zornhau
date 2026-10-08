using Zornhau;
using Vintagestory.API.Common;

internal static class ShieldChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string message)
        { if (!ok) throw new Exception(message); checks++; }
        foreach (Direction incoming in Enum.GetValues<Direction>())
        foreach (Direction guard in Enum.GetValues<Direction>())
        {
            Check(CombatRules.GuardCovers(true, guard, incoming), "Shield covers all four directions");
            Check(CombatRules.GuardCovers(false, guard, incoming) == (guard == Fighter.GuardFor(incoming)),
                "Weapon parries still require the correct direction");
        }
        Check(CombatShields.IsShield(new Item { Tool = EnumTool.Shield }), "Armory shield classification");
        Check(!CombatShields.IsShield(new Item { Tool = EnumTool.Sword }), "Sword is not a shield");
        Check(!CombatShields.IsShield(null), "Empty offhand is not a shield");
        foreach (string code in new[] { "game:shield-blackguard", "game:shield-woodmetal",
            "armory:shield-light-plain-iron", "armory:shield-heavy-plain-iron" })
            Check(CombatShields.IsShield(new Item { Code = new AssetLocation(code) }),
                code + ": shield remains recognized when a mod replaces its class/tool");
        foreach (string code in new[] { "game:stick", "armory:sword-short-plain-steel", "game:shieldrack" })
            Check(!CombatShields.IsShield(new Item { Code = new AssetLocation(code) }), code + ": not a shield");
        foreach (Command command in Enum.GetValues<Command>())
        foreach (Phase phase in Enum.GetValues<Phase>())
        {
            Check(!CombatShields.AllowsInput(command, false, false, phase), "No weapon or shield cannot own combat input");
            Check(CombatShields.AllowsInput(command, true, false, phase), "Weapon controls remain available");
            bool permitted = CombatShields.AllowsInput(command, false, true, phase);
            if (command == Command.Begin || command == Command.Release)
                Check(!permitted, "An empty hand/tool plus shield cannot create an unauthorized attack");
            if (command == Command.Block) Check(permitted, "Shield raises without a supported mainhand weapon");
            if (phase == Phase.Block && command is Command.Aim or Command.KeepAlive or Command.Cancel)
                Check(permitted, "Empty mainhand must not stop shield aiming/heartbeat/release");
        }
        var unarmedGuard = new Fighter();
        unarmedGuard.SetShield(true, 0);
        foreach (var (command, time) in new[] { (Command.Block, 0L), (Command.Aim, 170L),
            (Command.KeepAlive, 200L), (Command.KeepAlive, 900L), (Command.KeepAlive, 1600L) })
        {
            Check(CombatShields.AllowsInput(command, false, true, unarmedGuard.Phase), "Unarmed shield server input accepted");
            Check(unarmedGuard.Apply(command, Direction.Left, time, WeaponTiming.Sword), "Unarmed shield state progresses");
        }
        Check(unarmedGuard.CanGuard(1600), "Shield remains raised across heartbeats with empty mainhand");
        var f = new Fighter();
        f.SetShield(true, 0);
        Check(f.Apply(Command.Block, Direction.Left, 0, WeaponTiming.Sword), "Raise shield");
        Check(!f.CanGuard(179), "Shield cannot block before raise completes");
        f.Apply(Command.Aim, Direction.Right, 100, WeaponTiming.Sword);
        f.Apply(Command.Block, Direction.Thrust, 150, WeaponTiming.Sword);
        Check(f.Started == 0 && f.CanGuard(180), "Turning while blocking does not restart shield raise");
        Check(!f.Apply(Command.Begin, Direction.Right, 181, WeaponTiming.Sword), "No attack from held shield");
        f.Apply(Command.Cancel, Direction.Right, 190, WeaponTiming.Sword);
        Check(!f.CanGuard(190), "Shield stops blocking immediately on release");
        Check(!f.Apply(Command.Begin, Direction.Right, 269, WeaponTiming.Sword), "Guard release has a lockout");
        Check(f.Apply(Command.Begin, Direction.Right, 270, WeaponTiming.Sword), "Attack after shield release");
        f.Apply(Command.Release, Direction.Right, 550, WeaponTiming.Sword);
        long end = f.Ends;
        f.SetShield(false, 560);
        Check(!f.CanHit(f.HitAt) && f.Ends == end, "Unequipping shield cannot bypass committed recovery");
        f.Advance(end);
        f.SetShield(true, end);
        f.Apply(Command.Block, Direction.Left, end, WeaponTiming.Sword);
        f.SetShield(false, end + 200); f.Stun(end + 200, CombatRules.ShieldBreak);
        Check(!f.CanGuard(end + 201), "Broken shield cannot keep guarding");
        Check(!f.Apply(Command.Begin, Direction.Left, end + 549, WeaponTiming.Sword), "Shield break recovery");
        Check(f.Apply(Command.Begin, Direction.Left, end + 550, WeaponTiming.Sword), "Shield break recovery expires");
        Check(CombatRules.ShieldWear(.1f) == 1 && CombatRules.ShieldWear(4.1f) == 5,
            "Shield wear scales with incoming damage");
        Check(CombatRules.ShieldWear(500) == 100, "Shield wear has a bound");
        Check(CombatRules.ShieldBounce < CombatRules.ParryStun, "Directional parry keeps its advantage");
        foreach (double yaw in new[] { 0.0, Math.PI / 2, Math.PI, 3 * Math.PI / 2 })
        {
            var forward = Fighter.PlayerForward(yaw);
            Check(Fighter.Faces(forward.X, forward.Z, forward.X, forward.Z, .5), "Shield faces attacker");
            Check(!Fighter.Faces(forward.X, forward.Z, -forward.X, -forward.Z, .5), "Rear bypasses shield");
            Check(!Fighter.Faces(forward.X, forward.Z, forward.Z, -forward.X, .5), "Side bypasses shield");
        }
        Console.WriteLine($"PASS: {checks} shield balance checks");
    }
}
