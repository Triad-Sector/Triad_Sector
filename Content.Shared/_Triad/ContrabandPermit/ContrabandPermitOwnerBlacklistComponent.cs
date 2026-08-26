using Robust.Shared.GameStates;

namespace Content.Shared._Triad.ContrabandPermit;

/// <summary>
///     Entities with this component will not be able to own permits. Useful for hostile invaders and TDF enforcers.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class ContrabandPermitOwnerBlacklistComponent : Component;
