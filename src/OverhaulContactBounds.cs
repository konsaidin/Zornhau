using CombatOverhaul.Colliders;
using Vintagestory.API.Common;

namespace Zornhau;

internal sealed class OverhaulContactBounds(ICoreAPI api) : ModelContactBounds(api,
    target => new HashSet<string>(target.GetBehavior<CollidersEntityBehavior>()?.CollidersTypes.Keys
        ?? Enumerable.Empty<string>(), StringComparer.Ordinal));
