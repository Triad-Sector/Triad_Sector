using Robust.Shared.GameStates; // Triad - for AutoGenerateComponentPause
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom; // Triad - for TimeOffsetSerializer

namespace Content.Server.Storage.Components;

/// <summary>
/// Applies an ongoing pickup area around the attached entity.
/// </summary>
[RegisterComponent, AutoGenerateComponentPause] // Triad - AutoGenerateComponentPause, see NextScan
public sealed partial class MaterialStorageMagnetPickupComponent : Component
{
    // Triad - TimeOffsetSerializer: a game time, written raw, arrives in the next round as a deadline
    // the new clock will not reach for as long as the old server had been up, and Update skips the
    // magnet until it does. Ore silos and lathes came back from the drydock with their magnets dead
    // (play test, 2026-09-07). The paused marker is what puts the field under the timestamp gate.
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
