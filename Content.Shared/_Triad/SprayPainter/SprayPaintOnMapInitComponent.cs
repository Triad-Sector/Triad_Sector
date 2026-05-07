using Content.Shared.SprayPainter.Components;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.SprayPainter;

/// <summary>
///       Auto applies a spray paint style on map init.
///       The style field is an EntProtoId with a PaintableComponent.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class SprayPaintOnMapInitComponent : Component
{
    /// <summary>
    /// The style that this entity is painted with on map init.
    /// </summary>
    [DataField(required: true), AutoNetworkedField]
    public EntProtoId<PaintableComponent> Style = string.Empty;
}
