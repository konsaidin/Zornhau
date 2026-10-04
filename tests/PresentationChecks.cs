using WarbandCombat;
using Vintagestory.API.Common;

internal static class PresentationChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        foreach (int latency in new[] { 0, 30, 80, 150, 300 })
        {
            var fighter = new Fighter();
            var view = new CombatPresentation();
            var clips = new CombatClipSelection();
            int swingStarts = 0, recoveryStarts = 0;
            void Draw(long now)
            {
                var action = view.Action(fighter, now);
                if (!clips.Change(action)) return;
                if (action == "swing") swingStarts++;
                if (action == "recovery") recoveryStarts++;
            }
            fighter.Apply(Command.Begin, Direction.Right, 1000, WeaponTiming.Sword); view.Begin(); Draw(1000);
            fighter.Apply(Command.Release, Direction.Right, 1050, WeaponTiming.Sword);
            Draw(1280); Draw(1290);
            // A release acknowledgement projected at receipt used to return to windup.
            fighter.Restore(Phase.Swing, Direction.Right, 1300, 300, 140 + latency, 550 + latency, 1,
                false, 190 + latency);
            Draw(1300); Draw(1300 + latency); Draw(1440 + latency);
            fighter.Restore(Phase.Recovery, Direction.Right, 1500 + latency, 500, -80, 300, 1, true, -30);
            for (long now = 1500 + latency; now < 1800 + latency; now += 20) Draw(now);
            fighter.Advance(1800 + latency); Draw(1800 + latency);
            // An old same-sequence swing/recovery after completion cannot replay it.
            fighter.Restore(Phase.Swing, Direction.Right, 1801 + latency, 200, 10, 200, 1); Draw(1801 + latency);
            Check(swingStarts == 1 && recoveryStarts == 1, $"One swing and recovery at {latency} ms latency");
            fighter.Reset(); fighter.Apply(Command.Begin, Direction.Left, 2200, WeaponTiming.Sword); view.Begin(); Draw(2200);
            fighter.Apply(Command.Release, Direction.Left, 2300, WeaponTiming.Sword); Draw(2480);
            Check(swingStarts == 2, "A new accepted attack can start another swing");
            long clientSent = 1000, serverReceived = 5000 + latency / 2, serverSent = serverReceived + 20;
            Check(CombatPresentation.SnapshotAge(1020 + latency, clientSent, serverReceived, serverSent) == latency,
                "Snapshot projection removes both input/ack transit from prediction");
        }
        var selector = new CombatClipSelection();
        Check(selector.Change("recovery"), "Recovery starts");
        for (int i = 0; i < 100; i++) Check(!selector.Change("recovery"), "Finished one-shot must not restart every tick");
        Check(selector.Change(null) && selector.Change("windup"), "Idle then a new windup transitions normally");
        Check(CombatPresentation.SnapshotAge(100, 0, 0, 0) == 0, "Initial snapshots without an input echo are supported");
        var own = new TestAnimationManager();
        own.OnStartAnimation += CombatAnimations.OnLocalAnimationStart;
        CombatAnimations.StartOwnedAnimation(own, "warband-swing-right");
        Check(own.IsAnimationActive("warband-swing-right"), "Predicted local swing is permitted by the actual API event");
        own.ActiveAnimationsByAnimCode.Clear(); // Engine retires a completed one-shot.
        foreach (string code in new[] { "warband-swing-right", "warband-swing-right-fp", "warband-windup-left", "warband-recovery-right" })
            Check(!own.StartAnimation(new AnimationMetaData { Code = code, Animation = code }),
                "Delayed server clip cannot restart on the owning player");
        CombatAnimations.StartOwnedAnimation(own, "warband-swing-left");
        Check(own.IsAnimationActive("warband-swing-left"), "Next local swing is permitted");
        Check(own.StartAnimation(new AnimationMetaData { Code = "walk", Animation = "walk" }), "Walking still synchronizes");
        var remote = new TestAnimationManager();
        Check(remote.StartAnimation(new AnimationMetaData { Code = "warband-swing-right", Animation = "warband-swing-right" }),
            "Other players keep server animation replication");
        foreach (var timing in new[] { WeaponTiming.Sword, new WeaponTiming(420, 170, 480), new WeaponTiming(180, 100, 260) })
        foreach (Direction direction in Enum.GetValues<Direction>())
        {
            var player = new TestPlayer();
            var fighter = new Fighter();
            fighter.Apply(Command.Begin, direction, 1000, timing);
            CombatAnimations.Update(player, fighter, 1000);
            fighter.Apply(Command.Release, direction, 1001, timing);
            long swingStart = 1000 + timing.Windup;
            CombatAnimations.Update(player, fighter, swingStart);
            player.Manager.ActiveAnimationsByAnimCode.Clear(); // A finished engine clip.
            for (long now = swingStart + 20; now <= fighter.ContactEnds; now += 20)
                CombatAnimations.Update(player, fighter, now);
            fighter.Advance(fighter.ContactEnds + 1);
            CombatAnimations.Update(player, fighter, fighter.ContactEnds + 1);
            player.Manager.ActiveAnimationsByAnimCode.Clear();
            for (long now = fighter.ContactEnds + 20; now < fighter.Ends; now += 20)
                CombatAnimations.Update(player, fighter, now);
            string suffix = direction.ToString().ToLowerInvariant();
            Check(player.Manager.Starts["warband-swing-" + suffix] == 1, "Production updater never repeats a retired swing");
            Check(player.Manager.Starts["warband-recovery-" + suffix] == 1, "Production updater never repeats a retired recovery");
        }
        Console.WriteLine($"PASS: {checks} multiplayer presentation checks");
    }

    private sealed class TestAnimationManager : AnimationManager
    {
        internal readonly Dictionary<string, int> Starts = new();
        public override bool StartAnimation(string code)
        {
            Starts[code] = Starts.GetValueOrDefault(code) + 1;
            return StartAnimation(new AnimationMetaData { Code = code, Animation = code });
        }
        public override void StopAnimation(string code) => ActiveAnimationsByAnimCode.Remove(code);
    }

    private sealed class TestPlayer : EntityPlayer
    {
        internal readonly TestAnimationManager Manager = new();
        public override IAnimationManager AnimManager { get => Manager; set { } }
    }
}
