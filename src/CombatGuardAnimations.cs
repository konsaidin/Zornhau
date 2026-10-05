namespace WarbandCombat;

internal static class CombatGuardAnimations
{
    internal static string Code(Direction direction, bool twoHanded = false)
        => "warbandcombat:warband-block-" + (twoHanded ? "2h-" : "") + direction.ToString().ToLowerInvariant();
}
