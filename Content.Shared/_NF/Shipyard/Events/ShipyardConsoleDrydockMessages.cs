// Triad: drydock tab. The berth, transfer, sale, rename, move, impound and deed messages, beside
// the store and retrieve ones. Buying a berth names only a size class. Every other message names
// what it acts on by something the server sent (a ship, berth or transfer id, or the ship entity
// for a deed reissue), and the server checks the sending account may act on it before anything
// else; a message that fails that check is refused and written to the timeline.
using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shipyard.Events;

/// <summary>Buy a berth of this size class for the operator's account.</summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleBuyBerthMessage : BoundUserInterfaceMessage
{
    public readonly string SizeClass;

    public ShipyardConsoleBuyBerthMessage(string sizeClass)
    {
        SizeClass = sizeClass;
    }
}

/// <summary>Sell one of the operator's empty berths. The server checks it is theirs and empty.</summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleSellBerthMessage : BoundUserInterfaceMessage
{
    public readonly int BerthId;

    public ShipyardConsoleSellBerthMessage(int berthId)
    {
        BerthId = berthId;
    }
}

/// <summary>Raise one of the operator's berths to the next class up, paying the difference.</summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleUpgradeBerthMessage : BoundUserInterfaceMessage
{
    public readonly int BerthId;

    public ShipyardConsoleUpgradeBerthMessage(int berthId)
    {
        BerthId = berthId;
    }
}

/// <summary>
/// Offer one of the operator's stored ships to another account. The recipient must be online when
/// the offer is made and must have a free berth the hull fits; the ship then waits in escrow, in
/// its own berth, until they answer or the offer expires.
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleOfferTransferMessage : BoundUserInterfaceMessage
{
    public readonly Guid ShipId;
    public readonly Guid RecipientUserId;

    public ShipyardConsoleOfferTransferMessage(Guid shipId, Guid recipientUserId)
    {
        ShipId = shipId;
        RecipientUserId = recipientUserId;
    }
}

/// <summary>The owner withdraws a standing offer. The ship leaves escrow.</summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleCancelTransferMessage : BoundUserInterfaceMessage
{
    public readonly long TransferId;

    public ShipyardConsoleCancelTransferMessage(long transferId)
    {
        TransferId = transferId;
    }
}

/// <summary>The recipient takes the ship into a free berth of theirs that fits.</summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleAcceptTransferMessage : BoundUserInterfaceMessage
{
    public readonly long TransferId;

    public ShipyardConsoleAcceptTransferMessage(long transferId)
    {
        TransferId = transferId;
    }
}

/// <summary>The recipient turns the offer down. The ship leaves escrow.</summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleDeclineTransferMessage : BoundUserInterfaceMessage
{
    public readonly long TransferId;

    public ShipyardConsoleDeclineTransferMessage(long transferId)
    {
        TransferId = transferId;
    }
}

/// <summary>
/// Scrap a stored ship for credits. Carries the name the player typed, which the server compares
/// with the ship's own name before paying anything: the client's locked button is a convenience,
/// this comparison is the safety.
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleSellStoredShipMessage : BoundUserInterfaceMessage
{
    public readonly Guid ShipId;
    public readonly string TypedName;

    public ShipyardConsoleSellStoredShipMessage(Guid shipId, string typedName)
    {
        ShipId = shipId;
        TypedName = typedName;
    }
}

/// <summary>Rename a stored ship. Applied to hull and deed the next time it is retrieved.</summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleRenameStoredShipMessage : BoundUserInterfaceMessage
{
    public readonly Guid ShipId;
    public readonly string NewName;

    public ShipyardConsoleRenameStoredShipMessage(Guid shipId, string newName)
    {
        ShipId = shipId;
        NewName = newName;
    }
}

/// <summary>Move a stored ship to another of the operator's own empty berths that fits.</summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleMoveStoredShipMessage : BoundUserInterfaceMessage
{
    public readonly Guid ShipId;
    public readonly int BerthId;

    public ShipyardConsoleMoveStoredShipMessage(Guid shipId, int berthId)
    {
        ShipId = shipId;
        BerthId = berthId;
    }
}

/// <summary>
/// The owner pays the fee and takes an impounded ship back into one of their own berths. The
/// server re-reads the terms, charges the fee it has on the row, checks the berth again, and
/// refuses a locked impound whatever the client drew.
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleRedeemImpoundMessage : BoundUserInterfaceMessage
{
    public readonly Guid ShipId;
    public readonly int BerthId;

    public ShipyardConsoleRedeemImpoundMessage(Guid shipId, int berthId)
    {
        ShipId = shipId;
        BerthId = berthId;
    }
}

/// <summary>
/// The owner gives an impounded ship up rather than pay for it. Carries the name the player
/// typed, which the server compares with the ship's own before anything moves, as a sale does.
/// No money moves in either direction.
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleAbandonShipMessage : BoundUserInterfaceMessage
{
    public readonly Guid ShipId;
    public readonly string TypedName;

    public ShipyardConsoleAbandonShipMessage(Guid shipId, string typedName)
    {
        ShipId = shipId;
        TypedName = typedName;
    }
}

/// <summary>
/// The owner moves the deed to one of their ships that is out onto the card in the slot, wherever
/// the ship is. The server checks the account behind the press owns the hull and strips the deed
/// from every other card that carries it.
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleReissueDeedMessage : BoundUserInterfaceMessage
{
    public readonly NetEntity Ship;

    public ShipyardConsoleReissueDeedMessage(NetEntity ship)
    {
        Ship = ship;
    }
}
