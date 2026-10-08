using Zornhau;

internal static class ToolInteractionChecks
{
    internal static void Run()
    {
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            count++;
        }
        foreach (bool workTool in new[] { false, true })
        foreach (bool block in new[] { false, true })
        foreach (bool corpse in new[] { false, true })
        foreach (bool living in new[] { false, true })
        foreach (bool inCombat in new[] { false, true })
        {
            bool useTool = CombatRules.UseToolInteraction(workTool, block, corpse, living, inCombat);
            if (!workTool || living || inCombat)
                Check(!useTool, "Weapons, live opponents and ongoing combat gestures must not switch to tool actions");
            else
                Check(useTool == (block || corpse), "Idle axe/knife gestures must use vanilla actions on blocks and corpses");
            if (useTool)
                Check(CombatRules.RejectVanillaPvpMelee(true, false, true, true),
                    "Tool actions must not open a vanilla melee PvP bypass if the player turns toward an opponent");
        }
        var fighter = new Fighter();
        fighter.Apply(Command.Begin, Direction.Thrust, 0, WeaponTiming.Sword);
        fighter.Apply(Command.Release, Direction.Thrust, 1, WeaponTiming.Sword);
        Check(!CombatRules.UseToolInteraction(true, true, false, false, fighter.Phase != Phase.Idle),
            "Turning toward a block must preserve the committed strike and late-contact window");
        fighter.Invalidate(200);
        Check(!CombatRules.UseToolInteraction(true, true, false, false, fighter.Phase != Phase.Idle),
            "Changing weapons cannot use auto tool mode to escape combat recovery");
        fighter.Advance(fighter.Ends);
        Check(CombatRules.UseToolInteraction(true, true, false, false, fighter.Phase != Phase.Idle),
            "Normal tool use must become available after recovery finishes");
        Console.WriteLine($"PASS: {count} tool interaction checks");
    }
}
