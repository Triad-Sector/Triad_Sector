using Robust.Shared.Audio;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom; // Triad

namespace Content.Shared._DEN.TimedDespawn;

/// <summary>
/// This is used for a more detailed timed despawn component.
/// </summary>
[RegisterComponent]
public sealed partial class TimedDespawnDetailedComponent : Component
{
    /// <summary>
    /// When the timer started, as an absolute game time. Triad: written through
    /// <see cref="TimeOffsetSerializer"/>, since a raw CurTime stamp reloads against a clock that
    /// no longer exists and puts the deadline out of reach.
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan StartTime { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// How long the entity will exist, in seconds, before despawning.
    /// </summary>
    [DataField]
    public float Lifetime = 5f;

    [DataField("examineText")]
    public LocId? ExamineLocId { get; set; } = "timed-despawn-holoprojection-examine";

    [DataField]
    public SoundSpecifier? StartSound { get; set; } = new SoundPathSpecifier("/Audio/_DEN/Effects/holofan_sound_looped.ogg");

    [DataField]
    public AudioParams StartSoundParams { get; set; } = AudioParams.Default;

    [DataField]
    public SoundSpecifier? EndSound { get; set; }

    [DataField]
    public AudioParams EndSoundParams { get; set; } = AudioParams.Default;
}
