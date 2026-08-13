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
    [AutoNetworkedField, ViewVariables]
    public EntityUid? PermitOwner;

    /// <summary>
    /// The sector service record key of the permit item. Will almost always be the humanoid view of the permit owner, unless the permit owner doesn't have a humanoid view.
    /// Not serialized.
    /// </summary>
    [ViewVariables]
    public NetEntity? PermitRecordKey;

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
