using Robust.Shared.GameStates;
using Robust.Shared.Toolshed.Commands.GameTiming;

namespace Content.Shared._Triad.Weapons.Ranged.Components;

/// <summary>
/// When tried to wield but untoggled state.
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
