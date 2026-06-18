using Robust.Shared.Serialization;

namespace Content.Shared._Triad.Shipyard.Save;

[Serializable, NetSerializable]
public sealed class SendShipSaveDataClientMessage : EntityEventArgs
{
    public string ShipName { get; }
    public string ShipData { get; }

    public SendShipSaveDataClientMessage(string shipName, string shipData)
    {
        ShipName = shipName;
        ShipData = shipData;
    }
}
