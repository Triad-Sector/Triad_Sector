using Content.Shared.Tools;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.Tools.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class DisableToolUseComponent : Component
{
    [DataField, AutoNetworkedField]
    public List<ProtoId<ToolQualityPrototype>> Disabled = new();

    [DataField, AutoNetworkedField]
    public bool DisableAll = false;

    [DataField, AutoNetworkedField]
    public LocId? Popup = null;
}
