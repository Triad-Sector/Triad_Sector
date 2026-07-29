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
    /// The UID of the permit owner.
    /// </summary>
    [DataField, AutoNetworkedField]
    public EntityUid? PermitOwner;

    /// <summary>
    /// Flavor RP date of whenever the contraband permit was granted.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string DateGranted = string.Empty;
}
