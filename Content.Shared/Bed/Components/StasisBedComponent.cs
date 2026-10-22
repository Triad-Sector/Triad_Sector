using Content.Shared.Buckle.Components;
using Content.Shared.Construction.Prototypes;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.Bed.Components;

/// <summary>
/// A <see cref="StrapComponent"/> that modifies a strapped entity's metabolic rate by the given multiplier
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(SharedBedSystem))]
public sealed partial class StasisBedComponent : Component
{
    /// <summary>
    /// What the metabolic update rate will be multiplied by (higher = slower metabolism)
    /// </summary>
    [DataField, AutoNetworkedField]
    public float Multiplier = 10f;

    // Triad start - upgradeable parts
    [DataField, AutoNetworkedField]
    public float BaseMultiplier = 10f;

    [DataField]
    public ProtoId<MachinePartPrototype> MachinePartMetabolismModifier = "Capacitor";
    // Triad end
}
