using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom; // Triad: TimeOffsetSerializer

namespace Content.Shared.Xenoarchaeology.Artifact.Components;

/// <summary>
/// This is used for tracking the nodes which have been triggered during a particular unlocking state.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, AutoGenerateComponentPause]
public sealed partial class XenoArtifactUnlockingComponent : Component
{
    /// <summary>
    /// Indexes corresponding to all of the nodes that have been triggered
    /// </summary>
    [DataField, AutoNetworkedField]
    public HashSet<int> TriggeredNodeIndexes = new();

    /// <summary>
    /// The time at which the unlocking state ends.
    /// </summary>
    // Triad: absolute CurTime, so a ship saved mid-session used to restore with a window that had
    // already expired on a fresher server, or one that never closed. See NextUnlockTime on
    // XenoArtifactComponent for the same fix and the reasoning.
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan EndTime;

    // Triad: ArtifexiumApplied removed with the reagent

    /// <summary>
    /// The sound that plays when an artifact finishes unlocking successfully (with node unlocked).
    /// </summary>
    [DataField]
    public SoundSpecifier UnlockActivationSuccessfulSound = new SoundCollectionSpecifier("ArtifactUnlockingActivationSuccess")
    {
        Params = new()
        {
            Variation = 0.1f,
            Volume = 3f
        }
    };

    /// <summary>
    /// The sound that plays when artifact finishes unlocking non-successfully.
    /// </summary>
    [DataField]
    public SoundSpecifier? UnlockActivationFailedSound = new SoundCollectionSpecifier("ArtifactUnlockActivationFailure")
    {
        Params = new()
        {
            Variation = 0.1f
        }
    };
}
