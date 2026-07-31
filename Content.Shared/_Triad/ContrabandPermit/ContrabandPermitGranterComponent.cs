using Robust.Shared.GameStates;

namespace Content.Shared._Triad.ContrabandPermit;

/// <summary>
///     Entities with this component will be able to grant permits to players.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class ContrabandPermitGranterComponent : Component;
