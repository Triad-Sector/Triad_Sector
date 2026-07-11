using Content.Shared.Whitelist;
using Robust.Shared.GameStates;

namespace Content.Shared._Triad.Weapons.Ranged.Components;


/// Marks a gun that receives its full handling only while the user wears a matching powered harness.
/// Weapons and harnesses are paired by <see cref="SupportKey"/>, so new weapon/harness pairs can be defined in YAML.
/// Configures powered spread bonuses, powered/unsupported movement modifiers, and magnetic retrieval eligibility.
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ReqWeapHarnComponent : Component
{
    [DataField, AutoNetworkedField]
    public string SupportKey = "Default";

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

    [DataField("magnetRetrieve")]
    public EntityWhitelist? MagnetRetrive;
}
