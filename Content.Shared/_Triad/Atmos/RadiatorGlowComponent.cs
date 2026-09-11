using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Shared._Triad.Atmos;

/// <summary>
/// Triad: marks a radiator whose incandescence cross-fades instead of snapping
/// between thermal buckets.
/// </summary>
/// <remarks>
/// The networked thermal signal stays quantized to <see cref="RadiatorThermalBucket"/>
/// and is only dirtied on a bucket transition, which is what keeps a large
/// array off the wire. The interpolation between two buckets is therefore done
/// client-side, from <see cref="RadiatorGlowRamp"/>, while the server writes
/// each bucket's resting values to the networked light. Nothing on this
/// component is networked.
/// </remarks>
[RegisterComponent]
public sealed partial class RadiatorGlowComponent : Component
{
    /// <summary>
    /// Seconds to cross-fade from one bucket's look to the next. Long enough
    /// to read as a hot mass changing temperature rather than a light switch.
    /// </summary>
    [DataField]
    public float FadeDuration = 1.5f;

    // Runtime interpolation state; client-side only, never serialized.
    public Color StartColor = Color.Transparent;
    public Color TargetColor = Color.Transparent;
    public float StartEnergy;
    public float TargetEnergy;
    public float StartRadius = 1.2f;
    public float TargetRadius = 1.2f;

    /// <summary>
    /// Seconds since the current fade began. Once this passes
    /// <see cref="FadeDuration"/> the fade is finished and stops being applied.
    /// </summary>
    public float Elapsed = float.MaxValue;
}
