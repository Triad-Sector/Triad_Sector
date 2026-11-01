// Triad: drydock tab. New file in the NF namespace because it is part of the shipyard console's
// existing interface state rather than a surface of its own.
using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shipyard.BUI;

/// <summary>
/// One of the operator's impounded ships, as the card above the berth list draws it: what it
/// costs to reclaim and against what, why it was taken, whether the owner may act on it at all,
/// and which of their berths it could go back into. Presentation only: the server re-reads the
/// terms, charges the fee it has on the row, and checks the berth again on the press.
/// </summary>
[Serializable, NetSerializable]
public sealed class DrydockImpoundedShipInfo
{
    public Guid ShipId;
    public string Name = string.Empty;

    /// <summary>The stored class text, shown as-is for the same reason a berth row's is.</summary>
    public string? SizeClass;

    /// <summary>Credits to reclaim it, frozen at impound. Zero is free.</summary>
    public int Fee;

    /// <summary>
    /// The appraisal the fee was cut from, so the card can say what share of the hull's worth it
    /// is. Null when the revision recorded none, in which case the fee is zero.
    /// </summary>
    public int? Appraisal;

    /// <summary>
    /// Whether the owner may reclaim or abandon it. False is an adjudication: the card says an
    /// admin holds it and offers neither button.
    /// </summary>
    public bool Redeemable;

    /// <summary>What it was taken for, as the admin or the sweep wrote it. Null when nothing was given.</summary>
    public string? Reason;

    /// <summary>
    /// The berth Reclaim lands in unless the picker says otherwise: the ship's own last berth if it
    /// is free and fits, else the smallest free berth that fits. Null when nothing fits, which is
    /// what greys Reclaim.
    /// </summary>
    public int? DefaultBerthId;

    /// <summary>Every free berth the hull fits, for the picker beside Reclaim.</summary>
    public List<int> FittingBerthIds = new();

    public DrydockImpoundedShipInfo(Guid shipId, string name, string? sizeClass, int fee, int? appraisal, bool redeemable, string? reason, int? defaultBerthId, List<int> fittingBerthIds)
    {
        ShipId = shipId;
        Name = name;
        SizeClass = sizeClass;
        Fee = fee;
        Appraisal = appraisal;
        Redeemable = redeemable;
        Reason = reason;
        DefaultBerthId = defaultBerthId;
        FittingBerthIds = fittingBerthIds;
    }
}
