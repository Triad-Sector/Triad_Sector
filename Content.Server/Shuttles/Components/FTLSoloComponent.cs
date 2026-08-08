namespace Content.Server.Shuttles.Components;

/// <summary>
/// A grid that never takes docked grids with it when it FTLs, regardless of their FTL locks.
/// </summary>
/// <remarks>
/// Triad: for scheduled traffic that runs on its own timer rather than at someone's command. The
/// public transit bus departs every time its clock says so, whether or not a ship happens to be
/// docked at that instant, so consent to travel together cannot meaningfully have been given. The
/// FTL lock is the right mechanism for two ships that chose to fly as a pair; this is for grids
/// that should never form a pair in the first place.
/// </remarks>
[RegisterComponent]
public sealed partial class FTLSoloComponent : Component;
