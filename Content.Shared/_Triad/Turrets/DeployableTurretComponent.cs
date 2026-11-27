namespace Content.Shared.Turrets;

public sealed partial class DeployableTurretComponent : Component
{
    /// <summary>
    /// Whether the turret is starts on.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool StartEnabled = false;
}
