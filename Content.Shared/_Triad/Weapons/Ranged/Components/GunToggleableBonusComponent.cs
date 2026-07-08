using Robust.Shared.GameStates;

namespace Content.Shared._Triad.Weapons.Ranged.Components;

/// <summary>
/// Changes the accuracy of the gun when toggled.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class GunToggleableBonusComponent : Component
{
    [DataField, AutoNetworkedField]
    public TimeSpan DoAfterTime = TimeSpan.FromSeconds(0.5);

    [DataField, AutoNetworkedField]
    public Angle MinAngle = Angle.FromDegrees(5);

    /// <summary>
    /// Angle bonus applied upon being toggled. Positive numbers are worse.
    /// </summary>
    [DataField, AutoNetworkedField]
    public Angle MaxAngle = Angle.FromDegrees(5);

    /// <summary>
    /// Recoil bonuses applied upon being toggled.
    /// Higher angle decay bonus, quicker recovery.
    /// </summary>
    [DataField, AutoNetworkedField]
    public Angle AngleDecay = Angle.FromDegrees(0);

    /// <summary>
    /// Recoil bonuses applied upon being toggled.
    /// Lower angle increase bonus (negative numbers), slower buildup.
    /// </summary>
    [DataField, AutoNetworkedField]
    public Angle AngleIncrease = Angle.FromDegrees(0);

    [DataField, AutoNetworkedField]
    public float BonusFireRate = 0f;

    /// <summary>
    /// If true, the gun can only be shot if toggled.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool RequiresToggle = true;

    [DataField]
    public LocId? ExamineMessage = "guntoggleablebonus-component-examine";

    [DataField]
    public LocId RequiresToggledMessage = "guntoggleablebonus-component-requires-unfolded";

    [DataField, AutoNetworkedField]
    public TimeSpan LastPopup;

    [DataField, AutoNetworkedField]
    public TimeSpan PopupCooldown = TimeSpan.FromSeconds(1);
}
