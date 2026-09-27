using Content.Server.Station;
using Robust.Shared.Utility;

namespace Content.Server._Triad.HighCommand;

/// <summary>
/// Spawns the TFA High Command outpost on a map of its own at round start.
/// </summary>
[RegisterComponent, Access(typeof(HighCommandOutpostRuleSystem))]
public sealed partial class HighCommandOutpostRuleComponent : Component
{
    /// <summary>
    /// Grid loaded onto the outpost's private map.
    /// </summary>
    [DataField(required: true)]
    public ResPath GridPath;

    /// <summary>
    /// Station built over the loaded grid, so the outpost can carry job slots.
    /// </summary>
    [DataField(required: true)]
    public StationConfig StationConfig = default!;

    [DataField]
    public EntityUid? MapEntity;

    [DataField]
    public EntityUid? GridEntity;

    [DataField]
    public EntityUid? Station;
}
