namespace Content.Server._Triad.Ghost;

/// <summary>
/// Placed on a map entity. Ghosts without an active admin rank cannot list, warp to or ghostnado onto anything on
/// that map; GhostSystem checks it on all three paths.
/// </summary>
[RegisterComponent]
public sealed partial class AdminOnlyWarpMapComponent : Component;
