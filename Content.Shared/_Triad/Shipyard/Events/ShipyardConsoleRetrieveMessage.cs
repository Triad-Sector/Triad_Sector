// Triad: drydock tab.
using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shipyard.Events;

/// <summary>
/// Retrieve the stored ship with this id onto the console's station.
///
/// <para>The id arrives from the client, so it is a request and not an authorization: the list it
/// was picked from only ever held the operator's own ships, but nothing stops a client sending an
/// id it was never given. The pipeline re-reads the row's owner and refuses a mismatch, which is
/// why this message is safe to accept at face value.</para>
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleRetrieveMessage : BoundUserInterfaceMessage
{
    public readonly Guid ShipId;

    public ShipyardConsoleRetrieveMessage(Guid shipId)
    {
        ShipId = shipId;
    }
}
