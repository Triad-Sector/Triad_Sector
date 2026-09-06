using Content.Shared.Access;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Server._NF.Shipyard.Components;

[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class ShipyardVoucherComponent : Component
{
    /// <summary>
    ///  Number of redeemable ships that this voucher can still be used for. Decremented on purchase.
    /// </summary>
    [DataField]
    public uint RedemptionsLeft = 1;

    /// <summary>
    ///  If true, card will be destroyed when no redemptions are left. Checked at time of sale.
    /// </summary>
    [DataField]
    public bool DestroyOnEmpty = false;

    /// <summary>
    ///  Access tags and groups for shipyard access.
    /// </summary>
    [DataField]
    public IReadOnlyCollection<ProtoId<AccessLevelPrototype>> Access { get; private set; } = Array.Empty<ProtoId<AccessLevelPrototype>>();

    [DataField]
    public IReadOnlyCollection<ProtoId<AccessGroupPrototype>> AccessGroups { get; private set; } = Array.Empty<ProtoId<AccessGroupPrototype>>();

    // Mono
    /// <summary>
    ///  Vessels this voucher can be used for, in addition to what Access and AccessGroups would allow.
    /// </summary>
    [DataField]
    public HashSet<ProtoId<VesselPrototype>> Vessels = new();

    /// <summary>
    ///  Triad - A hashset of console types where this voucher can be used.
    /// </summary>
    [DataField(required: true)]
    public HashSet<ShipyardConsoleUiKey> ConsoleTypes = new();

    /// <summary>
    ///  The company name associated with this voucher. Used to transfer company information to purchased ships.
    /// </summary>
    [DataField]
    public string? CompanyName;
}
