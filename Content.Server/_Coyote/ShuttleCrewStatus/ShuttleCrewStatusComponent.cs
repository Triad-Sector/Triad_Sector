using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server._Coyote.ShuttleCrewStatus;

/// <summary>
/// Component that tracks shuttle crew status and manages delayed IFF color updates.
/// </summary>
[RegisterComponent]
public sealed partial class ShuttleCrewStatusComponent : Component
{
    /// <summary>
    /// The original IFF color before crew-status-driven overrides.
    /// </summary>
    [DataField]
    public Color? OriginalColor;

    /// <summary>
    /// Last applied crew status represented by IFF color.
    /// </summary>
    [DataField]
    public bool HasActiveCrew = true;

    /// <summary>
    /// Whether a visual IFF update is pending.
    /// </summary>
    [DataField]
    public bool PendingVisualUpdate;

    /// <summary>
    /// Target crew status to apply when delay expires.
    /// </summary>
    [DataField]
    public bool PendingHasActiveCrew;

    /// <summary>
    /// When the pending visual update should be applied.
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan ApplyVisualAt = TimeSpan.Zero;
}
