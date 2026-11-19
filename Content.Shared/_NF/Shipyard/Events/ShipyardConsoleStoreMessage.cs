// Triad: drydock tab.
using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shipyard.Events;

/// <summary>
/// Store the ship whose deed is on the inserted ID card.
///
/// <para>Carries no ship handle. The ship is resolved server-side from the card in the console's
/// slot, so there is nothing for a client to substitute; a player who wants to store a different
/// ship has to physically put its deed in the slot. The one thing it may name is which of the
/// operator's own berths to land in; the server checks that berth is theirs, free, and large
/// enough, and picks one itself when none is named.</para>
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleStoreMessage : BoundUserInterfaceMessage
{
    public readonly int? BerthId;

    public ShipyardConsoleStoreMessage(int? berthId = null)
    {
        BerthId = berthId;
    }
}
