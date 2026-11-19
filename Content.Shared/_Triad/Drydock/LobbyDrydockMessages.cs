
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
