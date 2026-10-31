
using Robust.Shared.Configuration;

namespace Content.Shared._Triad.CCVar;

/// <summary>
/// Configuration variables for Triad features
/// </summary>
[CVarDefs]
public sealed class TriadCCVars
{
    // Triad: tamper protection
    /// <summary>
    /// Tamper check mode for legacy ship-save import. Each imported file's signature is verified
    /// against the keys this server once signed saves with. "off" skips the check and its audit
    /// rows; "notify" lets every file through and writes an audit event naming what it found;
    /// "enforce" refuses files not signed by one of our keys unless the player holds a permit, and
    /// also caps the import list at the account's budget and retires imported files on the
    /// player's disk. Unrecognised values fall back to "notify".
    /// </summary>
    public static readonly CVarDef<string> TamperMode =
        CVarDef.Create("triad.tamper_mode", "notify", CVar.SERVERONLY);

    // Triad: radiator overhaul
    /// <summary>
    /// Whether radiators pushed into the top thermal bucket (white-hot) slowly
    /// take structural damage until they rupture. Off by default: the glow ramp
    /// and the contact burn already telegraph an overloaded fin, so losing the
    /// hardware on top of that is punishment rather than feedback. Turn it on
    /// to force players to spread load across an array.
    /// </summary>
    public static readonly CVarDef<bool> RadiatorOverheatDamage =
        CVarDef.Create("triad.radiator_overheat_damage", false, CVar.SERVERONLY);

    public static readonly CVarDef<bool> UseNightVisionColor =
        CVarDef.Create("triad.use_night_vision_color", false, CVar.CLIENTONLY | CVar.ARCHIVE, "If a custom night vision color should be used instead of the default.");

    public static readonly CVarDef<string> NightVisionColor =
        CVarDef.Create("triad.night_vision_color", "#00FF00", CVar.CLIENTONLY | CVar.ARCHIVE, "The tint/phosphor color of night vision.");

    // Triad: atmos
    /// <summary>
    /// Whether atmos input devices (scrubbers, siphoning vents, passive vents, intakes) may pull gas
    /// out of a map's own atmosphere. Off by default, which limits them to the sector map and ships
    /// in FTL, so expedition planets can no longer be drained for free gas.
    /// </summary>
    public static readonly CVarDef<bool> AllowMapGasExtraction =
        CVarDef.Create("triad.atmos.allow_map_gas_extraction", false, CVar.SERVER | CVar.REPLICATED);

    // Triad: drydock
    /// <summary>
    /// Master switch for the drydock. Off means the console offers neither store nor retrieve, and
    /// the round-end warning, the round-end impound sweep, transfer-offer expiry and the re-bake sweep
    /// do not run. Stored ships are untouched either way.
    /// </summary>
    public static readonly CVarDef<bool> DrydockEnabled =
        CVarDef.Create("triad.drydock.enabled", false, CVar.SERVERONLY);

    /// <summary>
    /// Retrieve is allowed and store is refused, impounds included. The round-end warning and
    /// impound sweep do not run either, transfer offers past their deadline are not expired until it
    /// is lifted, and the re-bake sweep files nothing: it does not start, and one already running
    /// stops before its next ship. This is the switch to
    /// reach for when a build is suspected of writing bad revisions: the deploy pipeline is a daily
    /// cron with no rollback path, so refusing loudly for a day beats filing a day of bad blobs
    /// while still letting people fly the ships they already own.
    /// </summary>
    public static readonly CVarDef<bool> DrydockReadOnly =
        CVarDef.Create("triad.drydock.read_only", false, CVar.SERVERONLY);

    /// <summary>
    /// How many revisions keep their blob. Revision history itself is kept indefinitely; this only
    /// bounds the documents, and the current revision is never pruned regardless of this value.
    /// Zero or less prunes nothing at all, which is the safe direction to misconfigure.
    /// </summary>
    public static readonly CVarDef<int> DrydockKeepBlobs =
        CVarDef.Create("triad.drydock.keep_blobs", 3, CVar.SERVERONLY);

    /// <summary>
    /// The fraction of what was paid for a berth that selling it returns. A grant was paid nothing
    /// for and returns nothing whatever this says.
    /// </summary>
    public static readonly CVarDef<float> DrydockBerthRefund =
        CVarDef.Create("triad.drydock.berth_refund", 0.5f, CVar.SERVERONLY);

    /// <summary>
    /// How long a transfer offer stands before it expires and the ship leaves escrow. The clock is
    /// the persisted deadline on the offer row, so it keeps running through the recipient logging
    /// off and through a server restart, and both sides read the same remaining time from it.
    /// </summary>
    public static readonly CVarDef<int> DrydockTransferOfferSeconds =
        CVarDef.Create("triad.drydock.transfer_offer_seconds", 1800, CVar.SERVERONLY);

    /// <summary>
    /// Whether the drydock offers to import ships saved under the old shipyard save system. This is
    /// onboarding, not a feature: it exists to drain the legacy pool and is meant to be switched off
    /// once it has.
    /// </summary>
    public static readonly CVarDef<bool> DrydockImportEnabled =
        CVarDef.Create("triad.drydock.import_enabled", false, CVar.SERVERONLY);

    /// <summary>
    /// How many legacy ships one account may import, ever. A save file identifies a file and not a
    /// hull - the envelope carries no ship identity - so someone holding three saves of the same
    /// ship holds three legally loadable files. Burning the hash stops a file being imported twice;
    /// this is what stops the other two becoming ships. Counted against imports that were actually
    /// spent, so the rehearsal imports a non-enforcing server allows do not use anyone's budget up.
    /// </summary>
    public static readonly CVarDef<int> DrydockImportBudget =
        CVarDef.Create("triad.drydock.import_budget", 1, CVar.SERVERONLY);

    /// <summary>
    /// Test servers only. On, the same legacy save imports as many times as it is pressed: the
    /// spent-file ledger is neither checked nor written, <see cref="DrydockImportBudget"/> is ignored,
    /// and the player's local file is never retired, whatever the tamper mode. Every press files a
    /// separate hull with its own granted berth. Never on a server whose ships matter: this is the
    /// duplication the ledger exists to stop.
    /// </summary>
    public static readonly CVarDef<bool> DrydockImportUnlimited =
        CVarDef.Create("triad.drydock.import_unlimited", false, CVar.SERVERONLY);

    /// <summary>
    /// How many milliseconds of main-thread time one drydock store or retrieve may spend per tick.
    /// The store is elastic on purpose: making the one captain who pressed the button wait longer is
    /// free, making sixty other players wait is not. Lower is safer for everyone else and slower for
    /// the captain, so lower is the safe direction to misconfigure.
    ///
    /// <para>Zero or less turns slicing off completely, and it means no job at all rather than a job
    /// with a zero budget: the whole pipeline runs on the caller's own async path against the
    /// synchronous slice, exactly as it ran before the slicing change. A zero-budget job queue would
    /// instead never run anything, because the queue tests its own clock before it dequeues. This is
    /// the rollback lever on a pipeline whose deploy has no other one, and it is what the
    /// integration fixtures set.</para>
    ///
    /// <para>This does not bound the worst tick on its own, for two reasons. The job queue tests its
    /// clock before each dequeue and re-runs a suspended job, so a run admitted just inside the
    /// budget carries the tick past it, and a phase boundary that suspends before the budget is
    /// spent leaves room for another run in the same tick. Four engine calls also cannot be
    /// interrupted from content - the grid serialize, the round-trip validation load, the retrieve's
    /// grid load, and the dock - so the worst tick is this budget plus the longest of those. The
    /// queue's own budget follows this cvar rather than a constant, so lower is both the safe
    /// direction and a direction that moves.</para>
    /// </summary>
    public static readonly CVarDef<int> DrydockTickBudgetMs =
        CVarDef.Create("triad.drydock.tick_budget_ms", 2, CVar.SERVERONLY);

    /// <summary>
    /// How many entities a sliced drydock loop processes between stopwatch reads. Higher spends less
    /// time reading the clock and overshoots the budget by more; lower is the safe direction to
    /// misconfigure.
    /// </summary>
    public static readonly CVarDef<int> DrydockSliceStride =
        CVarDef.Create("triad.drydock.slice_stride", 32, CVar.SERVERONLY);

    /// <summary>
    /// How many real seconds a drydock job may go without advancing before it is cancelled and
    /// unwound. This is the release-mode net under the one rule a sliced pipeline cannot enforce at
    /// compile time, that every await goes through the slice: a missed wrapper leaves the job with no
    /// resume handle, which only asserts in debug and hangs forever in release, stranding a frozen
    /// ship on a private map. Raise it if a slow database makes it fire spuriously; zero disables it,
    /// which is the unsafe direction.
    /// </summary>
    public static readonly CVarDef<int> DrydockSliceWatchdogSeconds =
        CVarDef.Create("triad.drydock.slice_watchdog_seconds", 120, CVar.SERVERONLY);

    /// <summary>
    /// Whether the store drives the engine's serializer one entity at a time against the tick budget
    /// instead of calling <c>TrySaveGrid</c>, which serializes the whole ship in a single
    /// un-interruptible call.
    ///
    /// <para>That one call is the store's entire tick spike: it was measured at 807 ms warm and
    /// 3275 ms on a fresh import of the same hull, and the pipeline's worst slice equals it to a
    /// tenth of a millisecond. Every piece needed to drive the loop from content is public on
    /// <c>EntitySerializer</c>, so this costs no engine divergence; what it gives up is the engine's
    /// own wrapper, and with it the tile-map reuse that only exists to keep map file diffs small.
    /// A drydock document is an opaque blob in Postgres that nothing ever diffs.</para>
    ///
    /// <para>Off is the engine path, which is the rollback if the sliced walk and the batch call
    /// ever disagree about a document.</para>
    /// </summary>
    public static readonly CVarDef<bool> DrydockSlicedSerialize =
        CVarDef.Create("triad.drydock.sliced_serialize", false, CVar.SERVERONLY);

    /// <summary>
    /// With <see cref="DrydockSlicedSerialize"/> on, also run the engine's own whole-grid serialize
    /// over the same ship in the same tick and log every difference between the two documents.
    ///
    /// <para>The fidelity guard for the sliced walk. Both documents come from one grid at one
    /// instant, so a difference is the walk's and cannot be content nondeterminism: two separately
    /// loaded copies of the same hull disagree by tens of entities all on their own, which is why
    /// an A/B across two loads cannot answer this question and this can.</para>
    ///
    /// <para>Doubles what a store costs while it is on. For a soak on a test server and for the
    /// roster sweep, never for production.</para>
    /// </summary>
    public static readonly CVarDef<bool> DrydockSerializeShadowCompare =
        CVarDef.Create("triad.drydock.serialize_shadow_compare", false, CVar.SERVERONLY);

    /// <summary>
    /// What a retrieve does with <c>MapInitEvent</c>, which the engine never raises for a restored
    /// entity: <c>off</c> leaves it unraised, <c>report</c> raises it on every entity of the ship and
    /// logs every persisted field it rewrote and every entity it spawned or deleted, <c>revert</c>
    /// raises it and puts every rewritten persisted field back and deletes every spawned entity, so
    /// the ship keeps what its document said and gains what map init builds at runtime.
    ///
    /// <para><c>report</c> lands the damage on the ship; it is the soak mode for a test server.
    /// <c>off</c> is the rollback: the retrieve's named sweeps then redo the handful of map-init
    /// jobs they know about and nothing else.</para>
    ///
    /// <para>A legacy import reads the same knob before it files the hull: <c>revert</c> there puts
    /// persisted fields back but keeps every entity map init spawned, since an old save never
    /// carried its fills (door electronics among them).</para>
    /// </summary>
    public static readonly CVarDef<string> DrydockMapInitRefire =
        CVarDef.Create("triad.drydock.mapinit_refire", "revert", CVar.SERVERONLY);

    /// <summary>
    /// The share of a hull's appraisal the round-end sweep charges to reclaim a ship it impounded,
    /// 0 to 1. Frozen onto the row at impound, so changing this never moves a fee already quoted.
    /// The admin impound dialog opens at this rate too, so an impound by hand charges what the sweep
    /// would have unless the admin moves the slider.
    /// </summary>
    public static readonly CVarDef<float> DrydockImpoundRoundEndFraction =
        CVarDef.Create("triad.drydock.impound_round_end_fraction", 0.5f, CVar.SERVERONLY);

    /// <summary>
    /// How many seconds the round-end sweep may hold the restart while it is still filing hulls.
    /// The sweep starts when the round ends and mostly drains inside game.round_restart_time; this
    /// is the ceiling past that, because twenty capital hulls at a few seconds each fit inside the
    /// window until the night they do not, and a restart that runs ahead of the sweep deletes the
    /// hulls it had not reached. Past the ceiling the restart goes ahead and every hull not yet
    /// filed stays checked out with a timeline row saying so. Zero holds nothing.
    /// </summary>
    public static readonly CVarDef<int> DrydockImpoundRestartCeilingSeconds =
        CVarDef.Create("triad.drydock.impound_restart_ceiling_seconds", 180, CVar.SERVERONLY);

    /// <summary>
    /// Whether the tier 1 re-bake sweep runs: every stored ship's current document is read, the
    /// migration mappings are baked into it, and a changed one is filed as a new system revision
    /// with the old one kept. On by default because <see cref="DrydockEnabled"/> already gates it
    /// and <see cref="DrydockReadOnly"/> pauses it; this is the switch for the sweep alone.
    ///
    /// <para>The sweep is scheduled once per server run, a minute after startup, and only if this
    /// and the drydock switches allow it at startup; the <c>drydockrebake</c> admin command starts
    /// one on demand. Turning this off stops a running sweep before its next ship.</para>
    /// </summary>
    public static readonly CVarDef<bool> DrydockRebakeEnabled =
        CVarDef.Create("triad.drydock.rebake_enabled", true, CVar.SERVERONLY);

    /// <summary>
    /// The re-bake sweep's pace, in ships per minute. Each ship's decompress, transform and compress
    /// runs on a worker thread; this bounds how often the sweep reads a document and writes a
    /// revision, so a large fleet drains over minutes instead of hammering the database at boot.
    /// Zero or less runs nothing, which is the safe direction to misconfigure.
    /// </summary>
    public static readonly CVarDef<int> DrydockRebakeShipsPerMinute =
        CVarDef.Create("triad.drydock.rebake_ships_per_minute", 30, CVar.SERVERONLY);
    // End Triad
    // Triad: market data
    // The queue knobs mirror the admin log ones, which solve the same problem at production volume
    // on this server: adminlogs.queue_send_delay_seconds, queue_max, pre_round_queue_max and
    // drop_threshold. Defaults are deliberately identical so the two behave alike under load.

    /// <summary>
    /// Master switch. Off means nothing is recorded and no database work happens at all. On its own
    /// this does not create the tables; the migration does that whether or not this is set.
    /// </summary>
    public static readonly CVarDef<bool> MarketDataEnabled =
        CVarDef.Create("triad.market.enabled", false, CVar.SERVERONLY);

    /// <summary>
    /// Whether to record per-item line rows as well as transaction headers. Lines are the entire
    /// input to the price rollup, so turning this off leaves a Grafana-only feature with no in-game
    /// consumer. Separate from the master switch because line capture is the expensive half and
    /// wants to be disableable on its own if a sale on a full pallet ever costs real frame time.
    /// </summary>
    public static readonly CVarDef<bool> MarketDataLinesEnabled =
        CVarDef.Create("triad.market.lines_enabled", true, CVar.SERVERONLY);

    /// <summary>
    /// How long the writer waits between flushes.
    /// </summary>
    public static readonly CVarDef<float> MarketDataQueueSendDelay =
        CVarDef.Create("triad.market.queue_send_delay_seconds", 5f, CVar.SERVERONLY);

    /// <summary>
    /// Queue depth that forces a flush before the delay elapses.
    /// </summary>
    public static readonly CVarDef<int> MarketDataQueueMax =
        CVarDef.Create("triad.market.queue_max", 5000, CVar.SERVERONLY);

    /// <summary>
    /// Cap on the separate pre-round queue. Rows can be created before a round has an id, so they
    /// are held and stamped once it exists; character spawn sits exactly on that boundary.
    /// </summary>
    public static readonly CVarDef<int> MarketDataPreRoundQueueMax =
        CVarDef.Create("triad.market.pre_round_queue_max", 5000, CVar.SERVERONLY);

    /// <summary>
    /// Depth past which rows are dropped rather than queued. Dropping is the correct behaviour for
    /// telemetry: the tick must never stall for it. Drops are counted and logged, never silent.
    /// </summary>
    public static readonly CVarDef<int> MarketDataDropThreshold =
        CVarDef.Create("triad.market.drop_threshold", 20000, CVar.SERVERONLY);

    /// <summary>
    /// How long raw transactions, splits and lines are kept. The daily price rollup is permanent and
    /// unaffected. Zero or less disables the purge entirely and keeps everything.
    /// </summary>
    public static readonly CVarDef<int> MarketDataRetentionDays =
        CVarDef.Create("triad.market.retention_days", 90, CVar.SERVERONLY);

    /// <summary>
    /// Max limit of a person could have for safety boxes.
    /// </summary>
    public static readonly CVarDef<int> SafetyBoxLimit =
        CVarDef.Create("triad.safety_box_limit", 5, CVar.REPLICATED);

    /// <summary>
    /// How many minutes the round-end countdown runs when nothing supplies its own length, which is
    /// the auto call. A call from a comms console still takes the alert level's shuttle time.
    /// </summary>
    public static readonly CVarDef<int> RoundEndCountdownMinutes =
        CVarDef.Create("triad.round_end.countdown_minutes", 10, CVar.SERVERONLY);

    /// <summary>
    /// When the lobby is on, start its round countdown at boot and after every restart even with
    /// nobody connected, so the round starts and sector simulations start. Players who arrive still
    /// get sent to the lobby can late-join. False keeps the wait for the first player.
    /// </summary>
    public static readonly CVarDef<bool> LobbyStartWithoutPlayers =
        CVarDef.Create("triad.lobby.start_without_players", false, CVar.SERVERONLY);
}
