using Content.Shared.Radio;
using Content.Shared.Roles;
using Content.Shared.Whitelist;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._Triad.ContrabandPermit;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ContrabandPermitConsoleComponent : Component
{
    /// <summary>
    /// Entities in this whitelist will be able to grant permits to people.
    /// </summary>
    [DataField, AutoNetworkedField]
    public EntityWhitelist? GrantPermitWhitelist;

    /// <summary>
    /// Entities in this blacklist will not be able to grant permits to people.
    /// </summary>
    [DataField, AutoNetworkedField]
    public EntityWhitelist? GrantPermitBlacklist;

    [DataField, AutoNetworkedField]
    public string ChipSlotContainerId = "chip_slot";

    [DataField, AutoNetworkedField]
    public string CurrentPermitReason = string.Empty;

    /// <summary>
    /// The current selected permit
    /// </summary>
    [ViewVariables, AutoNetworkedField]
    public PermitFocusData? FocusPermit;

    [DataField, AutoNetworkedField]
    public SoundSpecifier ErrorSound =
        new SoundPathSpecifier("/Audio/Effects/Cargo/buzz_sigh.ogg");

    [DataField, AutoNetworkedField]
    public SoundSpecifier ConfirmSound =
        new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg");

    /// <summary>
    /// Sound to play when fax printing a permit chip.
    /// </summary>
    [DataField]
    public SoundSpecifier ChipPrintSound = new SoundPathSpecifier("/Audio/Machines/printer.ogg");

    [DataField]
    public EntProtoId ChipPrototype = "PermitChip";

    /// <summary>
    /// The comms channel that announces a permit grant or revoke.
    /// </summary>
    [DataField]
    public ProtoId<RadioChannelPrototype> RadioChannel = "Nfsd"; // TDF channel

    /// <summary>
    /// Timeout for printing a permit chip from the console.
    /// </summary>
    [DataField, AutoNetworkedField]
    public TimeSpan PrintChipTimeout = TimeSpan.FromSeconds(10);

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField]
    public TimeSpan PrintChipTimeoutEnd;
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
