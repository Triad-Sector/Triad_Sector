using Robust.Shared.Serialization;

namespace Content.Shared._Triad.Atmos;

[Serializable, NetSerializable]
public sealed class StopDropRollVisualsNetworkEvent(NetEntity user) : EntityEventArgs
{
    public readonly NetEntity User = user;
}
