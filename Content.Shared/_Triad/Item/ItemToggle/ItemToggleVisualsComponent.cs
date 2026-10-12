using Robust.Shared.GameStates;

namespace Content.Shared._Triad.Item.ItemToggle;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ItemToggleVisualsComponent : Component
{
    [DataField, AutoNetworkedField]
    public string? OnPrefix = "on";

    [DataField, AutoNetworkedField]
    public string? OffPrefix = null;
}
