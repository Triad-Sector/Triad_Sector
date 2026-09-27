using Content.Client.Players.PlayTimeTracking;
using Content.Shared.GameTicking;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Client._Triad.Lobby;

/// <summary>
/// Keeps admin-gated jobs a player cannot take out of the lobby's job lists, the latejoin station list and the
/// character editor's departments, so the lobby never names them.
/// </summary>
/// <remarks>
/// Only <see cref="JobPrototype.AdminWhitelist"/> jobs are dropped. Jobs a player is denied for playtime, species or a
/// database whitelist stay listed, disabled, because their tooltip is how a player learns what to work toward.
/// </remarks>
public static class AdminGatedJobFilter
{
    /// <summary>
    /// Whether the lobby leaves <paramref name="job"/> out for this player: it is admin-gated and the player fails
    /// <see cref="JobRequirementsManager.CheckWhitelist"/> on it.
    /// </summary>
    public static bool Hides(JobPrototype job, JobRequirementsManager requirements)
    {
        return job.AdminWhitelist && !requirements.CheckWhitelist(job, out _);
    }

    /// <summary>
    /// Whether every job the character editor would list under <paramref name="department"/> is one
    /// <see cref="Hides"/> drops, so the department goes too, header included. A department that lists no jobs to
    /// begin with is kept.
    /// </summary>
    public static bool EmptiesDepartment(
        DepartmentPrototype department,
        IPrototypeManager prototypes,
        JobRequirementsManager requirements)
    {
        var listed = false;
        foreach (var id in department.Roles)
        {
            if (!prototypes.TryIndex(id, out var job) || !job.SetPreference)
                continue;

            if (!Hides(job, requirements))
                return false;

            listed = true;
        }

        return listed;
    }

    /// <summary>
    /// Returns the stations with every job <see cref="Hides"/> drops removed. A station left with no jobs by that is
    /// removed too; a station that arrived with none is kept.
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
                if (prototypes.TryIndex(job, out var proto) && Hides(proto, requirements))
                    continue;

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
