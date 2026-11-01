using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Shared._NF.Bank;
using Content.Shared._Triad.Drydock;
using Robust.Shared.GameObjects;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// Every phase of a store or a retrieve. The values are also the <see cref="DrydockPhaseTimer"/>
/// mark names, so the phase a progress bar is showing and the phase a timing line attributes a
/// hitch to are the same word.
/// </summary>
public enum DrydockPhase : byte
{
    // store
    Gate, Freeze, Purge, Appraise, Sidecars, Strip, Capture, Prepare,
    Serialize, Validate, Hash, Drift, Manifest, Compress, Commit, Despawn,
    // retrieve
    Fetch, Load, Fidelity, Sweeps, Damage, Station, Dock, Release,
    // both
    Unwind,
    // retrieve, between Fidelity and Sweeps; declared last so no earlier value moves
    MapInit,
}

/// <summary>
/// The static shape of the two pipelines: which phases they run, in what order, and roughly what
/// each costs. Nothing here is state, so both the jobs and the synchronous fallback read it.
/// </summary>
public static class DrydockPhases
{
    private static readonly DrydockPhase[] StorePhases =
    {
        DrydockPhase.Gate,
        DrydockPhase.Freeze,
        DrydockPhase.Purge,
        DrydockPhase.Appraise,
        DrydockPhase.Sidecars,
        DrydockPhase.Strip,
        DrydockPhase.Capture,
        DrydockPhase.Prepare,
        DrydockPhase.Serialize,
        DrydockPhase.Validate,
        DrydockPhase.Hash,
        DrydockPhase.Drift,
        DrydockPhase.Manifest,
        DrydockPhase.Compress,
        DrydockPhase.Commit,
        DrydockPhase.Despawn,
    };

    private static readonly DrydockPhase[] RetrievePhases =
    {
        DrydockPhase.Fetch,
        DrydockPhase.Load,
        DrydockPhase.Fidelity,
        DrydockPhase.MapInit,
        DrydockPhase.Sweeps,
        DrydockPhase.Damage,
        DrydockPhase.Station,
        DrydockPhase.Dock,
        DrydockPhase.Release,
    };

    /// <summary>
    /// Relative main-thread cost, seeding the percentage. Measured on one capital hull, so it is a
    /// shape and not a law: a 144-entity hull and a 960-entity hull do not share the split, and the
    /// bar's apparent rate will differ between them. Serialize and Validate carry the two largest
    /// weights because they are the two atomic bulk calls of the store, and Load and Dock are the
    /// same for the retrieve.
    ///
    /// <para>The numbers do not have to sum to anything: <see cref="DrydockProgress"/> normalises
    /// against the total of whichever roster it was handed, so a phase a pipeline skips costs the
    /// bar nothing.</para>
    /// </summary>
    public static int Weight(DrydockPhase phase)
    {
        return phase switch
        {
            // store. The 2026-09 measurement of a 2.1s store: validate 43%, serialize 37%,
            // drift ~5% after the optimisation that shipped, sidecars 6%, capture 2%.
            DrydockPhase.Gate => 2,
            DrydockPhase.Freeze => 1,
            DrydockPhase.Purge => 1,
            DrydockPhase.Appraise => 1,
            DrydockPhase.Sidecars => 6,
            DrydockPhase.Strip => 1,
            DrydockPhase.Capture => 2,
            DrydockPhase.Prepare => 1,
            DrydockPhase.Serialize => 37,
            DrydockPhase.Validate => 43,
            DrydockPhase.Hash => 1,
            DrydockPhase.Drift => 5,
            DrydockPhase.Manifest => 1,
            DrydockPhase.Compress => 3,
            DrydockPhase.Commit => 3,
            DrydockPhase.Despawn => 1,

            // retrieve. Never measured against a profiler the way the store was, so these are the
            // pipeline's own shape - one bulk load, then a long revive epilogue, then one dock.
            DrydockPhase.Fetch => 5,
            DrydockPhase.Load => 40,
            DrydockPhase.Fidelity => 15,
            // Two renders of every persisted field on the hull, so about the fidelity restore twice.
            DrydockPhase.MapInit => 25,
            DrydockPhase.Sweeps => 20,
            DrydockPhase.Damage => 5,
            DrydockPhase.Station => 3,
            DrydockPhase.Dock => 10,
            DrydockPhase.Release => 2,

            // Never part of a roster, so its weight never reaches the normaliser. It exists so the
            // phase name a cancelled pipeline reports is honest.
            DrydockPhase.Unwind => 1,

            _ => 1,
        };
    }

    public static IReadOnlyList<DrydockPhase> Store => StorePhases;

    public static IReadOnlyList<DrydockPhase> Retrieve => RetrievePhases;

    /// <summary>
    /// The <see cref="DrydockPhaseTimer"/> mark string: the lowercase invariant of the enum name.
    /// Freeze, Purge, Strip and Unwind are new marks that did not exist before the slicing change,
    /// so the flat <c>phase=Nms</c> line Loki pattern-matches gains four keys.
    /// </summary>
    public static string Mark(DrydockPhase phase)
    {
        return phase.ToString().ToLowerInvariant();
    }
}

/// <summary>
/// Fired when the whole integer percent changes and on every phase entry, so an atomic phase still
/// counts as a sign of life for the client's liveness timer.
///
/// <para>Runs on the main thread, synchronously, from inside the job's Run inside
/// <c>DrydockSystem.Update</c>. It may touch ECS and send a BUI message. It must not block, must not
/// await, and must not throw: the pipeline does not catch for it, so a throw here travels up through
/// the job's process wrapper and is logged as a pipeline failure.</para>
/// </summary>
public delegate void DrydockProgressCallback(int percent, DrydockPhase phase);

/// <summary>
/// An honest percentage for an elastic operation. Total work is known up front - the roster of
/// phases, weighted, and an item count per phase - which is what makes an unbounded duration
/// acceptable to the player who pressed the button.
///
/// <para>Monotonic by construction. The retrieve's revision-fallback loop re-enters Fetch and Load
/// on a document that would not load, and a bar that jumped backwards there would read as a failure
/// rather than as a retry.</para>
/// </summary>
public sealed class DrydockProgress
{
    private readonly IReadOnlyList<DrydockPhase> _phases;
    private readonly DrydockProgressCallback? _callback;
    private readonly int _totalWeight;

    /// <summary>Position in <see cref="_phases"/> of the open phase, or -1 before the first one.</summary>
    private int _index = -1;

    /// <summary>Items in the open phase. Zero marks it atomic, so the bar rests on its entry value.</summary>
    private int _items;

    private int _item;

    public DrydockProgress(IReadOnlyList<DrydockPhase> phases, DrydockProgressCallback? callback)
    {
        _phases = phases;
        _callback = callback;

        var total = 0;
        for (var i = 0; i < phases.Count; i++)
            total += DrydockPhases.Weight(phases[i]);

        // Never zero: an empty roster would divide by it, and a roster of zero-weight phases is a
        // caller error we would rather render as a stuck bar than crash a store over.
        _totalWeight = Math.Max(1, total);
    }

    public int Percent { get; private set; }

    public DrydockPhase Phase { get; private set; }

    public bool Complete { get; private set; }

    /// <summary>
    /// Opens a phase and always fires the callback. <paramref name="items"/> of zero or less marks
    /// the phase atomic.
    ///
    /// <para>Re-opening a phase already passed is legal and defined: the percentage is clamped to
    /// its previous value and never goes backwards, the phase name updates, and the callback
    /// fires.</para>
    /// </summary>
    public void BeginPhase(DrydockPhase phase, int items)
    {
        if (Complete)
            return;

        // A phase outside the roster - the unwind, above all - still names itself and still counts
        // as a sign of life, but it must not move the bar: it has no place in the total, so its
        // items would otherwise creep through whichever phase's weight band was open last. Holding
        // its item count at zero is what makes it genuinely inert.
        var index = IndexOf(phase);
        _items = index < 0 ? 0 : Math.Max(0, items);
        if (index >= 0)
            _index = index;

        _item = 0;
        Phase = phase;

        Recompute();
        _callback?.Invoke(Percent, Phase);
    }

    /// <summary>Zero-based item index inside the current phase. Clamped to the phase's item count.</summary>
    public void Advance(int index)
    {
        if (Complete || _items <= 0)
            return;

        _item = Math.Clamp(index, 0, _items);

        var before = Percent;
        Recompute();

        // Only the whole integer percent is worth a message. A 960-entity hull would otherwise push
        // one BUI message per entity, which costs more than the work it reports on.
        if (Percent != before)
            _callback?.Invoke(Percent, Phase);
    }

    /// <summary>Drives <see cref="Percent"/> to 100 and fires once more. Idempotent.</summary>
    public void Finish()
    {
        if (Complete)
            return;

        Complete = true;
        Percent = 100;
        _callback?.Invoke(Percent, Phase);
    }

    private int IndexOf(DrydockPhase phase)
    {
        for (var i = 0; i < _phases.Count; i++)
        {
            if (_phases[i] == phase)
                return i;
        }

        return -1;
    }

    private void Recompute()
    {
        if (_index < 0)
            return;

        var before = 0;
        for (var i = 0; i < _index; i++)
            before += DrydockPhases.Weight(_phases[i]);

        var weight = DrydockPhases.Weight(_phases[_index]);
        var fraction = _items > 0 ? _item / (double)_items : 0d;
        var raw = (int)((before + weight * fraction) * 100d / _totalWeight);

        // Capped at 99 short of Finish, so reaching 100 means the pipeline actually returned rather
        // than the last phase happening to round up.
        Percent = Math.Clamp(Math.Max(raw, Percent), 0, 99);
    }
}

/// <summary>
/// What one phase cost the main thread, in ticks rather than in runs.
///
/// <para><see cref="WorstTickMs"/> is the figure the slicing design is judged on: the most
/// main-thread time this phase took away from a single tick. It is deliberately not "the longest
/// run", because <c>JobQueue.Process</c> re-runs a suspended job while its own clock has
/// room and <c>Begin</c> force-suspends whether or not the budget is spent, so one tick routinely
/// holds several runs. A per-run maximum would read well under the budget while the tick actually
/// paid more, and it would do it worst in exactly the sliced phases the design exists to
/// improve.</para>
/// </summary>
public readonly record struct DrydockPhaseCost(double WorstTickMs, double TotalMs, int Ticks);

/// <summary>
/// Buckets main-thread spans by tick and by phase, so a phase's cost is what a bystander felt in
/// one tick rather than what one resumption happened to take.
///
/// <para>The caller owns the clock and the phase, because the two pipelines learn them differently:
/// a job reads the stopwatch the engine restarts at the top of every run, and knows the phase that
/// was open when that run started.</para>
/// </summary>
public sealed class DrydockSpanMeter
{
    private readonly Dictionary<DrydockPhase, double> _thisTick = new();
    private readonly Dictionary<DrydockPhase, DrydockPhaseCost> _costs = new();
    private uint _tick;
    private double _thisTickTotal;
    private bool _open;

    /// <summary>The worst single tick the pipeline cost, across every phase that shared it.</summary>
    public double WorstTickMs { get; private set; }

    public IReadOnlyDictionary<DrydockPhase, DrydockPhaseCost> Costs => _costs;

    /// <summary>Credits one main-thread span to the phase that owned it.</summary>
    public void Add(uint tick, DrydockPhase? phase, double ms)
    {
        if (_open && tick != _tick)
            Flush();

        _tick = tick;
        _open = true;
        _thisTickTotal += ms;

        // A span with no phase is the job's own prologue, before the first phase opened. It counts
        // toward the tick and against no phase, which is why the rows do not have to sum to the
        // headline.
        if (phase is { } p)
            _thisTick[p] = _thisTick.GetValueOrDefault(p) + ms;
    }

    /// <summary>Closes the tick being accumulated. Idempotent, so the pipeline's exit can just call it.</summary>
    public void Flush()
    {
        if (!_open)
            return;

        foreach (var (phase, ms) in _thisTick)
        {
            var prev = _costs.GetValueOrDefault(phase);
            _costs[phase] = new DrydockPhaseCost(Math.Max(prev.WorstTickMs, ms), prev.TotalMs + ms, prev.Ticks + 1);
        }

        WorstTickMs = Math.Max(WorstTickMs, _thisTickTotal);

        _thisTick.Clear();
        _thisTickTotal = 0;
        _open = false;
    }
}

/// <summary>
/// The tick-budget handle every pipeline is written against. Two implementations: the two jobs,
/// which really suspend, and <see cref="DrydockSyncSlice"/>, which never does.
///
/// <para>Inside a job pipeline the only legal awaits are <see cref="Begin"/>, <see cref="Step"/> and
/// <see cref="Await{T}"/>. A bare await leaves the job with no resume handle, which asserts in debug
/// and hangs the job forever in release, stranding a frozen ship on a private map.</para>
/// </summary>
public interface IDrydockSlice
{
    CancellationToken Cancellation { get; }

    DrydockProgress Progress { get; }

    /// <summary>
    /// Opens a phase and force-suspends, so one phase's overrun never lands on the next phase's
    /// start. <see cref="DrydockSyncSlice"/> never suspends, so there it only opens the phase.
    /// </summary>
    Task Begin(DrydockPhase phase, int items);

    /// <summary>Advances progress and suspends if the tick budget is spent, checked every stride items.</summary>
    Task Step(int index);

    /// <summary>The only legal way to await anything else from inside a pipeline.</summary>
    Task<T> Await<T>(Task<T> task);

    Task Await(Task task);
}

/// <summary>
/// Runs a pipeline to completion with no suspension, on the caller's own async path. This is what
/// <c>triad.drydock.tick_budget_ms &lt;= 0</c> selects, and it is the rollback lever: no job, no
/// queue, no queue latency, and control flow byte-identical to the pipeline as it ran before
/// slicing.
/// </summary>
/// <remarks>
/// Must not be used inside a job: its <see cref="Await{T}"/> is a bare await, which is exactly the
/// violation that leaves a job with no resume handle. The store's unwind does not use it either -
/// the unwind is plain synchronous code, because an unwind that honours cancellation cannot finish.
/// </remarks>
public sealed class DrydockSyncSlice : IDrydockSlice
{
    public DrydockSyncSlice(IReadOnlyList<DrydockPhase> phases, DrydockProgressCallback? callback = null)
    {
        Progress = new DrydockProgress(phases, callback);
    }

    public CancellationToken Cancellation => CancellationToken.None;

    public DrydockProgress Progress { get; }

    public Task Begin(DrydockPhase phase, int items)
    {
        Progress.BeginPhase(phase, items);
        return Task.CompletedTask;
    }

    public Task Step(int index)
    {
        Progress.Advance(index);
        return Task.CompletedTask;
    }

    public Task<T> Await<T>(Task<T> task) => task;

    public Task Await(Task task) => task;
}

/// <summary>
/// Thrown by a resume guard when the world moved under a parked pipeline: the grid died, the staging
/// map went, the round ended. Caught by the pipeline itself and turned into a normal outcome; it must
/// never escape into the job's process wrapper, which logs any escaping exception at Error and would
/// fail every pooled integration pair that ran a cancellation.
/// </summary>
public sealed class DrydockAbortedException : Exception
{
    public DrydockAbortedException(string reason, bool gridGone = false)
        : base($"Drydock pipeline aborted: {reason}")
    {
        Reason = reason;
        GridGone = gridGone;
    }

    /// <summary>Human-readable, for the log line. Never branched on; that is what <see cref="GridGone"/> is for.</summary>
    public string Reason { get; }

    /// <summary>
    /// True when the abort is the store's grid having been deleted, which the store reports as a
    /// failed serialize rather than a cancellation. Set only by the guard that detects it.
    /// </summary>
    public bool GridGone { get; }
}

public sealed record DrydockStoreOutcome(DrydockStoreResult Result, Guid? ShipId);

public sealed record DrydockRetrieveOutcome(DrydockRetrieve Retrieve);

/// <summary>
/// Per-store mutable state. Constructed by <c>TryStoreShip</c>, handed to the job, filled by
/// <c>RunStorePipeline</c>, read by the unwind and by the wrapper's finally after a cancellation.
///
/// <para>Every list here is an undo ledger and every one of them is appended to <em>before</em> the
/// mutation it records. A phase that built a local ledger and returned it at the end would leave a
/// ship blanked with no record of what was taken off it if the pipeline aborted mid-phase, which
/// across ticks is no longer a theoretical window.</para>
/// </summary>
public sealed class DrydockStoreContext
{
    public EntityUid GridUid;
    public Guid OwnerUserId;
    public int? RoundId;
    public int? BerthId;

    /// <summary>
    /// The station the console resolved. The unwind needs somewhere to put the ship back and the
    /// docking partner can be gone by then. Passed by the caller and never re-derived from the grid,
    /// which is on a private map by the time the unwind runs.
    /// </summary>
    public EntityUid StationUid;

    /// <summary>
    /// The map the hull was on, and where on it, when the store began. Read before anything moves
    /// the grid, because the freeze reparents it onto a private map and the ordinary transform
    /// then answers "the staging map" for every question about where the ship came from. An
    /// impound's eviction puts occupants down here and nowhere else: a spawn point on another map
    /// is at best a different sector and at worst a hull mid-store, where a body dropped on it is
    /// written into somebody else's document.
    /// </summary>
    public EntityUid? HomeMap;

    public Vector2 HomePosition;

    public Guid ShipId;
    public EntityUid? StagingMap;

    /// <summary>The grid the ship was docked to when the store began. First choice for the unwind.</summary>
    public EntityUid? ReturnTarget;

    public string? ReturnDockTag;

    public bool Committed;
    public bool Frozen;
    public bool Undocked;

    public readonly List<EntityUid> InjectedGas = new();
    public readonly List<EntityUid> InjectedDamage = new();
    public readonly List<EntityUid> InjectedAppearance = new();
    public readonly List<IComponent> Stripped = new();
    public readonly List<(EntityUid Store, EntityUid? Map)> StoreMaps = new();

    /// <summary>
    /// Created by the pipeline and assigned here before the first cleared member, not returned at
    /// the end. The sliced capture fills it in place, so an abort part-way through the walk still
    /// has a record of every field already cleared.
    /// </summary>
    public DrydockFidelityCapture? Fidelity;

    public EntityUid? DeedHolder;
    public bool DeedDetached;

    /// <summary>
    /// Null on an ordinary store. Set means the hull is being taken rather than put away, which
    /// bends exactly three gates: the berth checks do not apply because the lot is not a
    /// berth, hazards are destroyed instead of refused for, and anyone found aboard is moved off
    /// instead of refusing. <see cref="DrydockStoreResult.SerializeFailed"/> and
    /// <see cref="DrydockStoreResult.ValidationFailed"/> are never forced: a document that will not
    /// write or will not read back is the one thing an impound cannot paper over.
    /// </summary>
    public DrydockImpound? Impound;

    /// <summary>
    /// Set by the round-end sweep: the pipeline runs on the caller's own async path with no job and
    /// the engine's own serializer, whatever the two cvars say. Slicing exists to protect bystanders
    /// from tick hitches and costs roughly twice the wall time to do it; at round end there are no
    /// bystanders left and the restart is waiting.
    /// </summary>
    public bool Inline;

    /// <summary>
    /// The mind of whoever is putting the ship away, or null when nobody is. Read by the purge: a
    /// permitted item travels only when its permit was issued to this mind, the ship-save path's rule,
    /// so a player cannot store someone else's permitted kit aboard their own hull. Null means an
    /// impound or the round-end sweep, and permits are judged against <see cref="OwnerUserId"/>
    /// instead. The drydock's verbs check the account and never the mind; this is not a verb check
    /// but a permit one, and a permit is issued to a character.
    /// </summary>
    public EntityUid? PermitHolderMind;

    /// <summary>
    /// How many occupants the impound moved off, summed across all three gates because somebody can
    /// board between them. Copied onto <see cref="DrydockRevisionRequest.Evicted"/> when the hull
    /// is filed, and from there into the audit reason, so the timeline says a hull was taken with
    /// people on it rather than leaving an admin to infer it.
    /// </summary>
    public int Evicted;

    public readonly DrydockPhaseTimer Timer = new();
}

/// <summary>
/// What an impound charges and why, and who took it: carried into the store pipeline and written
/// onto the ship row when the hull is filed. Frozen here rather than recomputed at redemption,
/// because a debt already quoted to a player must not move under them.
/// </summary>
/// <param name="FeePercent">
/// Share of the appraisal owed, 0 to 100, clamped on construction. Carried as a percent rather
/// than credits so a fee above the hull's worth cannot be expressed at all: the credits are
/// computed where the appraisal is known, by <see cref="FeeAgainst"/>.
/// </param>
/// <param name="Reason">Shown to the owner. Null when nothing was given.</param>
/// <param name="Redeemable">
/// Whether the owner may act on it at all. Not inferable from <paramref name="FeePercent"/>: a
/// courtesy impound is free and redeemable, an adjudication is frozen at any price.
/// </param>
/// <param name="ActorUserId">
/// Who took it: the admin, or null for the round-end sweep. Written as the actor of the impound's
/// revision and of its audit row, where the owner would otherwise be named for a taking they never
/// asked for.
/// </param>
public sealed record DrydockImpound(int FeePercent, string? Reason, bool Redeemable, Guid? ActorUserId)
{
    public int FeePercent { get; init; } = DrydockImpoundFee.ClampPercent(FeePercent);

    /// <summary>Credits owed against a given appraisal, rounded down. Never more than the appraisal.</summary>
    public int FeeAgainst(int appraisal) => DrydockImpoundFee.Against(appraisal, FeePercent);

    /// <summary>
    /// The timeline row's text: the reason given, the fee with the share and appraisal it was taken
    /// against, whether the owner may reclaim it, and how many occupants were moved off. The ship
    /// row keeps only the latest impound's terms, so this is where an earlier impound's numbers
    /// survive being overwritten.
    /// </summary>
    public string AuditReason(int fee, int appraisal, int evicted)
    {
        var parts = new List<string>(4);

        if (!string.IsNullOrWhiteSpace(Reason))
            parts.Add(Reason);

        parts.Add($"fee {BankSystemExtensions.ToSpesoString(fee)}, {FeePercent}% of {BankSystemExtensions.ToSpesoString(appraisal)}");
        parts.Add(Redeemable ? "owner can reclaim" : "held for adjudication");

        if (evicted > 0)
            parts.Add($"{evicted} moved off");

        return string.Join(" · ", parts);
    }
}

/// <summary>
/// Per-retrieve mutable state. Constructed by <c>TryRetrieveShip</c>, handed to the job, filled by
/// <c>RunRetrievePipeline</c>.
///
/// <para><see cref="ClaimHeld"/> is read by the wrapper's finally, outside the job, which is what
/// makes the database claim release survive a cancellation: the job's own body may never run
/// again.</para>
/// </summary>
public sealed class DrydockRetrieveContext
{
    public Guid ShipId;
    public Guid OwnerUserId;
    public EntityUid StationUid;
    public int? RoundId;

    public EntityUid? StagingMap;
    public EntityUid? Grid;
    public int EntityCount;

    /// <summary>True from the moment the database claim lands until the ship is really docked.</summary>
    public bool ClaimHeld;

    /// <summary>True once the ship is docked; the wrapper then also owns vacating the berth.</summary>
    public bool Presented;

    /// <summary>
    /// The revision that loaded, and the two fidelity restore reports from its revive. Left here for
    /// the wrapper, which writes their skips to the timeline once the ship is presented.
    /// </summary>
    public int? LoadedRevision;

    /// <inheritdoc cref="LoadedRevision"/>
    public DrydockFidelityRestore? CapturedRestore;

    /// <inheritdoc cref="LoadedRevision"/>
    public DrydockFidelityRestore? AppearanceRestore;

    /// <summary>The map-init transaction's report, null when the mode was off.</summary>
    public DrydockMapInitReport? MapInitReport;

    public readonly DrydockPhaseTimer Timer = new();
}
