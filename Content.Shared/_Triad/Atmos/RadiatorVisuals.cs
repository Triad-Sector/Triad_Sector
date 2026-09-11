using Robust.Shared.Serialization;

namespace Content.Shared._Triad.Atmos;

/// <summary>
/// Appearance keys for the radiator's thermal-state skin.
/// </summary>
[Serializable, NetSerializable]
public enum RadiatorVisuals : byte
{
    Bucket,
    Connections,
}

/// <summary>
/// Quantized thermal state of a radiator body, ordered cold to hot so bucket
/// comparisons work numerically. Networked via appearance only on change.
/// Cold exists for the chill hazard (and a future frost skin); the hot tail is
/// the blackbody incandescence ramp.
/// </summary>
[Serializable, NetSerializable]
public enum RadiatorThermalBucket : byte
{
    Cold = 0,
    Neutral = 1,
    DullRed = 2,
    CherryRed = 3,
    Orange = 4,
    Yellow = 5,
    White = 6,
}

/// <summary>
/// Sprite layer map keys for the radiator.
/// </summary>
[Serializable, NetSerializable]
public enum RadiatorVisualLayers : byte
{
    Glow,
}

/// <summary>
/// Which of the radiator's two ports are directly linked to another radiator,
/// expressed as the sprite treatment: a cap renders only on unlinked ends.
/// </summary>
[Serializable, NetSerializable]
public enum RadiatorConnectionState : byte
{
    /// <summary>No radiator on either port; both caps (the original art).</summary>
    Isolated = 0,
    /// <summary>Outlet linked; the cap remains at the inlet end.</summary>
    CapIn = 1,
    /// <summary>Inlet linked; the cap remains at the outlet end.</summary>
    CapOut = 2,
    /// <summary>Both ports linked; capless middle section.</summary>
    Middle = 3,
}
