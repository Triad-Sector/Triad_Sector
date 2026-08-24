using Content.Server.Physics.Controllers;
using Content.Shared.Physics;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server.Physics.Components;

/// <summary>
/// A component which makes its entity periodically chaotic jumps arounds
/// </summary>
[RegisterComponent, Access(typeof(ChaoticJumpSystem))]
public sealed partial class ChaoticJumpComponent : Component
{
    /// <summary>
    /// The next moment in time when the entity is pushed toward its goal
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan NextJumpTime;

    /// <summary>
    /// Minimum interval between jumps
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float JumpMinInterval = 5f;
    /// <summary>
    /// Maximum interval between jumps
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float JumpMaxInterval = 15f;

    /// <summary>
    /// collision limits for which it is impossible to make a jump
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public int CollisionMask = (int) CollisionGroup.Impassable;

    /// <summary>
    /// Minimum jump range
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float RangeMin = 5f;

    /// <summary>
    /// Maximum jump range
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float RangeMax = 10f;

    /// <summary>
    /// Spawn before jump
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public EntProtoId Effect = "EffectEmpPulse";

    /// <summary>
    /// Triad: radius of the footprint swept along the jump path. The jump cannot land the entity past a
    /// gap narrower than this, which is what stops it teleporting through containment-field corner slots.
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float SweepRadius = 0.35f;

    /// <summary>
    /// Triad: how far short of a swept contact the entity lands, so it does not end up flush against the obstacle.
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float SweepSkin = 0.1f;
}
