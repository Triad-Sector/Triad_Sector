using Robust.Shared.GameStates;

namespace Content.Shared._Triad.HighCommand;

/// <summary>
/// Marks a shuttle grid as cleared to FTL to the TFA High Command outpost.
/// </summary>
/// <remarks>
/// The outpost's <c>FTLDestinationComponent.Whitelist</c> requires this component, and that whitelist is
/// tested against the shuttle grid rather than the pilot (SharedShuttleSystem.CanFTLTo), so clearance is
/// per-hull and not per-player. Grant it with the <c>hcclearance</c> command; it is deliberately not on any
/// ship prototype, so no hull carries it across a round.
/// </remarks>
[RegisterComponent, NetworkedComponent]
public sealed partial class TfaHighCommandClearanceComponent : Component;
