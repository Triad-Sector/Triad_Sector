using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.Shipyard;

/// <summary>
/// Spawns an entity when a ship is loaded from a player's saved ships.
/// Optional field to delete the entity after spawning, true by default.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class SpawnOnShipLoadComponent : Component
{
    [DataField, AutoNetworkedField]
    public EntProtoId Spawn;

    [DataField, AutoNetworkedField]
    public bool DeleteSelfAfterSpawn = true;
}
