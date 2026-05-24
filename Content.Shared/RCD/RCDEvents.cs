using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.RCD;

[Serializable, NetSerializable]
public sealed class RCDSystemMessage : BoundUserInterfaceMessage
{
    public ProtoId<RCDPrototype> ProtoId;

    public RCDSystemMessage(ProtoId<RCDPrototype> protoId)
    {
        ProtoId = protoId;
    }
}

[Serializable, NetSerializable]
public sealed class RCDConstructionGhostRotationEvent : EntityEventArgs
{
    public readonly NetEntity NetEntity;
    public readonly Direction Direction;

    public RCDConstructionGhostRotationEvent(NetEntity netEntity, Direction direction)
    {
        NetEntity = netEntity;
        Direction = direction;
    }
}

[Serializable, NetSerializable]
public enum RcdUiKey : byte
{
    Key
}

// Triad: RPD port from funky-station — color picker message + RPD UI key.
[Serializable, NetSerializable]
public sealed class RCDColorChangeMessage : BoundUserInterfaceMessage
{
    public readonly NetEntity NetEntity;
    public readonly (string Key, Color? Color) PipeColor;

    public RCDColorChangeMessage(NetEntity entity, (string Key, Color? Color) pipeColor)
    {
        NetEntity = entity;
        PipeColor = pipeColor;
    }
}

[Serializable, NetSerializable]
public enum RpdUiKey : byte
{
    Key
}
// End Triad
