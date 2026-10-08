using Zornhau;

int count = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    count++;
}
var timing = WeaponTiming.Sword;
var f = new Fighter();
Check(!f.Apply(Command.Release, Direction.Right, 0, timing), "Release without a windup must fail");
Check(f.Apply(Command.Begin, Direction.Right, 0, timing), "Start windup");
Check(!f.TakeHit(100), "Holding must not deal damage");
Check(f.Apply(Command.Release, Direction.Right, 50, timing), "Quick tap schedules a strike");
Check(f.HitAt == 420, "Quick taps must respect minimum windup and contact delay");
Check(!f.TakeHit(419), "Cannot hit early");
Check(f.TakeHit(420) && !f.TakeHit(420), "Exactly one contact per attack");
Check(!f.Apply(Command.Begin, Direction.Right, 500, timing), "Recovery prevents attack spam");
Check(!f.Apply(Command.Block, Direction.Left, 500, timing), "Committed attacks cannot be cancelled into block");
f.Advance(780);
Check(f.Apply(Command.Begin, Direction.Overhead, 780, timing), "Attack becomes available after recovery");
Check(f.Apply(Command.Block, Direction.Overhead, 900, timing), "Windup can be feinted into guard");
Check(!f.TakeHit(1400), "Feint cannot cause damage");
f.Advance(1701);
Check(f.Phase == Phase.Idle, "Disconnected guards must expire");
f.Apply(Command.Begin, Direction.Left, 2000, timing);
f.Apply(Command.KeepAlive, Direction.Left, 2400, timing);
f.Advance(2800);
Check(f.Phase == Phase.Windup, "Heartbeat allows held windup");
f.Apply(Command.Release, Direction.Left, 2800, timing);
Check(f.Charge == 1, "Server clock determines full charge");
f.Invalidate(2900);
Check(!f.TakeHit(3000), "Weapon switches cancel pending contact");
Check(!f.Apply(Command.Begin, Direction.Right, 3000, timing), "Weapon switches cannot bypass recovery");
f.Stun(3000, 480);
Check(!f.Apply(Command.Begin, Direction.Thrust, 3479, timing), "Parry stagger prevents immediate retaliation");
Check(f.Apply(Command.Begin, Direction.Thrust, 3480, timing), "Stagger expires");
Check(!f.Apply((Command)99, Direction.Left, 3500, timing), "Invalid commands rejected");
Check(!f.Apply(Command.Aim, (Direction)99, 3500, timing), "Invalid directions rejected");
Check(Fighter.FromMouse(2, 1, Direction.Thrust) == Direction.Thrust, "Mouse noise must not change direction");
Check(Fighter.FromMouse(10, 3, Direction.Thrust) == Direction.Right, "Horizontal direction");
Check(Fighter.FromMouse(-10, 3, Direction.Thrust) == Direction.Left, "Left direction");
Check(Fighter.FromMouse(1, -10, Direction.Left) == Direction.Overhead, "Upper direction");
Check(Fighter.FromMouse(1, 10, Direction.Left) == Direction.Thrust, "Lower direction");
Check(Fighter.GuardFor(Direction.Left) == Direction.Right && Fighter.GuardFor(Direction.Right) == Direction.Left,
    "Horizontal guard must mirror attacker's view");
Check(Fighter.GuardFor(Direction.Overhead) == Direction.Overhead && Fighter.GuardFor(Direction.Thrust) == Direction.Thrust,
    "Vertical guard matches incoming attack");
Check(Fighter.Faces(0, 1, 0, 3), "Front attacks can be parried");
Check(!Fighter.Faces(0, 1, 0, -3), "Back attacks bypass parry");
Check(!Fighter.Faces(0, 1, 3, 0), "Side attacks bypass narrow guard");
Console.WriteLine($"PASS: {count} combat checks");
AnimationGeometry.Check();
BalanceChecks.Run();
SprintChecks.Run();
TurnChecks.Run();
AimChecks.Run();
ContactWindowChecks.Run();
ToolInteractionChecks.Run();
PresentationChecks.Run();
ShieldChecks.Run();

HitDiagnosticChecks.Run();
