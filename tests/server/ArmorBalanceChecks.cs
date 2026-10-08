using CombatOverhaul.DamageSystems;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Zornhau;

internal static class ArmorBalanceChecks
{
    internal static void Run()
    {
        var catalog = JObject.Parse(File.ReadAllText("balance/armor-catalog.json"));
        var policy = JObject.Parse(File.ReadAllText("balance/policy.json"));
        var profiles = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, WeaponBalanceProfile[]>>>(
            File.ReadAllText("assets/zornhau/config/balance-profiles.json"))!["co"];
        var weapons = JArray.Parse(File.ReadAllText("balance/catalog.json"));
        var items = catalog["items"]!.ToDictionary(x => (string)x["code"]!);
        var table = catalog["damageReduction"]!.ToObject<float[][]>()!;
        var saved = (DamageResistData.DamageReduction, DamageResistData.MaxAttackTier, DamageResistData.MaxArmorTier);
        DamageResistData.DamageReduction = table;
        DamageResistData.MaxAttackTier = table[0].Length; DamageResistData.MaxArmorTier = table.Length;
        int checks = 0;
        var csv = File.ReadAllLines("balance/armor-matchups.csv");
        var header = csv[0].Split(',');
        int Column(string name) => Array.IndexOf(header, name);
        var expected = csv.Skip(1).Select(line => line.Split(',')).ToDictionary(
            row => string.Join('|', row.Take(3).Concat(new[] { row[Column("direction")], row[Column("armor")] })),
            row => float.Parse(row[Column("damage")], System.Globalization.CultureInfo.InvariantCulture));
        void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); checks++; }
        try
        {
            var sets = policy["armor"]!["sets"]!.ToDictionary(x => (string)x["id"]!, x =>
                DamageResistData.Combine(x["items"]!.Select(code =>
                {
                    var r = items[(string)code!]["resists"]!;
                    return new DamageResistData(r["Resists"]!.ToObject<Dictionary<EnumDamageType, float>>()!,
                        r["FlatDamageReduction"]!.ToObject<Dictionary<EnumDamageType, float>>()!);
                })));
            foreach (var entry in weapons.Where(x => (string)x["context"]! == "co" && (bool)x["canAttack"]!))
            {
                string code = (string)entry["code"]!, mode = (string)entry["mode"]!, stance = (string)entry["stance"]!;
                var item = new Item { Code = new(code), Tool = Enum.TryParse<EnumTool>((string?)entry["tool"], out var tool) ? tool : null };
                CombatBalance.Register(item, profiles[code]);
                var player = new Holder(); player.Right.Itemstack = new ItemStack(item);
                if (stance != "two") player.Left.Itemstack = new ItemStack(new Item { Code = new(stance.StartsWith("dual:")
                    ? stance[5..].Replace("*", "long-plain-iron") : "game:torch") });
                var profile = CombatBalance.For(player, mode)!;
                Check(profile.Stance == stance, "Real profile selects benchmark grip");
                foreach (Direction direction in Enum.GetValues<Direction>())
                {
                    var contact = entry["directions"]![direction.ToString()]!;
                    var type = Enum.Parse<EnumDamageType>((string)contact["type"]!);
                    int tier = (int)contact["tier"]!, native = (int)contact["ap"]!;
                    int ap = CombatBalance.ArmorPiercing(player, type, native, mode);
                    float raw = CombatBalance.Power(player, direction, (float)contact["power"]!, mode)
                        * CombatBalance.DirectionBonus(item.Tool, direction);
                    Check(ap >= native, "Native high AP is never removed");
                    foreach (var (name, resists) in sets)
                    {
                        float damage = raw;
                        resists.ApplyPlayerResist(new(type, tier, ap), ref damage, out _);
                        Check(Math.Abs(damage - expected[$"{code}|{mode}|{stance}|{direction}|{name}"]) < .00002,
                            "Published damage matrix matches the real pinned CO armor implementation");
                        Check(damage >= 0 && damage <= raw && float.IsFinite(damage), "Real armor cannot increase or invalidate physical damage");
                        if (name == "bare") Check(Math.Abs(damage - raw) < .00001, "Unarmored target preserves the existing damage budget");
                        if (name is "full-iron" or "full-steel" && profile.Class.StartsWith("iron-") && profile.Family is "sword" or "greatsword")
                            Check(damage <= raw * .4f + .00001, "Heavy layered armor substantially prolongs an iron sword fight");
                        if (name == "full-steel" && profile.Class.StartsWith("steel-") && profile.Family is "sword" or "greatsword")
                            Check(damage <= raw * .5f + .00001, "Steel swords cannot nullify a complete heavy steel set");
                    }
                }
            }
        }
        finally
        {
            (DamageResistData.DamageReduction, DamageResistData.MaxAttackTier, DamageResistData.MaxArmorTier) = saved;
        }
        Console.WriteLine($"PASS: {checks} loaded CO armor and server weapon profile checks");
    }
}
