using Robust.Shared.GameStates;

namespace Content.Shared._Triad.ContrabandPermit;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ContrabandPermitItemComponent : Component
{
    /// <summary>
    /// The name of the permit owner.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string PermitOwnerName = string.Empty;

    [DataField, AutoNetworkedField]
    public string PermitReason = string.Empty;

    /// <summary>
    /// The UID of the permit owner. Session-local, re-stamped on ship load; never serialized.
    /// </summary>
    [AutoNetworkedField]
    public EntityUid? PermitOwner;

    /// <summary>
    /// The mind of the permit owner. Used for checking if a permitted item should stay or be seized on a saved ship.
    /// Session-local, re-stamped on ship load; never serialized.
    /// </summary>
    [ViewVariables]
    public EntityUid? PermitOwnerMind;

    /// <summary>
    /// Flavor RP date of whenever the contraband permit was granted.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string DateGranted = string.Empty;
}
