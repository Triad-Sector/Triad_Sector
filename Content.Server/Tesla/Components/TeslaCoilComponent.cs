using Content.Server.Tesla.EntitySystems;

namespace Content.Server.Tesla.Components;

/// <summary>
/// Generates electricity from lightning bolts
/// </summary>
[RegisterComponent, Access(typeof(TeslaCoilSystem))]
public sealed partial class TeslaCoilComponent : Component
{
    /// <summary>
    /// How much power will the coil generate from a lightning strike
    /// </summary>
    // To Do: Different lightning bolts have different powers and generate different amounts of energy
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float ChargeFromLightning = 50000f;

    /// <summary>
    /// Triad: strike-chance floor once the battery is full. Lightning favors the biggest potential
    /// difference, so the coil's effective hit chance scales with charge headroom: empty = 1.0
    /// (plus a priority bump above every static target), full = this floor. A floored coil still
    /// outranks grounding rods in the sort but gets skipped on almost every roll, so overflow
    /// falls through to the rods.
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float SaturatedHitProbability = 0.05f;

    /// <summary>
    /// Triad: priority added on top of the coil's static LightningTarget priority while the battery
    /// is completely empty, so a fresh coil catches the next bolt ahead of every charged coil.
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public int EmptyPriorityBonus = 1;
}
