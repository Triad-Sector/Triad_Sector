using Content.Shared.Inventory;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom; // Triad
using Robust.Shared.GameStates; // Frontier

namespace Content.Shared.Storage.Components;

/// <summary>
/// Applies an ongoing pickup area around the attached entity.
/// </summary>
[RegisterComponent, AutoGenerateComponentPause]
[NetworkedComponent, AutoGenerateComponentState] // Frontier
public sealed partial class MagnetPickupComponent : Component
{
    [ViewVariables(VVAccess.ReadWrite), DataField("nextScan", customTypeSerializer: typeof(TimeOffsetSerializer))] // Triad - time offset
    [AutoPausedField]
    public TimeSpan NextScan = TimeSpan.Zero; // Triad - TimeOffsetSerializer: absolute game time, re-based on load so a saved ship does not carry the previous server clock

    /// <summary>
    /// What container slot the magnet needs to be in to work.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField("slotFlags")]
    public SlotFlags SlotFlags = SlotFlags.BELT;

    [ViewVariables(VVAccess.ReadWrite), DataField("range")]
    public float Range = 1f;

    // Frontier: togglable magnets
    /// <summary>
    /// Is the magnet currently enabled?
    /// </summary>
    [AutoNetworkedField, ViewVariables(VVAccess.ReadWrite), DataField]
    public bool MagnetEnabled = true;

    /// <summary>
    /// Is the magnet currently enabled?
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField]
    public bool MagnetCanBeEnabled = true;

    /// <summary>
    /// Is the magnet currently enabled?
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField]
    public int MagnetTogglePriority = 3;
    // End Frontier: togglable magnets
}
