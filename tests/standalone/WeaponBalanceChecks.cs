using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using WarbandCombat;

static class WeaponBalanceChecks
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var runtime = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, WeaponBalanceProfile[]>>>(
            File.ReadAllText(Path.Combine(root, "assets/warbandcombat/config/balance-profiles.json")))!;
        var policy = JObject.Parse(File.ReadAllText(Path.Combine(root, "balance/policy.json")));
        var catalog = JArray.Parse(File.ReadAllText(Path.Combine(root, "balance/catalog.json")));
        int profilesChecked = 0;
        foreach (var context in runtime.Values)
        foreach (var (code, profiles) in context)
        {
            // The catalog supplies the real tool enum; stats come exclusively
            // from server-owned compiled profiles, never from a contact packet.
            var entry = catalog.OfType<JObject>().First(row => row["code"]!.Value<string>() == code);
            var item = new Item { Code = new(code), Tool = Enum.TryParse<EnumTool>(entry["tool"]?.Value<string>(), out var tool) ? tool : null };
            CombatBalance.Register(item, profiles);
            var player = new Holder(); player.Right.Itemstack = new ItemStack(item);
            foreach (var profile in profiles)
            {
                player.Left.Itemstack = profile.Stance == "two" ? null
                    : new ItemStack(new Item { Code = new(profile.Stance.StartsWith("dual:")
                        ? profile.Stance[5..].Replace("*", "test") : "game:torch-basic-lit") });
                player.WatchedAttributes.SetString("warbandWeaponMode", profile.Mode);
                check(ReferenceEquals(CombatBalance.For(player, profile.Mode), profile), "Current mode and offhand choose the real audited stance");
                check(CombatBalance.CanAttack(player, profile.Mode) == profile.CanAttack, "An unavailable stance cannot begin an attack");
                check(CombatSystem.Timing(player, profile.Mode) == profile.Timing, "Client/server prediction uses the stance's balanced timings");
                if (!profile.CanAttack) { check(CombatBalance.Power(player, Direction.Right, 100, profile.Mode) == 0, "Disabled stances cannot acquire damage through a forged amount"); continue; }
                profilesChecked++;
                int cycle = profile.Windup + profile.Strike + CombatRules.ContactWindow + profile.Recovery;
                double sum = 0;
                foreach (Direction direction in Enum.GetValues<Direction>())
                {
                    float damage = CombatBalance.Power(player, direction, profile.SourcePower[(int)direction], profile.Mode)
                        * CombatBalance.DirectionBonus(item.Tool, direction);
                    sum += damage;
                    check(Math.Abs(CombatBalance.Power(player, direction, profile.SourcePower[(int)direction] / 4, profile.Mode)
                        - profile.Power / 4) < .00001, "A weak haft/pommel contact retains its quarter-strength damage");
                    foreach (double contact in new[] { 0, .5, 1 })
                    {
                        var fighter = new Fighter();
                        fighter.Apply(Command.Begin, direction, 1000, CombatSystem.Timing(player, profile.Mode));
                        fighter.Apply(Command.Release, direction, 1001, CombatSystem.Timing(player, profile.Mode));
                        long now = fighter.HitAt + (long)((fighter.ContactEnds - fighter.HitAt) * contact);
                        check(fighter.TakeHit(now) && !fighter.CanHit(now), "The first contact immediately closes the physical cut");
                        check(fighter.Ends == 1000 + cycle, "Early/middle/late contact has the same committed DPS cycle");
                        check(!fighter.Apply(Command.Begin, direction, fighter.Ends - 1, profile.Timing)
                            && fighter.Apply(Command.Begin, direction, fighter.Ends, profile.Timing), "Next swing is prohibited until the exact cycle deadline");
                    }
                }
                string material = profile.Class[..profile.Class.LastIndexOf('-')];
                double goal = policy["materials"]![material]!["dps"]!.Value<double>()
                    * (profile.Stance == "two" ? policy["twoHandedDpsMultiplier"]!.Value<double>() : 1)
                    * policy["families"]![profile.Family]!["dpsMultiplier"]!.Value<double>();
                check(Math.Abs(sum / 4 * 1000 / cycle - goal) < .00002, "Actual compiled server power/cadence matches the policy's class DPS");
                if (material == "iron" && profile.Family is "knife" or "sword" or "greatsword")
                {
                    int expected = profile.Family == "knife" ? 4 : profile.Family == "sword" ? 3 : 2;
                    check(Math.Ceiling(15 / profile.Power) == expected, "Iron knife/sword/greatsword preserve distinct four/three/two side-hit kill thresholds");
                }
            }
            if (profiles.Any(p => p.Mode.Length > 0))
            {
                check(!CombatBalance.CanAttack(player, "forged-mode"), "A forged mode never inherits a valid damage profile");
            }
        }
        Console.WriteLine($"Checked {profilesChecked} enabled audited weapon/mode/grip balance profiles on production code");
    }
}
