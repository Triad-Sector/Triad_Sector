
using Content.Shared._NF.Shipyard.BUI;
using Robust.Shared.Serialization;

namespace Content.Shared._Triad.Drydock;

[Serializable, NetSerializable]
public sealed class LobbyDrydockBerthInfoRequest : EntityEventArgs;

[Serializable, NetSerializable]
public sealed class LobbyDrydockBerthInfoResponse(List<DrydockBerthInfo> berths) : EntityEventArgs
{
    public List<DrydockBerthInfo> Berths = berths;
}

[Serializable, NetSerializable]
public sealed class LobbyDrydockDeleteShipMessage(Guid shipId, string confirmText) : EntityEventArgs
{
    public Guid ShipId = shipId;
    public string ConfirmText = confirmText;
}

[Serializable, NetSerializable]
public sealed class LobbyDrydockExportShipMessage(Guid shipId) : EntityEventArgs
{
    public Guid ShipId = shipId;
}

[Serializable, NetSerializable]
public sealed class LobbyDrydockExportShipResponse(string? yaml) : EntityEventArgs
{
    public string? Yaml = yaml;
}

[Serializable, NetSerializable]
public sealed class LobbyDrydockTransferShipOwnerMessage(Guid shipId, int character) : EntityEventArgs
{
    public Guid ShipId = shipId;
    public int Character = character;
}
