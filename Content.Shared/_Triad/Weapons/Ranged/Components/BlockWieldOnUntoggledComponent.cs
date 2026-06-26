using Robust.Shared.GameStates;
using Robust.Shared.Toolshed.Commands.GameTiming;

namespace Content.Shared._Triad.Weapons.Ranged.Components;

/// <summary>
/// Blocks wielding when the datafield BlockWieldOnUntoggled is set to true.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class BlockWieldOnUntoggledComponent : Component
{
    /// <summary>
    /// If true, the gun can only be wielded when toggled.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool BlockWieldOnUntoggled = true;
}
