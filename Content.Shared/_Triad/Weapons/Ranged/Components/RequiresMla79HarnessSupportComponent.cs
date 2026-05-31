using Robust.Shared.GameStates;

namespace Content.Shared._Triad.Weapons.Ranged.Components;

/// <summary>
/// Marks a gun that receives its full handling only while the user wears a powered MLA-79 harness.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class RequiresMla79HarnessSupportComponent : Component
{
    [DataField, AutoNetworkedField]
    public Angle MinAngleBonus = Angle.FromDegrees(-5);

    [DataField, AutoNetworkedField]
    public Angle MaxAngleBonus = Angle.FromDegrees(-37);

    [DataField, AutoNetworkedField]
    public Angle AngleDecayBonus = Angle.Zero;

    [DataField, AutoNetworkedField]
    public Angle AngleIncreaseBonus = Angle.Zero;

    [DataField, AutoNetworkedField]
    public float PoweredWieldedWalkModifier = 0.9f;

    [DataField, AutoNetworkedField]
    public float PoweredWieldedSprintModifier = 0.8f;

    [DataField, AutoNetworkedField]
    public float UnsupportedWalkModifier = 0.5f;

    [DataField, AutoNetworkedField]
    public float UnsupportedSprintModifier = 0.4f;
}
