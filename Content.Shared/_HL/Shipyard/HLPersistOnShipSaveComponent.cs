using Robust.Shared.GameStates;

namespace Content.Shared._HL.Shipyard;

/// <summary>
/// Entities with this component will persist themselves and their contents when the ship is saved.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class HLPersistOnShipSaveComponent : Component
{
    /// <summary>
    ///      Whether or not this entity will also persist its contents on ship save.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool SaveContents = false;
}
