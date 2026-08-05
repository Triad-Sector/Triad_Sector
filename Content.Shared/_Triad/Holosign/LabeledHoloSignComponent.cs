using Robust.Shared.GameStates;

namespace Content.Shared._Triad.Holosign;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class LabeledHoloSignComponent : Component
{
    [DataField, AutoNetworkedField]
    public string Description = string.Empty;

    [DataField, AutoNetworkedField]
    public bool IsExplicit;
}
