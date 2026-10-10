using Robust.Shared.GameStates;

namespace Content.Shared._Triad.HighCommand;

/// <summary>
/// Marks a shuttle grid as cleared to FTL to the TFA High Command outpost.
/// </summary>
/// <remarks>
/// The outpost's <c>FTLDestinationComponent.Whitelist</c> requires this component, and that whitelist is
/// tested against the shuttle grid rather than the pilot (SharedShuttleSystem.CanFTLTo), so clearance is
/// per-hull and not per-player. Grant it with the <c>hcclearance</c> command. It is on no ship prototype and no
/// grid save writes it (shipyard save or map save), so a cleared hull loses clearance when it is saved and
/// loaded again.
/// </remarks>
[RegisterComponent, NetworkedComponent, UnsavedComponent]
public sealed partial class TfaHighCommandClearanceComponent : Component;
