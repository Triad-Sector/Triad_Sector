using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shuttles.Save;

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

[Serializable, NetSerializable]
public sealed class RequestLoadShipMessage : EntityEventArgs
{
    public string YamlData { get; }

    public RequestLoadShipMessage(string yamlData)
    {
        YamlData = yamlData;
    }
}

[Serializable, NetSerializable]
public sealed class RequestAvailableShipsMessage : EntityEventArgs
{
}

[Serializable, NetSerializable]
public sealed class SendAvailableShipsMessage : EntityEventArgs
{
    public List<string> ShipNames { get; }

    public SendAvailableShipsMessage(List<string> shipNames)
    {
        ShipNames = shipNames;
    }
}

[Serializable, NetSerializable]
public sealed class AdminRequestPlayerShipsMessage : EntityEventArgs
{
    public Guid PlayerId { get; }
    public string AdminName { get; }

    public AdminRequestPlayerShipsMessage(Guid playerId, string adminName)
    {
        PlayerId = playerId;
        AdminName = adminName;
    }
}

[Serializable, NetSerializable]
public sealed class AdminSendPlayerShipsMessage : EntityEventArgs
{
    public List<(string filename, string shipName, DateTime timestamp)> Ships { get; }
    public string AdminName { get; }

    public AdminSendPlayerShipsMessage(List<(string filename, string shipName, DateTime timestamp)> ships, string adminName)
    {
        Ships = ships;
        AdminName = adminName;
    }
}

[Serializable, NetSerializable]
public sealed class AdminRequestShipDataMessage : EntityEventArgs
{
    public string ShipFilename { get; }
    public string AdminName { get; }

    public AdminRequestShipDataMessage(string shipFilename, string adminName)
    {
        ShipFilename = shipFilename;
        AdminName = adminName;
    }
}

[Serializable, NetSerializable]
public sealed class AdminSendShipDataMessage : EntityEventArgs
{
    public string ShipData { get; }
    public string ShipFilename { get; }
    public string AdminName { get; }

    public AdminSendShipDataMessage(string shipData, string shipFilename, string adminName)
    {
        ShipData = shipData;
        ShipFilename = shipFilename;
        AdminName = adminName;
    }
}
