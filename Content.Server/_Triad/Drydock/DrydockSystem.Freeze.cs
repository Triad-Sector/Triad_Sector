using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._Triad.CCVar;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.CPUJob.JobQueues;
using Robust.Shared.CPUJob.JobQueues.Queues;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// Freeze, thaw, and the machinery that makes both survivable across ticks.
///
/// <para>The premise: a store costs about two seconds of main-thread time, and it is fine to make
/// the one captain who pressed the button wait as long as it takes, but it is not fine to stall
/// sixty other players for a reason they cannot perceive. So the work is sliced over ticks - and
/// the moment the work spans ticks, the ship it is reading has to stop moving, or a sliced
/// serialize tears its own snapshot.</para>
///
/// <para>The mechanism is the engine's own pause flag, applied by moving the grid onto a private
/// map that was paused while it was still empty. Measured across sixteen roster hulls of 144 to 960
/// entities: about three milliseconds total, and the only difference it makes to the written
/// document is one <c>paused: true</c> per entity. A frozen ship then holds perfectly still, against
/// a live-map control that drifts 46 to 240 lines over the same span.</para>
/// </summary>
public sealed partial class DrydockSystem
{
    [Dependency] private MetaDataSystem _meta = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _xform = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private ShuttleConsoleSystem _shuttleConsole = default!;

    /// <summary>
    /// Floor under the queue's per-tick budget. The queue tests its clock before it dequeues
    /// anything, so a budget of zero there stops every job from ever running, including one already
    /// in flight when an admin turns slicing off mid-store: that job would hang until the watchdog
    /// fired and its unwind could never run either. A cvar of zero means no job is made at all,
    /// which the caller handles; this only keeps an in-flight one draining.
    /// </summary>
    private const double MinQueueTime = 0.0005;

    /// <summary>
    /// Ours, because the shuttle system's startup sound is a private readonly field and widening an
    /// upstream field for one caller is a merge conflict for nothing. The volume matches upstream's,
    /// so a faked departure sounds exactly like a real jump.
    /// </summary>
    private static readonly SoundSpecifier DepartureSound =
        new SoundPathSpecifier("/Audio/Effects/Shuttle/hyperspace_begin.ogg")
        {
            Params = AudioParams.Default.WithVolume(-5f),
        };

    /// <summary>
    /// The queue's budget has to be the cvar, not a constant. <see cref="JobQueue.Process"/> tests
    /// its clock before each dequeue and re-enqueues a suspended job immediately, so it re-runs the
    /// same job until its own clock is spent: a constant larger than the job's max time admits two
    /// or three runs per tick, and the constant rather than the cvar becomes what a tick costs.
    /// Overridable because <see cref="JobQueue.MaxTime"/> is virtual; set once per tick in
    /// <see cref="ProcessJobs"/> rather than captured, so the cvar stays live.
    /// </summary>
    private sealed class DrydockJobQueue : JobQueue
    {
        public double Budget = MinQueueTime;

        public override double MaxTime => Budget;
    }

    private readonly DrydockJobQueue _jobQueue = new();

    /// <summary>
    /// Every job this system has started and not yet retired, by the id stamped on its staging maps.
    /// The registry is what lets a staging map name an owner that can be asked whether it is still
    /// alive, which is the whole basis of the orphan sweep.
    /// </summary>
    private readonly Dictionary<int, (IJob Job, CancellationTokenSource Cancellation)> _liveJobs = new();

    /// <summary>Scratch for the two places that must not mutate a dictionary or a query while reading it.</summary>
    private readonly List<int> _jobScratch = new();

    private readonly List<EntityUid> _stagingScratch = new();

    /// <summary>Ids start at one, so zero is free to mean "no job at all" - the synchronous path.</summary>
    private int _nextJobId = 1;

    // --- job plumbing -----------------------------------------------------------------

    /// <summary>
    /// Registers a job and the token source that cancels it. The returned id is what gets stamped on
    /// every staging map the job creates, so a map left behind can be traced back to an owner that
    /// either still exists or does not.
    /// </summary>
    internal int RegisterJob(IJob job, CancellationTokenSource cts)
    {
        var id = _nextJobId++;
        _liveJobs[id] = (job, cts);
        return id;
    }

    internal void EnqueueJob(IJob job)
    {
        _jobQueue.EnqueueJob(job);
    }

    /// <summary>
    /// The start half every sliced wrapper shares: a token source, the job, its registration and its
    /// place in the queue. The id is stamped on the job before it is enqueued, so the pipeline has it
    /// before the first staging map it creates. From here <see cref="RetireJob"/> owns the token
    /// source; pair every call with <see cref="EndPipelineJob{TContext,TOutcome}"/> in a finally.
    /// </summary>
    private DrydockPipelineJob<TContext, TOutcome> StartPipelineJob<TContext, TOutcome>(
        TContext context,
        double budget,
        DrydockProgressCallback? onProgress,
        IReadOnlyList<DrydockPhase> phases,
        Func<TContext, IDrydockSlice, Task<TOutcome>> pipeline)
    {
        var cancellation = new CancellationTokenSource();
        var job = new DrydockPipelineJob<TContext, TOutcome>(
            this, context, budget, SliceStride, onProgress, cancellation.Token, phases, pipeline);

        job.JobId = RegisterJob(job, cancellation);
        EnqueueJob(job);
        return job;
    }

    /// <summary>
    /// The finish half: retires the job and records its meter. Null means the pipeline ran on the
    /// rollback lever with no job, which still clears the cost table rather than leaving the previous
    /// sliced run's for the next reader to mistake for this one's.
    /// </summary>
    private void EndPipelineJob<TContext, TOutcome>(DrydockPipelineJob<TContext, TOutcome>? job)
    {
        if (job != null)
            RetireJob(job.JobId);

        RecordPhaseCosts(job?.Meter);
    }

    /// <summary>Disposes the token source and drops the registry entry. Idempotent.</summary>
    internal void RetireJob(int jobId)
    {
        if (!_liveJobs.Remove(jobId, out var entry))
            return;

        entry.Cancellation.Dispose();
    }

    /// <summary>
    /// Cancels every live job. Each one observes the cancellation at its next resume and runs its own
    /// unwind, which is what puts the ship back; this only asks. Disposal stays with
    /// <see cref="RetireJob"/>, because the wrappers still hold these jobs and still have finallys to
    /// run against them.
    /// </summary>
    internal void CancelAllJobs(string reason)
    {
        if (_liveJobs.Count == 0)
            return;

        Log.Info($"Drydock: cancelling {_liveJobs.Count} in-flight pipeline(s): {reason}.");

        foreach (var (_, entry) in _liveJobs)
        {
            if (entry.Job.Status == JobStatus.Finished)
                continue;

            entry.Cancellation.Cancel();
        }
    }

    /// <summary>The current tick, for the span meter to bucket by. Value rather than GameTick so the
    /// meter does not need an engine type.</summary>
    internal uint CurTickValue => _timing.CurTick.Value;

    /// <summary>
    /// Per-phase cost of the last store or retrieve that ran a job, for the local timing rig.
    /// Cleared rather than left standing when a pipeline runs without one, because the benchmark
    /// runs both arms in the same server and a stale table read as the other arm's would be a
    /// fabricated result rather than a missing one. <see cref="LastPhaseCostsRun"/> is the tripwire
    /// that lets a caller prove the table belongs to the run it just made.
    /// </summary>
    internal IReadOnlyDictionary<DrydockPhase, DrydockPhaseCost>? LastPhaseCosts { get; private set; }

    /// <summary>Monotonic id of the pipeline that filled <see cref="LastPhaseCosts"/>.</summary>
    internal int LastPhaseCostsRun { get; private set; }

    /// <summary>Worst whole tick, across every phase, of the pipeline that filled <see cref="LastPhaseCosts"/>.</summary>
    internal double LastWorstTickMs { get; private set; }

    private int _phaseCostRun;

    /// <summary>
    /// Records a finished pipeline's meter. Called from the wrapper's finally on every path, so a
    /// cancelled or refused run clears the table instead of leaving the previous one to be read as
    /// its own.
    /// </summary>
    internal void RecordPhaseCosts(DrydockSpanMeter? meter)
    {
        LastPhaseCosts = meter?.Costs;
        LastWorstTickMs = meter?.WorstTickMs ?? 0;
        LastPhaseCostsRun = ++_phaseCostRun;
    }

    /// <summary>Job budget in seconds: the cvar's milliseconds over a thousand. Zero or less means no job.</summary>
    internal double TickBudgetSeconds => Math.Max(0, _cfg.GetCVar(TriadCCVars.DrydockTickBudgetMs)) / 1000.0;

    internal int SliceStride => Math.Max(1, _cfg.GetCVar(TriadCCVars.DrydockSliceStride));

    /// <summary>
    /// Drains the job queue, then runs the watchdog. Called first from the system's one update, so a
    /// job parked on a database continuation resumes in the same tick that continuation landed: the
    /// server drains the synchronisation context immediately before it ticks the systems.
    ///
    /// <para>The watchdog is the release-mode net under the rule that every await inside a pipeline
    /// goes through the slice. A missed wrapper leaves the job with no resume handle, which asserts
    /// in debug and silently hangs in release, and a hung store means a frozen ship parked on a
    /// private map for the rest of the round. Cancelling ends it in an unwind instead.</para>
    /// </summary>
    internal void ProcessJobs()
    {
        _jobQueue.Budget = Math.Max(MinQueueTime, TickBudgetSeconds);
        _jobQueue.Process();

        if (_liveJobs.Count == 0)
            return;

        var watchdog = _cfg.GetCVar(TriadCCVars.DrydockSliceWatchdogSeconds);

        _jobScratch.Clear();
        foreach (var (id, entry) in _liveJobs)
        {
            // A finished job whose wrapper has not retired it yet is not a leak worth keeping: the
            // pipeline is over, nothing will run it again, and leaving it in the registry would keep
            // its staging maps looking owned forever.
            if (entry.Job.Status == JobStatus.Finished)
            {
                _jobScratch.Add(id);
                continue;
            }

            if (watchdog <= 0 || entry.Cancellation.IsCancellationRequested)
                continue;

            if (!TryGetProgressAge(entry.Job, out var seconds) || seconds < watchdog)
                continue;

            Log.Error($"Drydock: pipeline {id} has not advanced in {seconds:F0}s; cancelling it. "
                      + "This is very likely an await inside the pipeline that did not go through the slice.");
            entry.Cancellation.Cancel();
        }

        foreach (var id in _jobScratch)
            RetireJob(id);
    }

    /// <summary>
    /// How long since a job last advanced. Only drydock pipeline jobs report it; anything else in the
    /// queue has no watchdog.
    /// </summary>
    private static bool TryGetProgressAge(IJob job, out double seconds)
    {
        if (job is IDrydockPipelineJob pipeline)
        {
            seconds = pipeline.SecondsSinceProgress;
            return true;
        }

        seconds = 0;
        return false;
    }

    /// <summary>Whether a staging map's owner is still running. An id of zero never is.</summary>
    private bool IsJobLive(int jobId)
    {
        return jobId != 0
               && _liveJobs.TryGetValue(jobId, out var entry)
               && entry.Job.Status != JobStatus.Finished;
    }

    // --- staging maps -----------------------------------------------------------------

    /// <summary>
    /// Creates a private map and tags it. Whether the map is map-initialised is the caller's call
    /// rather than something derived from the kind, because the three kinds need different answers:
    /// a store's freeze map takes a post-mapinit grid and so must be initialised itself, while the
    /// validation scratch map is deliberately pre-init so that nothing on it ever ticks.
    ///
    /// <para>The pause happens while the map is still empty, and that ordering is the whole trick.
    /// The engine's own set-paused recurses over every descendant, so pausing a map that already
    /// carried a 960-entity ship would walk all 960 inside one un-yieldable call. Paused first, it
    /// walks one.</para>
    /// </summary>
    internal EntityUid CreateStagingMap(int jobId, DrydockStagingKind kind, Guid shipId, bool mapInit)
    {
        var mapUid = _maps.CreateMap(out _, runMapInit: mapInit);
        _maps.SetPaused(mapUid, true);

        // Named for whoever finds one of these in the entity list at three in the morning.
        _meta.SetEntityName(mapUid, $"drydock staging ({kind})");

        TagStagingMap(mapUid, jobId, kind, shipId);
        return mapUid;
    }

    /// <summary>Tags a map the engine's map loader created, so the sweep can see it.</summary>
    internal void TagStagingMap(EntityUid mapUid, int jobId, DrydockStagingKind kind, Guid shipId)
    {
        var staging = EnsureComp<DrydockStagingMapComponent>(mapUid);
        staging.Kind = kind;
        staging.ShipId = shipId;
        staging.OwnerJobId = jobId;
    }

    /// <summary>
    /// Deletes the map and everything on it. Refuses, loudly, if the map still carries a grid: the
    /// only grid that can be on a staging map is a player's ship, and this call is not the one that
    /// gets to decide a ship is expendable.
    /// </summary>
    internal void ScrapStagingMap(EntityUid mapUid)
    {
        if (!Exists(mapUid))
            return;

        if (HasGridChild(mapUid))
        {
            Log.Error($"Drydock: refused to scrap staging map {ToPrettyString(mapUid)}, it still carries a grid.");
            return;
        }

        Del(mapUid);
    }

    /// <summary>
    /// Scraps every staging map whose owning job is gone and which has nothing on it. A map that
    /// still carries a grid is re-kinded stranded and logged at Error, never deleted: an orphaned map
    /// with a ship on it is a ship an administrator can still give back, and a deleted one is not.
    ///
    /// <para>A job parked waiting on the database counts as live even after its cancellation is
    /// requested, because it only observes that at its next resume. Its map is skipped here and
    /// caught by the next sweep, or by shutdown.</para>
    /// </summary>
    internal void SweepOrphanStagingMaps()
    {
        // Materialised first: deleting entities inside a live query enumerator is not safe.
        _stagingScratch.Clear();
        var query = AllEntityQuery<DrydockStagingMapComponent>();
        while (query.MoveNext(out var uid, out _))
            _stagingScratch.Add(uid);

        var scrapped = 0;
        var stranded = 0;

        foreach (var uid in _stagingScratch)
        {
            if (!TryComp<DrydockStagingMapComponent>(uid, out var staging))
                continue;

            // Already triaged by an unwind that could not place the ship. Left exactly as it is.
            if (staging.Kind == DrydockStagingKind.Stranded)
                continue;

            if (IsJobLive(staging.OwnerJobId))
                continue;

            if (HasGridChild(uid))
            {
                staging.Kind = DrydockStagingKind.Stranded;
                stranded++;
                Log.Error($"Drydock: staging map {ToPrettyString(uid)} for ship {staging.ShipId} outlived its "
                          + "pipeline with a grid still on it. Left in place for an admin; it is a live ship.");
                continue;
            }

            ScrapStagingMap(uid);
            scrapped++;
        }

        if (scrapped > 0 || stranded > 0)
            Log.Info($"Drydock: staging sweep scrapped {scrapped} empty map(s), stranded {stranded}.");
    }

    private bool HasGridChild(EntityUid mapUid)
    {
        var children = Transform(mapUid).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (HasComp<MapGridComponent>(child))
                return true;
        }

        return false;
    }

    // --- tree helpers -----------------------------------------------------------------

    /// <summary>
    /// Counts a transform tree, root included, without materialising it: order does not matter for a
    /// total, so this walks a stack that only ever holds the frontier rather than building the whole
    /// list the sliced walkers use (see <see cref="DrydockFidelitySystem.GridTreeList"/>). It still
    /// allocates that stack once per call.
    /// </summary>
    internal int CountTree(EntityUid root)
    {
        if (!Exists(root))
            return 0;

        var count = 0;
        var stack = new Stack<EntityUid>();
        stack.Push(root);

        while (stack.TryPop(out var uid))
        {
            count++;
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                stack.Push(child);
        }

        return count;
    }

    // --- freeze / thaw ----------------------------------------------------------------

    /// <summary>
    /// Reparents the grid to the origin of an already-paused staging map, then walks the tree pausing
    /// each entity on the tick budget.
    ///
    /// <para>The reparent is what actually freezes the ship: on the server the engine reads whether
    /// the destination map is paused and applies that to every descendant inside the one call that
    /// changes the map id. The walk afterwards therefore drives the progress bar and back-stops the flag; it
    /// is not the mechanism, which is why an entity that appears between the count and the walk is
    /// still frozen and why setting a flag that is already set costs nothing.</para>
    ///
    /// <para>Breadth-first from the grid, so the grid itself - and with it the grid-level
    /// simulations, atmos above all - is the first thing the walk touches.</para>
    ///
    /// <para>The caller must have undocked first: a serialized dock reloads as a reference to a
    /// partner that is not in the document.</para>
    /// </summary>
    internal async Task FreezeOntoStagingMap(EntityUid gridUid, EntityUid stagingMap, IDrydockSlice slice)
    {
        // Counted before the phase opens, because opening a phase force-suspends and the count is
        // what the progress bar divides by.
        var estimate = CountTree(gridUid);

        // The reparent stays above the phase open. A job's Begin force-suspends unconditionally
        // (DrydockPipelineJob.Begin), the caller's last organics gate is the statement before this
        // call, and the ship is undocked with its airlock still swinging shut, so a suspension here
        // is a tick someone can walk aboard in, and the walk-on would be frozen onto the grid below
        // and written into the document. Past this line the grid is on a private paused map and
        // unreachable, so every suspension from here down is free.
        _xform.SetCoordinates(gridUid, new EntityCoordinates(stagingMap, Vector2.Zero));

        await slice.Begin(DrydockPhase.Freeze, estimate);

        var tree = _fidelity.GridTreeList(gridUid);
        for (var i = 0; i < tree.Count; i++)
        {
            var uid = tree[i];
            if (Exists(uid))
                _meta.SetEntityPaused(uid, true);

            await slice.Step(i);
        }
    }

    // --- departure / return -----------------------------------------------------------

    /// <summary>
    /// The departure cue. A stored ship leaves the same way a jumping one does, because from an
    /// observer's side a real jump's vanish is itself a map reparent: sound, then gone.
    ///
    /// <para>Played at the grid's world coordinates and unattached, deliberately not through the
    /// entity overload plus the shuttle system's grid-audio call. That pairing parents the audio
    /// entity to the grid, and the grid is reparented one line later, which takes the sound out of
    /// every listener's view so nobody hears the departure, parks its timed despawn inside a paused
    /// subtree so it never fires, and adds a global view override that would force the private
    /// staging map onto every client for the length of the store.</para>
    ///
    /// <para>Never the thruster half of a real jump setup. A real jump latches the thrusters north
    /// and clears them when it arrives; this path serializes the ship instead, so it would come back
    /// permanently firing north.</para>
    /// </summary>
    internal void PlayDepartureEffect(EntityUid gridUid)
    {
        if (Transform(gridUid).MapUid is { } mapUid && !TerminatingOrDeleted(mapUid))
            _audio.PlayPvs(DepartureSound, new EntityCoordinates(mapUid, _xform.GetWorldPosition(gridUid)));

        // The helms of anything watching this ship on radar, so it stops being listed the moment it
        // leaves rather than at the end of the store.
        _shuttleConsole.RefreshShuttleConsoles(gridUid);
    }

    /// <summary>
    /// The grid on the far side of any live dock. Read before the undock, which is the only moment it
    /// is still knowable, and remembered so the unwind has a first choice of where to put the ship
    /// back.
    /// </summary>
    internal EntityUid? FindDockedPartnerGrid(EntityUid gridUid)
    {
        if (!Exists(gridUid))
            return null;

        foreach (var dock in _docking.GetDocks(gridUid))
        {
            if (dock.Comp.DockedWith is not { } partner || !Exists(partner))
                continue;

            if (Transform(partner).GridUid is { } partnerGrid && partnerGrid != gridUid)
                return partnerGrid;
        }

        return null;
    }

    /// <summary>
    /// The unwind's return leg, and the only inbound placement call on the store side. The store's
    /// outbound leg is faked - a sound and a reparent - because a real jump is a multi-tick state
    /// machine whose updates run on the paused-skipping enumerator, so freezing mid-jump would stall
    /// it forever. Coming back is the other way round: the outbound leg has no placement problem and
    /// the inbound one is nothing but a placement problem, so the inbound leg uses the real thing.
    ///
    /// <para>The target is re-resolved at call time rather than trusted from the context. Across an
    /// elastic store both the remembered docking partner and the station's own largest grid can die,
    /// and the remembered one is only a preference.</para>
    /// </summary>
    /// <returns>
    /// False when there was nowhere at all to put the ship, which leaves it exactly where it is. This
    /// never deletes the grid.
    /// </returns>
    internal bool ReturnGridToStation(
        EntityUid gridUid,
        EntityUid? rememberedTarget,
        EntityUid stationUid,
        string? priorityDockTag)
    {
        if (!Exists(gridUid) || !TryComp<ShuttleComponent>(gridUid, out var shuttle))
            return false;

        EntityUid? target = null;

        if (rememberedTarget is { } remembered && Exists(remembered) && Transform(remembered).MapUid != null)
            target = remembered;

        // The ship being stored is itself a member of its station's Grids, so the station's largest
        // grid can be the ship.
        if (target == null && ResolveDockTarget(stationUid) is { } largest && largest != gridUid)
            target = largest;

        if (target is not { } dockTarget || !Exists(dockTarget))
            return false;

        // The dock places the ship on the station's own unpaused map, and that map change thaws the
        // whole subtree in the same engine walk, so an unwind that gets this far never needs the
        // per-entity thaw.
        _shuttle.TryFTLDock(gridUid, shuttle, dockTarget, priorityTag: priorityDockTag);

        // TryFTLDock returns false both when proximity placed the ship and when its first guard
        // moved nothing (ShuttleSystem.FasterThanLight.cs:1185-1203). The caller decides home vs
        // Stranded on this answer, so read the grid's map rather than the bool.
        var landed = Exists(gridUid) && Exists(dockTarget) && Transform(gridUid).MapUid == Transform(dockTarget).MapUid;

        if (!landed)
        {
            Log.Error($"Drydock: unwind could not return {ToPrettyString(gridUid)} to "
                      + $"{ToPrettyString(dockTarget)}; it is still on its staging map.");
        }

        return landed;
    }
}
