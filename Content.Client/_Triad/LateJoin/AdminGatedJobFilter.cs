using Content.Client.Players.PlayTimeTracking;
using Content.Shared.GameTicking;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Client._Triad.LateJoin;

/// <summary>
/// Drops admin-gated jobs a player cannot take out of the latejoin station list, so the lobby never names them.
/// </summary>
/// <remarks>
/// Only <see cref="JobPrototype.AdminWhitelist"/> jobs are dropped. Jobs a player is denied for playtime, species or a
/// database whitelist stay listed, disabled, because their tooltip is how a player learns what to work toward.
/// </remarks>
public static class AdminGatedJobFilter
{
    /// <summary>
    /// Returns the stations with every admin-gated job the player fails <see cref="JobRequirementsManager.CheckWhitelist"/>
    /// on removed. A station left with no jobs by that is removed too; a station that arrived with none is kept.
    /// </summary>
    public static Dictionary<NetEntity, StationJobInformation> Filter(
        IReadOnlyDictionary<NetEntity, StationJobInformation> stations,
        IPrototypeManager prototypes,
        JobRequirementsManager requirements)
    {
        var result = new Dictionary<NetEntity, StationJobInformation>(stations.Count);

        foreach (var (station, info) in stations)
        {
            var jobs = new Dictionary<ProtoId<JobPrototype>, int?>(info.JobsAvailable.Count);
            foreach (var (job, slots) in info.JobsAvailable)
            {
                if (prototypes.TryIndex(job, out var proto)
                    && proto.AdminWhitelist
                    && !requirements.CheckWhitelist(proto, out _))
                {
                    continue;
                }

                jobs.Add(job, slots);
            }

            if (jobs.Count == info.JobsAvailable.Count)
            {
                result.Add(station, info);
                continue;
            }

            if (jobs.Count == 0)
                continue;

            result.Add(station, new StationJobInformation(
                info.StationName,
                jobs,
                info.IsLateJoinStation,
                info.StationDisplayInfo,
                info.VesselDisplayInformation));
        }

        return result;
    }
}
