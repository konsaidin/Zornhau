using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace Zornhau;

internal static class CombatPrediction
{
    [ThreadStatic] private static int depth;
    internal static bool Active => depth > 0;

    internal static Scope Begin() { depth++; return new Scope(); }
    internal readonly struct Scope : IDisposable
    {
        public void Dispose() => depth--;
    }

    internal static void Patch(Harmony harmony)
        => harmony.Patch(AccessTools.Method(typeof(Entity), nameof(Entity.ReceiveDamage)),
            prefix: new HarmonyMethod(typeof(CombatPrediction), nameof(BeforeDamage)));

    private static bool BeforeDamage(Entity __instance, ref bool __result)
    {
        // CO creates its packet independently of ReceiveDamage's result. Wait
        // for the authoritative hurt counter instead of painting a parry red.
        if (depth == 0 || __instance.Api?.Side != EnumAppSide.Client) return true;
        __result = false; return false;
    }
}
