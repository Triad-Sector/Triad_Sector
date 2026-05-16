using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._HL.Shipyard;

/// <summary>
/// This is used for saving a VesselPrototype on saved ships.
/// </summary>
[Obsolete("Use VesselComponent")]
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class HLSavedVesselPrototypeComponent : Component
{
    [DataField, AutoNetworkedField]
    public ProtoId<VesselPrototype> VesselId = "Antlion"; // Default fallback ID, changed to Antlion by Triad
}
