using Robust.Shared.GameStates;

namespace Content.Shared._Triad.Weapons.Ranged.Components;

/// <summary>
///     This component denotes an entity as being able to be damaged by plasma burn stacks.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class PlasmaBurnEligibleComponent : Component;
