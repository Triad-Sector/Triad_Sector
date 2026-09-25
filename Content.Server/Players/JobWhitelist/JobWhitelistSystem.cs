using System.Collections.Immutable;
using Content.Server.GameTicking.Events;
using Content.Server.Station.Events;
using Content.Shared.CCVar;
using Content.Shared.Roles;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server.Players.JobWhitelist;

public sealed partial class JobWhitelistSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private JobWhitelistManager _manager = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    private ImmutableArray<ProtoId<JobPrototype>> _whitelistedJobs = [];
    private ImmutableArray<ProtoId<JobPrototype>> _adminGatedJobs = []; // Triad: admin-gated jobs

    public override void Initialize()
    {
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        SubscribeLocalEvent<StationJobsGetCandidatesEvent>(OnStationJobsGetCandidates);
        SubscribeLocalEvent<IsJobAllowedEvent>(OnIsJobAllowed);
        SubscribeLocalEvent<GetDisallowedJobsEvent>(OnGetDisallowedJobs);

        CacheJobs();
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs ev)
    {
        if (ev.WasModified<JobPrototype>())
            CacheJobs();
    }

    private void OnStationJobsGetCandidates(ref StationJobsGetCandidatesEvent ev)
    {
        // Triad: admin-gated jobs are held back from players without an admin rank whether or not role
        // whitelists are on.
        if (_adminGatedJobs.Length > 0 && _player.TryGetSessionById(ev.Player, out var session))
        {
            for (var i = ev.Jobs.Count - 1; i >= 0; i--)
            {
                if (_adminGatedJobs.Contains(ev.Jobs[i]) && !_manager.IsAllowed(session, ev.Jobs[i]))
                    ev.Jobs.RemoveSwap(i);
            }
        }
        // End Triad

        if (!_config.GetCVar(CCVars.GameRoleWhitelist))
            return;

        for (var i = ev.Jobs.Count - 1; i >= 0; i--)
        {
            var jobId = ev.Jobs[i];
            if (_player.TryGetSessionById(ev.Player, out var player) &&
                !_manager.IsAllowed(player, jobId))
            {
                ev.Jobs.RemoveSwap(i);
            }
        }
    }

    private void OnIsJobAllowed(ref IsJobAllowedEvent ev)
    {
        if (!_manager.IsAllowed(ev.Player, ev.JobId))
            ev.Cancelled = true;
    }

    private void OnGetDisallowedJobs(ref GetDisallowedJobsEvent ev)
    {
        // Triad: admin-gated jobs are disallowed for players without an admin rank whether or not role
        // whitelists are on.
        foreach (var job in _adminGatedJobs)
        {
            if (!_manager.IsAllowed(ev.Player, job))
                ev.Jobs.Add(job);
        }
        // End Triad

        if (!_config.GetCVar(CCVars.GameRoleWhitelist))
            return;

        foreach (var job in _whitelistedJobs)
        {
            if (!_manager.IsAllowed(ev.Player, job))
                ev.Jobs.Add(job);
        }
    }

    private void CacheJobs()
    {
        var builder = ImmutableArray.CreateBuilder<ProtoId<JobPrototype>>();
        var adminGated = ImmutableArray.CreateBuilder<ProtoId<JobPrototype>>(); // Triad: admin-gated jobs
        foreach (var job in _prototypes.EnumeratePrototypes<JobPrototype>())
        {
            if (job.Whitelisted)
                builder.Add(job.ID);

            if (job.AdminWhitelist) // Triad: admin-gated jobs
                adminGated.Add(job.ID); // Triad: admin-gated jobs
        }

        _whitelistedJobs = builder.ToImmutable();
        _adminGatedJobs = adminGated.ToImmutable(); // Triad: admin-gated jobs
    }
}
