using Content.Shared.Roles;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Triad.ContrabandPermit;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ContrabandPermitConsoleComponent : Component
{
    /// <summary>
    /// Jobs not in this list will not be able to grant permits to people.
    /// </summary>
    [DataField, AutoNetworkedField]
    public List<ProtoId<JobPrototype>>? GrantPermitRestrictedJobs;

    [DataField, AutoNetworkedField]
    public string ChipSlotContainerId = "chip_slot";

    /// <summary>
    /// The current selected permit
    /// </summary>
    [ViewVariables, AutoNetworkedField]
    public PermitFocusData? FocusPermit;
}

[Serializable, NetSerializable]
public struct PermitFocusData(
    NetEntity permitOwner,
    EntProtoId itemEntProtoId,
    NetEntity itemNetEntity)
{
    /// <summary>
    /// permit owner
    /// </summary>
    public NetEntity PermitOwner = permitOwner;

    /// <summary>
    /// Entity prototype of the permitted item
    /// </summary>
    public EntProtoId ItemEntProtoId = itemEntProtoId;

    /// <summary>
    /// Net entity of the permitted item
    /// </summary>
    public NetEntity ItemNetEntity = itemNetEntity;
}
