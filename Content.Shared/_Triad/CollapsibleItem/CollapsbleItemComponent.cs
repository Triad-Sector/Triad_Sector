using Content.Shared.Tools;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.CollapsibleItem;

/// <summary>
///     Component for items that 'collapse' into a smaller form, like a flatpack.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class CollapsibleItemComponent : Component
{
    [DataField(required: true), AutoNetworkedField]
    public EntProtoId CollapseInto;

    [DataField, AutoNetworkedField]
    public ProtoId<ToolQualityPrototype> ToolQuality = "Welding";

    /// <summary>
    ///     The fuel amount needed to collapse it.
    /// </summary>
    [DataField, AutoNetworkedField]
    public int FuelCost = 5;

    [DataField, AutoNetworkedField]
    public TimeSpan DoAfter = TimeSpan.FromSeconds(2);
}
