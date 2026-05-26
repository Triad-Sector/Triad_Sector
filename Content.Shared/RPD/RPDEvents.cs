using Robust.Shared.Serialization;

namespace Content.Shared.RPD;

/// <summary>
/// Networks the local player's eye rotation to the server so the RPD can compute pipe-layer placement.
/// Eye rotation isn't networked by Robust natively; funky-station's note "Not intended as a permanent
/// solution" still applies.
/// </summary>
[Serializable, NetSerializable]
public sealed class RPDEyeRotationEvent : EntityEventArgs
{
    public readonly NetEntity NetEntity;
    public readonly float? EyeRotation;

    public RPDEyeRotationEvent(NetEntity netEntity, float? eyeRotation)
    {
        NetEntity = netEntity;
        EyeRotation = eyeRotation;
    }
}

/// <summary>
/// BUI message from the RPD color picker. Carries only the palette key; the server re-derives the
/// <see cref="Color"/> from <c>RPDPalette.Colors[key]</c> so a misbehaving client can't desync the pair.
/// </summary>
[Serializable, NetSerializable]
public sealed class RPDColorChangeMessage : BoundUserInterfaceMessage
{
    public readonly NetEntity NetEntity;
    public readonly string PipeColor;

    public RPDColorChangeMessage(NetEntity entity, string pipeColor)
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
