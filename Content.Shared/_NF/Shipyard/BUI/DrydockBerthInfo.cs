// Triad: drydock tab. New files in the NF namespace because they are part of the shipyard console's
// existing interface state rather than a surface of their own.
using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shipyard.BUI;

/// <summary>One of the operator's berths as the drydock tab draws it: a slot, and what is in it.</summary>
[Serializable, NetSerializable]
public sealed class DrydockBerthInfo
{
    public int BerthId;

    /// <summary>The stored class text, shown as-is for the same reason the ship's is.</summary>
    public string MaxSizeClass = string.Empty;

    /// <summary>What selling this berth returns right now. Zero for a grant.</summary>
    public int SellValue;

    /// <summary>What raising this berth one class costs, or null when it is already the largest.</summary>
    public int? UpgradePrice;

    public string? UpgradeClass;

    public Guid? OccupantShipId;

    public string? OccupantName;

    /// <summary>The occupant's stored class text, so the row can say "Kestrel · Cutter".</summary>
    public string? OccupantSizeClass;

    /// <summary>The occupant's row state as text; only a Stored occupant offers Retrieve.</summary>
    public string? OccupantState;

    /// <summary>What scrapping the occupant pays right now, or null when no appraisal was captured.</summary>
    public int? OccupantSellPrice;

    /// <summary>
    /// The appraisal that price was cut from, so the sale prompt can say what fraction of the hull's
    /// worth the scrap pays. Null whenever <see cref="OccupantSellPrice"/> is.
    /// </summary>
    public int? OccupantAppraisal;

    /// <summary>When the occupant is in escrow: the standing offer, who it went to, and how long it has left.</summary>
    public long? OccupantTransferId;

    public string? OccupantOfferedTo;

    public int? OccupantOfferSecondsLeft;

    public DrydockBerthInfo(int berthId, string maxSizeClass, int sellValue, int? upgradePrice, string? upgradeClass,
        Guid? occupantShipId, string? occupantName, string? occupantSizeClass, string? occupantState, int? occupantSellPrice,
        long? occupantTransferId, string? occupantOfferedTo, int? occupantOfferSecondsLeft, int? occupantAppraisal = null)
    {
        OccupantAppraisal = occupantAppraisal;
        BerthId = berthId;
        MaxSizeClass = maxSizeClass;
        SellValue = sellValue;
        UpgradePrice = upgradePrice;
        UpgradeClass = upgradeClass;
        OccupantShipId = occupantShipId;
        OccupantName = occupantName;
        OccupantSizeClass = occupantSizeClass;
        OccupantState = occupantState;
        OccupantSellPrice = occupantSellPrice;
        OccupantTransferId = occupantTransferId;
        OccupantOfferedTo = occupantOfferedTo;
        OccupantOfferSecondsLeft = occupantOfferSecondsLeft;
    }
}

/// <summary>
/// The ship on the inserted card's deed, as the card at the top of the tab draws it: the one ship
/// the operator has out, and where it can go. Null when the card carries no deed.
/// </summary>
[Serializable, NetSerializable]
public sealed class DrydockDeedShipInfo
{
    public string Name = string.Empty;

    public string? SizeClass;

    /// <summary>Minutes since the ship was retrieved, or null for a hull that has never been stored.</summary>
    public int? MinutesOut;

    /// <summary>
    /// The berth a plain Store lands in: the ship's own last berth if it is free and fits, else
    /// the smallest free berth that fits. Null when nothing fits, which is what disables Store.
    /// </summary>
    public int? DefaultBerthId;

    /// <summary>Every free berth the hull fits, for the dropdown beside Store.</summary>
    public List<int> FittingBerthIds = new();

    /// <summary>
    /// Whether the ship is docked to the station this console belongs to. A store is a hand-over
    /// at a berth, not a remote command, so this is what disables Store for a ship left out in
    /// space; the server refuses the store regardless.
    /// </summary>
    public bool Docked;

    public DrydockDeedShipInfo(string name, string? sizeClass, int? minutesOut, int? defaultBerthId, List<int> fittingBerthIds, bool docked)
    {
        Name = name;
        SizeClass = sizeClass;
        MinutesOut = minutesOut;
        DefaultBerthId = defaultBerthId;
        FittingBerthIds = fittingBerthIds;
        Docked = docked;
    }
}

/// <summary>
/// A ship the operator's account owns that is out in the world, for the card that moves its deed
/// onto the inserted card. Only sent while the inserted card carries no deed.
/// </summary>
[Serializable, NetSerializable]
public sealed class DrydockReissueShipInfo
{
    public NetEntity Ship;
    public string Name = string.Empty;
    public string? SizeClass;

    public DrydockReissueShipInfo(NetEntity ship, string name, string? sizeClass)
    {
        Ship = ship;
        Name = name;
        SizeClass = sizeClass;
    }
}

/// <summary>
/// An offer addressed to the operator, as the alert on their tab draws it. Carries the offering
/// account's id only so the client can refuse to draw the offerer's own offers as alerts; the
/// server holds the real offer and decides everything about it.
/// </summary>
[Serializable, NetSerializable]
public sealed class DrydockTransferOfferInfo
{
    public long TransferId;
    public Guid ShipId;
    public string ShipName = string.Empty;
    public string? SizeClass;
    public string OfferedBy = string.Empty;
    public Guid OfferedByUserId;

    /// <summary>The berth the ship lands in if accepted now, or null when nothing fits any more.</summary>
    public int? LandsInBerthId;

    /// <summary>Read from the persisted deadline, so every console shows the same clock.</summary>
    public int SecondsLeft;

    public DrydockTransferOfferInfo(long transferId, Guid shipId, string shipName, string? sizeClass, string offeredBy, Guid offeredByUserId, int? landsInBerthId, int secondsLeft)
    {
        TransferId = transferId;
        ShipId = shipId;
        ShipName = shipName;
        SizeClass = sizeClass;
        OfferedBy = offeredBy;
        OfferedByUserId = offeredByUserId;
        LandsInBerthId = landsInBerthId;
        SecondsLeft = secondsLeft;
    }
}

/// <summary>
/// A captain online right now, for the transfer picker: their account, the name to show, and the
/// classes of their free berths so the picker can grey the ones with nowhere to put the ship.
/// </summary>
[Serializable, NetSerializable]
public sealed class DrydockCaptainInfo
{
    public Guid UserId;
    public string Name = string.Empty;
    public List<string> FreeBerthClasses = new();

    public DrydockCaptainInfo(Guid userId, string name, List<string> freeBerthClasses)
    {
        UserId = userId;
        Name = name;
        FreeBerthClasses = freeBerthClasses;
    }
}
