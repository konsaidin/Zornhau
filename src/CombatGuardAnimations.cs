namespace Zornhau;

internal static class CombatGuardAnimations
{
    internal static string Code(Direction direction, bool twoHanded = false)
        => "zornhau:zornhau-block-" + (twoHanded ? "2h-" : "") + direction.ToString().ToLowerInvariant();
}
