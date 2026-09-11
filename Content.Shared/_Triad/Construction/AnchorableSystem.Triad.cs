using Content.Shared._Triad.Construction;

namespace Content.Shared.Construction.EntitySystems;

public sealed partial class AnchorableSystem : EntitySystem
{
    private bool ShouldIgnoreTileFreeCheck(EntityUid entity)
    {
        return HasComp<IgnoreAnchorableTileFreeComponent>(entity);
    }
}
