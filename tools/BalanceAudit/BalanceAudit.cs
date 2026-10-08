using System.Collections;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

// A separate developer-only mod. Run it in an isolated headless profile to
// export the game's resolved variants/ByType patches, not guessed raw JSON.
public sealed class BalanceAudit : ModSystem
{
    public override double ExecuteOrder() => 1000;
    public override void StartServerSide(ICoreServerAPI api)
        => api.Event.ServerRunPhase(EnumServerRunPhase.GameReady, () => Export(api));

    private static void Export(ICoreAPI api)
    {
        string? destination = Environment.GetEnvironmentVariable("ZORNHAU_BALANCE_AUDIT");
        if (api.Side != EnumAppSide.Server || destination == null) return;
        var items = new JArray();
        var armors = new JArray();
        foreach (var item in api.World.Items)
        {
            if (item?.Code == null) continue;
            var attrs = item.Attributes == null ? new JObject() : JObject.Parse(item.Attributes.ToString()!);
            var armor = item.CollectibleBehaviors?.FirstOrDefault(b => b.GetType().FullName == "CombatOverhaul.Armor.ArmorBehavior");
            if (armor != null)
                armors.Add(new JObject {
                    ["code"] = item.Code.ToString(), ["durability"] = item.GetMaxDurability(new ItemStack(item)),
                    ["type"] = JToken.FromObject(Read(armor, "ArmorType")!),
                    ["resists"] = JToken.FromObject(Read(armor, "Resists")!),
                    ["resistsByType"] = JToken.FromObject(Read(armor, "ResistsByType")!),
                    ["stats"] = JToken.FromObject(Read(armor, "Stats")!)
                });
            else if (attrs["protectionModifiers"] is JObject protection)
                armors.Add(new JObject { ["code"] = item.Code.ToString(), ["protection"] = protection });
            if (item.Tool is not (EnumTool.Sword or EnumTool.Spear or EnumTool.Axe or EnumTool.Knife)
                && !attrs.Properties().Any(p => p.Name is "OneHandedStance" or "TwoHandedStance" or "Modes")) continue;
            var stack = new ItemStack(item);
            items.Add(new JObject {
                ["code"] = item.Code.ToString(), ["tool"] = item.Tool?.ToString(),
                ["power"] = item.GetAttackPower(stack), ["tier"] = item.ToolTier,
                ["durability"] = item.GetMaxDurability(stack), ["attributes"] = attrs
            });
        }
        var recipes = new JObject();
        foreach (string name in new[] { "GetGridRecipes", "GetSmithingRecipes", "GetKnappingRecipes", "GetClayformingRecipes" })
        {
            var method = AppDomain.CurrentDomain.GetAssemblies().SelectMany(Types)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == 1
                    && (m.GetParameters()[0].ParameterType.IsInstanceOfType(api)
                        || m.GetParameters()[0].ParameterType.IsInstanceOfType(api.World)));
            var entries = new JArray();
            object? collection = name == "GetGridRecipes" ? api.World.GridRecipes
                : method?.Invoke(null, new object[] { method.GetParameters()[0].ParameterType.IsInstanceOfType(api) ? api : api.World });
            if (collection is IEnumerable values)
                foreach (var value in values) entries.Add(Recipe(value!));
            recipes[name] = entries;
        }
        var resistType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(Types)
            .FirstOrDefault(t => t.FullName == "CombatOverhaul.DamageSystems.DamageResistData");
        File.WriteAllText(destination, new JObject { ["items"] = items, ["recipes"] = recipes,
            ["armors"] = armors, ["armorDamageReduction"] = resistType == null ? null
                : JToken.FromObject(resistType.GetProperty("DamageReduction")!.GetValue(null)!) }.ToString(Formatting.None));
        api.Logger.Notification("[Balance Audit] Exported {0} melee/tool variants to {1}", items.Count, destination);
    }

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { return error.Types.OfType<Type>(); }
    }

    private static object? Read(object target, string name) => target.GetType().GetProperty(name)?.GetValue(target)
        ?? target.GetType().GetField(name)?.GetValue(target);
    private static JObject Ingredient(object target)
    {
        var result = new JObject();
        foreach (string name in new[] { "Code", "Name", "Quantity", "Type", "AllowedVariants", "IsTool", "ToolDurabilityCost" })
        {
            var value = Read(target, name);
            if (value != null) result[name] = value is Array ? JToken.FromObject(value) : JToken.FromObject(value is string or int or bool ? value : value.ToString()!);
        }
        return result;
    }
    private static JObject Recipe(object recipe)
    {
        var result = new JObject { ["type"] = recipe.GetType().Name, ["name"] = Read(recipe, "Name")?.ToString() };
        if (Read(recipe, "Output") is { } output) result["output"] = Ingredient(output);
        if (Read(recipe, "Ingredient") is { } ingredient) result["ingredient"] = Ingredient(ingredient);
        if (Read(recipe, "Ingredients") is IDictionary ingredients)
        {
            var all = new JObject();
            foreach (DictionaryEntry entry in ingredients) if (entry.Value != null) all[entry.Key.ToString()!] = Ingredient(entry.Value);
            result["ingredients"] = all;
        }
        if (Read(recipe, "Pattern") is { } pattern) result["pattern"] = JToken.FromObject(pattern);
        if (Read(recipe, "IngredientPattern") is { } grid) result["grid"] = grid.ToString();
        return result;
    }
}
