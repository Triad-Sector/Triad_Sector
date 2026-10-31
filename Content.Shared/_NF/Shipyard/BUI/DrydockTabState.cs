// Triad: drydock tab. New file in the NF namespace because it is part of the shipyard console's
// existing interface state rather than a surface of its own.
using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shipyard.BUI;

/// <summary>
/// Triad: drydock tab. Everything the tab draws, as one value: the lists a refresh reads from the
/// database, the access and reissue flags read from the world at the same swap, the cvar-driven
/// fields (<see cref="DrydockEnabled"/>, <see cref="BerthPrices"/>, <see cref="TransferOfferMinutes"/>)
/// recomputed on every publish, and the live store/retrieve percentage that outlives any one
/// refresh. One field on <c>ShipyardConsoleInterfaceState</c> and one cache on
/// <c>ShipyardConsoleComponent</c> in place of the nine constructor parameters, five
/// post-construction fields and (all but one of) the eleven <c>Cached*</c> fields this replaces.
/// </summary>
[Serializable, NetSerializable]
public sealed record DrydockTabState
{
    /// <summary>
    /// Whether the drydock tab is offered at all. The master switch is server-only, so the client
    /// cannot read it and has to be told; without this the console would show a tab whose every
    /// button comes back refused.
    /// </summary>
    public bool DrydockEnabled;

    public List<StoredShipInfo> StoredShips;

    /// <summary>The operator's berths, occupants included.</summary>
    public List<DrydockBerthInfo> Berths;

    /// <summary>Berth purchase price per size class name, for the buy control.</summary>
    public Dictionary<string, int> BerthPrices;

    /// <summary>Every standing offer addressed to the operator, oldest deadline first.</summary>
    public List<DrydockTransferOfferInfo> TransferOffers;

    /// <summary>The captains online right now, for the transfer picker.</summary>
    public List<DrydockCaptainInfo> Captains;

    /// <summary>
    /// The account that owns the ship on the inserted card's deed, or null when the card carries
    /// no deed to a live ship. The client compares it with its own account and covers the drydock
    /// tab with the lockout when they differ. Presentation only: the server refuses every message
    /// the lockout hides, and the id is already networked on the ship's ownership component.
    /// </summary>
    public Guid? DeedOwnerUserId;

    /// <summary>The ship on the inserted card's deed and the berths it can go into, or null.</summary>
    public DrydockDeedShipInfo? DeedShip;

    /// <summary>
    /// How long a transfer offer stands, in whole minutes, for the sentence in the transfer
    /// prompt. The cvar behind it is server-only, so the client has to be told.
    /// </summary>
    public int TransferOfferMinutes;

    /// <summary>
    /// Legacy saves on this client's disk that the server will accept; empty until the client's
    /// manifest is answered, and empty for good when legacy import is switched off.
    /// </summary>
    public List<DrydockImportShipInfo> ImportableShips = new();

    /// <summary>The operator's ships in the impound lot, for the cards above the berth list.</summary>
    public List<DrydockImpoundedShipInfo> ImpoundedShips = new();

    /// <summary>
    /// How far along the store or retrieve running at this console is, or null when nothing is
    /// running. The live figure also travels by message, aimed at the operator who pressed; this
    /// is the reopen path alone, so a console opened mid-store draws the indicator instead of a
    /// live Store button over one already running.
    /// </summary>
    public int? StoreProgressPercent;

    /// <summary>
    /// The operator is barred from the drydock (TDF, TFA and the other voucher-issued roles),
    /// which covers the tab with the access-denied screen. The server refuses every drydock
    /// message from a barred operator regardless.
    /// </summary>
    public bool DrydockOperatorBarred;

    /// <summary>
    /// The civilian ships the operator's account has out in the world. Buying or retrieving
    /// another is refused while this is not empty, so the purchase and retrieve buttons grey on it.
    /// </summary>
    public List<DrydockReissueShipInfo> ShipsOut = new();

    /// <summary>
    /// Whether the inserted card can take a deed moved onto it: an ID card, not a voucher,
    /// carrying no deed. When it can, the deed card at the top names the ship in
    /// <see cref="ShipsOut"/> and offers Transfer deed in Store's place.
    /// </summary>
    public bool CanReissueToCard;

    /// <summary>
    /// What the footer's sale button does with the deed on the inserted card. Filled whether or not
    /// the drydock is on, since the footer is drawn either way. The server refuses a sale the button
    /// would not have offered.
    /// </summary>
    public ShipyardDeedSale DeedSale;

    public DrydockTabState(
        bool drydockEnabled,
        List<StoredShipInfo> storedShips,
        List<DrydockBerthInfo> berths,
        Dictionary<string, int> berthPrices,
        List<DrydockTransferOfferInfo> transferOffers,
        List<DrydockCaptainInfo> captains,
        Guid? deedOwnerUserId,
        DrydockDeedShipInfo? deedShip,
        int transferOfferMinutes)
    {
        DrydockEnabled = drydockEnabled;
        StoredShips = storedShips;
        Berths = berths;
        BerthPrices = berthPrices;
        TransferOffers = transferOffers;
        Captains = captains;
        DeedOwnerUserId = deedOwnerUserId;
        DeedShip = deedShip;
        TransferOfferMinutes = transferOfferMinutes;
    }

    /// <summary>The tab before anything has ever been read: no console found, or nothing cached yet.</summary>
    public static readonly DrydockTabState Empty = new(
        drydockEnabled: false,
        storedShips: new(),
        berths: new(),
        berthPrices: new(),
        transferOffers: new(),
        captains: new(),
        deedOwnerUserId: null,
        deedShip: null,
        transferOfferMinutes: 0);
}

/// <summary>Triad: the footer sale button's mode for the deed on the inserted card.</summary>
[Serializable, NetSerializable]
public enum ShipyardDeedSale : byte
{
    /// <summary>No deed to a live ship on the card: the button is drawn greyed.</summary>
    None = 0,

    /// <summary>A hull the drydock cannot hold (a faction hull bought with cash), or the drydock is off.</summary>
    Sell = 1,

    /// <summary>A hull issued on a voucher: handed back for nothing, and the voucher is burned.</summary>
    Return = 2,

    /// <summary>A civilian hull with the drydock on: stored, then sold from the drydock tab. No footer button.</summary>
    StoreFirst = 3,
}
