using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.Shipyard.Load;

/// <summary>
/// Replaces this entity with a prototype-fresh one when a ship comes back, on retrieve and on
/// import. Everything the save preserved is discarded, so it only fits state that cannot ride a
/// save at all.
/// </summary>
/// <remarks>
/// AI cores only. A core stores empty because minds are not saved, and an empty core does not
/// re-offer its ghost role, so respawning it is what makes it joinable. Gravity generators and AME
/// controllers carried this until they round-tripped correctly; after that it only destroyed state,
/// and the AME's replacement was the filled, injecting variant.
/// </remarks>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class SpawnOnShipLoadComponent : Component
{
    [DataField, AutoNetworkedField]
    public EntProtoId Spawn;

    [DataField, AutoNetworkedField]
    public bool DeleteSelfAfterSpawn = true;
}
