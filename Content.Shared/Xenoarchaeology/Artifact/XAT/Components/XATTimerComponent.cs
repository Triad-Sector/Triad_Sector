using Content.Shared.Destructible.Thresholds;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom; // Triad: TimeOffsetSerializer

namespace Content.Shared.Xenoarchaeology.Artifact.XAT.Components;

/// <summary>
/// This is used for a xenoarch trigger that self-activates at a regular interval
/// </summary>
[RegisterComponent, NetworkedComponent, Access(typeof(XATTimerSystem)), AutoGenerateComponentState, AutoGenerateComponentPause]
public sealed partial class XATTimerComponent : Component
{
    /// <summary>
    /// Next time timer going to activate.
    /// </summary>
    // Triad: absolute CurTime, same defect as NextUnlockTime on XenoArtifactComponent. Dormant while
    // TriggerTimer stays commented out in triggers.yml, but the field would go stale on a ship save
    // the moment anyone re-enables it.
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan NextActivation;

    /// <summary>
    /// Delay between activations.
    /// </summary>
    [DataField, AutoNetworkedField]
    public MinMax PossibleDelayInSeconds;
}
