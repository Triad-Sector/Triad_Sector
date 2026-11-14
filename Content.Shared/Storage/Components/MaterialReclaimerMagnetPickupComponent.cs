using Robust.Shared.GameStates; // Triad - for AutoGenerateComponentPause
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom; // Triad - for TimeOffsetSerializer

namespace Content.Server.Storage.Components;

/// <summary>
/// Applies an ongoing pickup area around the attached entity.
/// </summary>
[RegisterComponent, AutoGenerateComponentPause] // Triad - AutoGenerateComponentPause, see NextScan
public sealed partial class MaterialReclaimerMagnetPickupComponent : Component
{
    // Triad - TimeOffsetSerializer: same fault as MaterialStorageMagnetPickupComponent.NextScan, on
    // the reclaimer's magnet. A raw game time stored in one round is a far-future deadline in the
    // next, and the magnet sleeps until the clock reaches it. The paused marker puts it under the
    // timestamp gate.
    [ViewVariables(VVAccess.ReadWrite), DataField("nextScan", customTypeSerializer: typeof(TimeOffsetSerializer))]
    [AutoPausedField]
    public TimeSpan NextScan = TimeSpan.Zero;

    [ViewVariables(VVAccess.ReadWrite), DataField("range")]
    public float Range = 1f;

    /// <summary>
    /// Frontier - Is the magnet currently enabled?
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField("magnetEnabled")]
    public bool MagnetEnabled = false;
}
