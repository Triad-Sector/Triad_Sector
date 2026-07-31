using Robust.Shared.GameStates;

namespace Content.Shared._Triad.ContrabandPermit;

/// <summary>
/// Items with this component can be permitted for use, which allows them to be saved on ships.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ContrabandPermittableComponent : Component
{
    /// <summary>
    /// This purely exists for parenting
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool Permittable = true;

    [DataField, AutoNetworkedField]
    public LocId ExamineText = "contraband-permittable-examine-default";
}
