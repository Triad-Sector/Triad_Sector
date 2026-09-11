using Content.Server.NPC.Systems;
using Content.Shared.Whitelist;

// Mono - whole file

namespace Content.Server.NPC.Queries.Queries;

/// <summary>
/// Returns nearby entities tagged with ShipNpcTargetComponent.
/// </summary>
public sealed partial class NearbyNpcTargetsQuery : UtilityQuery
{
    [DataField]
    // Triad: revert of Mono #3728 (2x targeting range), halved back for perf.
    public float Range = 2000f;

    [DataField]
    public EntityWhitelist Blacklist = new();
}
