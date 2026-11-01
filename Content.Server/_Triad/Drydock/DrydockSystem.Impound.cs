using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Content.Server._NF.PublicTransit;
using Content.Server.Nuke;
using Content.Server.Spawners.Components;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Anomaly.Components;
using Content.Shared.Buckle;
using Content.Shared.Explosion.Components;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared.Mobs.Components;
using Content.Shared.Nuke;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Content.Shared.Singularity.Components;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// What an impound does that an ordinary store will not: clear the two gates that exist to refuse a
/// hull nobody can safely put away, so that a hull nobody is coming back for can be put away anyway.
///
/// <para>An ordinary store refuses and the player tries again. An impound has nowhere to refuse to,
/// because the alternative to taking the hull is leaving it in a round that is ending. So the gates
/// that protect a store from doing damage become instructions to do the damage deliberately, and
/// only those two: a document that will not write or will not read back still fails, because those
/// are the failures an impound cannot paper over.</para>
///
/// <para>That damage is not undone when one of those two failures follows it. A hazard is deleted
/// and an occupant is moved at the gates, before serialize and validate can run, because a mind
/// must never reach the document and a countdown must never resume from one; a refusal after them
/// hands the hull back to its station intact but emptied, with its crew at a bus stop. Both are
/// survivable and neither is a duplicate, which is the trade the gates exist to make.</para>
/// </summary>
public sealed partial class DrydockSystem
{
    [Dependency] private SharedBuckleSystem _buckle = default!;
    [Dependency] private PublicTransitSystem _transit = default!;
    [Dependency] private SharedMindSystem _minds = default!;
    [Dependency] private SharedJobSystem _jobs = default!;
    [Dependency] private StationJobsSystem _stationJobs = default!;

    /// <summary>
    /// Takes a live hull into the impound lot: the ordinary store pipeline with its two refusal
    /// gates turned into instructions, landing the row on
    /// <see cref="DrydockShipState.Impounded"/> with no berth.
    ///
    /// <para>The hull is always saved first. There is no impounded-while-flying, which is the one
    /// thing the freeze it replaces could never say: a row that reads impounded while a grid still
    /// carries the ship is the duplicate every state transition here exists to prevent.</para>
    /// </summary>
    /// <param name="terms">
    /// The fee share, the reason, whether the owner may act on it, and who is taking it. The fee is
    /// taken against the appraisal measured during this very store, so the credits owed can never
    /// exceed what the hull is worth.
    /// </param>
    /// <param name="inline">
    /// Run the pipeline unsliced on this caller's path. The round-end sweep sets it; the admin
    /// ripcord, taken mid-round with players to protect, leaves it off.
    /// </param>
    public Task<(DrydockStoreResult Result, Guid? ShipId)> TryImpoundShip(
        EntityUid gridUid,
        Guid ownerUserId,
        int? roundId,
        DrydockImpound terms,
        EntityUid? stationUid = null,
        DrydockProgressCallback? onProgress = null,
        bool inline = false)
    {
        return TryStoreShip(gridUid, ownerUserId, roundId, berthId: null, stationUid, onProgress, terms, inline);
    }

    /// <summary>
    /// Clears what <see cref="HasHazardAboard"/> refuses for, so the gate that follows has nothing
    /// left to find. Each of the four gets the treatment its reason for blocking calls for, and
    /// those are not the same treatment.
    ///
    /// <para>An armed nuke, a singularity and an anomaly ARE the hazard, so they go. An anomaly's own
    /// shutdown ends it without dropping a core, so nothing is left behind to take its place.
    /// <c>ActiveTimerTriggerComponent</c> is not a hazard component at all: the trigger system adds
    /// it to anything with a running countdown, so deleting every carrier would take innocuous
    /// hardware with it. The narrow reason a countdown blocks is that it is an ordinary data field
    /// which would resume on thaw, so removing the component cancels the countdown and keeps the
    /// object.</para>
    ///
    /// <para>Deleted rather than queued, because the gate this exists to satisfy runs in the same
    /// tick and a queued deletion is still there when it looks. Collected before acting, because
    /// deleting inside an <c>AllEntityQuery</c> walk mutates what is being walked.</para>
    /// </summary>
    private void PurgeHazardsAboard(EntityUid gridUid)
    {
        var doomed = new List<EntityUid>();
        var disarm = new List<EntityUid>();

        if (!CollectHazards(gridUid, doomed, disarm))
            return;

        foreach (var uid in disarm)
            RemComp<ActiveTimerTriggerComponent>(uid);

        foreach (var uid in doomed)
            Del(uid);

        Log.Info($"Drydock: impound cleared {doomed.Count} hazard(s) and {disarm.Count} countdown(s) from {ToPrettyString(gridUid)}.");
    }

    /// <summary>
    /// Moves every mind off the hull, so the gate that follows has nobody left to refuse for. A mind
    /// must never reach the document, and an impound cannot answer "somebody is aboard" with "then
    /// not today", so it answers it by emptying the ship.
    /// </summary>
    /// <returns>How many were moved, for the log line and the audit reason.</returns>
    private int EvictOrganicsAboard(DrydockStoreContext ctx)
    {
        var gridUid = ctx.GridUid;

        // The organics gate's own walk, collecting instead of stopping at the first: anything the
        // gate would find and the eviction missed would refuse an impound at a gate with no refusal
        // left, so the two share one predicate.
        var aboard = new List<EntityUid>();
        _shipyard.FoundOrganics(gridUid, GetEntityQuery<MobStateComponent>(), GetEntityQuery<TransformComponent>(), aboard);

        if (aboard.Count == 0)
            return 0;

        // One search per job, not per occupant: a crew of five deckhands shares one answer.
        var drops = new Dictionary<string, EntityCoordinates>();

        foreach (var uid in aboard)
        {
            var job = JobOfOccupant(uid);
            var jobKey = job?.Id ?? string.Empty;
            if (!drops.TryGetValue(jobKey, out var drop))
            {
                drop = FindImpoundDropOff(ctx, job);
                drops[jobKey] = drop;
            }

            // Both of these keep an occupant parented to the hull, so a bare move leaves them
            // aboard and the gate refuses a hull that has already been emptied on paper. Forced,
            // because every ordinary reason to say no to a removal is outranked here.
            _buckle.TryUnbuckle(uid, uid, popup: false);
            _containers.TryRemoveFromContainer(uid, force: true);

            // Leaving the paused staging map, when this runs after the freeze, is what unpauses
            // them: the engine re-derives an entity's pause state from the map it lands on.
            _xform.SetCoordinates(uid, drop);
        }

        Log.Info($"Drydock: impound moved {aboard.Count} occupant(s) off {ToPrettyString(gridUid)}.");
        return aboard.Count;
    }

    /// <summary>
    /// The job an occupant's mind holds, or null for a body with no mind or no job (a mob, a pet,
    /// a borg brain nobody is in).
    /// </summary>
    private ProtoId<JobPrototype>? JobOfOccupant(EntityUid uid)
    {
        return _minds.TryGetMind(uid, out var mindId, out _) && _jobs.MindTryGetJobId(mindId, out var job)
            ? job
            : null;
    }

    /// <summary>
    /// Lifts every ghost off the hull, admin ghosts included, on every store and not only an impound.
    /// A ghost never blocks a store (the organics gate skips it by design), but one still parented to
    /// the hull rides the freeze onto the private staging map and is deleted with the grid, which
    /// strands an admin mid-aghost. Put down where it floated, on the home map, or where the hull was
    /// when the hull has already left.
    /// </summary>
    private void EvictGhostsAboard(DrydockStoreContext ctx)
    {
        var gridUid = ctx.GridUid;
        if (ctx.HomeMap is not { } home)
            return;

        // A rare-component query like the hazard gate's, not a hull walk: this runs at every organics
        // gate, and the server's ghosts are far fewer than a hull's entities.
        var ghosts = new List<EntityUid>();
        var query = AllEntityQuery<GhostComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid == gridUid)
                ghosts.Add(uid);
        }

        if (ghosts.Count == 0)
            return;

        var gridStillHome = Transform(gridUid).MapUid == home;

        foreach (var uid in ghosts)
        {
            _containers.TryRemoveFromContainer(uid, force: true);
            var position = gridStillHome ? _xform.GetWorldPosition(uid) : ctx.HomePosition;
            _xform.SetCoordinates(uid, new EntityCoordinates(home, position));
        }

        Log.Info($"Drydock: lifted {ghosts.Count} ghost(s) off {ToPrettyString(gridUid)} before it was stored.");
    }

    /// <summary>
    /// Where an evicted occupant lands: a spawn point on the map the hull came from, and on that
    /// map only, chosen for the occupant's role. A spawn point for their own job comes first, then any
    /// spawn point on a station that offers their job, so a faction crew lands at their own base and
    /// a contractor at the civilian outpost, and only then anywhere at all. Within a tier bus service
    /// outranks distance, because somewhere close with no way to leave is worse than somewhere
    /// further along a route: the whole point of putting a player down rather than deleting them is
    /// that they can carry on playing. Player ships are never a drop-off, whatever jobs they list.
    ///
    /// <para>The home map is read off the context, never the grid, because after the freeze the grid
    /// is on a private staging map and a query keyed on its transform would either find nothing or,
    /// unfiltered, find spawn points on every map at once: another sector, or a hull mid-store on a
    /// paused map of its own, where a body put down past that store's last organics gate is written
    /// into its document. A grid that is itself one tick from its own freeze is skipped for the same
    /// reason.</para>
    ///
    /// <para>Observer spawns are skipped because they are ghost markers, and a body put on one is
    /// standing in whatever the mapper thought a ghost could occupy.</para>
    ///
    /// <para>The last rung is not a refusal. An impound never refuses, so a sector that offers no
    /// spawn point at all still takes the hull, and the occupants are left on the home map at the
    /// hull's own position: whatever they are wearing, wherever they were, which is survivable and
    /// reversible. Being written into the document is neither.</para>
    /// </summary>
    private EntityCoordinates FindImpoundDropOff(DrydockStoreContext ctx, ProtoId<JobPrototype>? job)
    {
        var gridUid = ctx.GridUid;

        EntityCoordinates? best = null;
        var bestRank = (Role: 0, Unserved: 0, Distance: 0f);

        if (ctx.HomeMap is { } home)
        {
            var spawns = AllEntityQuery<SpawnPointComponent, TransformComponent>();
            while (spawns.MoveNext(out var uid, out var spawn, out var xform))
            {
                if (spawn.SpawnType == SpawnPointType.Observer)
                    continue;

                if (xform.MapUid != home)
                    continue;

                if (xform.GridUid is not { } grid || grid == gridUid)
                    continue;

                if (HasComp<DrydockInProgressComponent>(grid))
                    continue;

                // Somebody's ship lists crew jobs too, and is no place to put a stranger down.
                if (HasComp<ShipOwnershipComponent>(grid) || HasComp<ShuttleDeedComponent>(grid))
                    continue;

                var rank = (
                    Role: RoleTier(uid, spawn, xform, job),
                    Unserved: _transit.StationList.Contains(grid) ? 0 : 1,
                    Distance: (_xform.GetWorldPosition(uid) - ctx.HomePosition).Length());

                if (best != null && rank.CompareTo(bestRank) >= 0)
                    continue;

                best = xform.Coordinates;
                bestRank = rank;
            }
        }

        if (best is { } found)
            return found;

        Log.Error($"Drydock: impound of {ToPrettyString(gridUid)} found no spawn point on its home map; its occupants stay on that map where the hull was.");

        return ctx.HomeMap is { } map
            ? new EntityCoordinates(map, ctx.HomePosition)
            : EntityCoordinates.Invalid;
    }

    /// <summary>
    /// How well a spawn point fits an occupant's role, lower is better: 0 is a spawn point for their
    /// job, 1 is any spawn point on a station that offers their job, 2 is anything else. An occupant
    /// with no job ranks every point 2, which is the pre-role behaviour.
    /// </summary>
    private int RoleTier(EntityUid spawnUid, SpawnPointComponent spawn, TransformComponent xform, ProtoId<JobPrototype>? job)
    {
        if (job is not { } id)
            return 2;

        if (spawn.SpawnType == SpawnPointType.Job && spawn.Job == id)
            return 0;

        return _station.GetOwningStation(spawnUid, xform) is { } station
               && TryComp<StationJobsComponent>(station, out var jobs)
               && _stationJobs.GetJobs(station, jobs).ContainsKey(id)
            ? 1
            : 2;
    }
}
