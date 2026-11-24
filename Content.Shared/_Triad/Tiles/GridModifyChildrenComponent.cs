using Content.Shared.Whitelist;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.Tiles;

/// <summary>
/// Component given to a grid. On map init, it'll apply or remove extra components to its children on mapinit.
/// Useful for protecting certain entities on grids
/// </summary>
[RegisterComponent]
public sealed partial class GridModifyChildrenComponent : Component
{
    [DataField(required: true)]
    public EntityWhitelist ModifyWhitelist = new();

    [DataField]
    public ComponentRegistry? AddComponents;

    [DataField]
    public ComponentRegistry? RemoveComponents;
}
