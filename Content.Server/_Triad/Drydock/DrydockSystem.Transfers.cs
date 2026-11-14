using System;
using System.Threading.Tasks;
using Content.Server.GameTicking;
using Content.Shared._Triad.CCVar;
using Content.Shared.GameTicking;
using Robust.Shared.Prototypes;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The system's one tick surface, and the hooks that hang off it. This is the only
/// <see cref="Initialize"/> and the only <see cref="Update"/> across every partial of
/// <see cref="DrydockSystem"/>, so everything that needs a heartbeat or a round boundary lands here.
///
/// <para>Four things run from it. The sliced store and retrieve jobs, which are the reason a store
/// no longer stalls the server for two seconds. The escrow sweep, whose deadlines are persisted
/// timestamps that keep running while the owner is logged off and across a restart, so a ship past
/// its deadline goes back to Stored and its offer is marked Expired. The round-end sweep's hooks,
/// which live on the sweep partial and are subscribed from here. And the round boundary, which
/// cancels any pipeline still in flight and then sweeps up whatever private maps they left. The
/// re-bake sweep is scheduled from here too, on a timer rather than this tick: see the re-bake partial.</para>
/// </summary>
public sealed partial class DrydockSystem
{
    [Dependency] private GameTicker _ticker = default!;

    /// <summary>
    /// How often the sweep runs. Coarse on purpose: the deadline the players see is thirty minutes,
    /// and the accept path checks the deadline itself, so a sweep that lands late never lets a
    /// stale offer complete. Finer would only add database reads.
    /// </summary>
    private const float TransferSweepSeconds = 30f;

    private float _transferSweepAccumulator;
    private bool _transferSweepRunning;

    public override void Initialize()
    {
        base.Initialize();

        // The boot sweep: a restart mid-offer must not strand a ship in escrow until the next
        // deadline happens to pass while someone is looking.
        _ = SweepExpiredTransfers();

        // There is deliberately no boot sweep for orphaned staging maps to match. Staging maps are
        // process-local and no map of any kind exists yet at system init, so it would provably do
        // nothing, and a hook whose name implies a recovery it cannot perform is worse than none.
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnStripPrototypesReloaded);

        InitializeSweep();
        InitializeRebake();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // First, before the accumulator below. A job parked on a database continuation resumes in
        // the same tick that continuation landed: the server drains the synchronisation context
        // immediately before it ticks the systems, so running the queue at the top of our update
        // costs the job no extra tick of latency.
        ProcessJobs();

        _transferSweepAccumulator += frameTime;
        if (_transferSweepAccumulator < TransferSweepSeconds)
            return;

        _transferSweepAccumulator = 0f;
        _ = SweepExpiredTransfers();
    }

    public override void Shutdown()
    {
        base.Shutdown();

        // A pipeline surviving into a shutdown has nothing left to run against, and its unwind holds
        // the only record of what it took off the ship.
        CancelAllJobs("server shutdown");
    }

    /// <summary>
    /// The round boundary. Every grid, map and station a pipeline is holding is about to stop
    /// existing, so a store still in flight is asked to stop and unwind, and whatever private maps
    /// the cancelled pipelines leave behind are swept.
    ///
    /// <para>The sweep tolerates a job that has not noticed yet. A pipeline parked waiting on the
    /// database only observes its cancellation at the next resume, so its staging map still has a
    /// live owner here and is skipped; the next sweep, or shutdown, catches it.</para>
    /// </summary>
    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        CancelAllJobs("round restart");
        SweepOrphanStagingMaps();
    }

    private async Task SweepExpiredTransfers()
    {
        // One sweep in flight at a time. A slow database must not stack sweeps that then race each
        // other over the same rows; the resolve is conditional on Pending so a lost race is
        // harmless, but it is still wasted work.
        if (_transferSweepRunning || !DrydockWritable)
            return;

        _transferSweepRunning = true;
        try
        {
            var roundId = _ticker.RoundIdOrNull;
            var released = await _store.ExpireTransfers(DateTime.UtcNow, roundId);
            if (released.Count == 0)
                return;

            Log.Info($"Drydock: {released.Count} transfer offer(s) expired; ships returned to their owners' berths.");
            _shipyard.KickDrydockRefreshAll();
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: the transfer expiry sweep threw: {e}");
        }
        finally
        {
            _transferSweepRunning = false;
        }
    }
}
