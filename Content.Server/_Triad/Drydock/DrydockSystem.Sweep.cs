using System;
using System.Threading.Tasks;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Server._Triad.RoundEnd;
using Content.Server.Shuttles.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Drydock;
using Content.Shared.GameTicking;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The round-end sweep: what happens to every hull the drydock has filed that is still in the
/// world when the round ends, and the hold that keeps the restart from deleting them first.
///
/// <para>Four verdicts, from the design page. A hull that could have brought itself home, meaning a
/// piloting console and at least one dock aboard, is taken into the lot by the forced store at the
/// configured share of its appraisal, with the owner free to reclaim it. A hull that could not is
/// written off: the row says Destroyed, the revisions stay, and the panel's Restore to is the way
/// back. A hull the forced store would not file stays checked out with a ShipStranded row naming the
/// failure. And a round the server never reaches the end of writes nothing, which is itself the
/// signal: the panel's Stranded chip is where those rows surface.</para>
///
/// <para>The sweep starts when the round ends, inside the restart window, and runs the pipeline
/// inline: no job, no slicing, the engine's own serializer. Slicing protects bystanders from tick
/// hitches and costs twice the wall time to do it, and at round end there are no bystanders left.
/// The restart itself is held by <see cref="HoldRestartForSweep"/> until the sweep drains or its
/// ceiling passes, because three separate callers reach RestartRound and any of them would pull the
/// rug mid-sweep otherwise.</para>
/// </summary>
public sealed partial class DrydockSystem
{
    // The player manager is injected on another partial as _player. RA0032 forbids a second
    // dependency field of the same type anywhere across a system's partials, so a partial that
    // needs it uses that field rather than declaring its own.
    [Dependency] private IChatManager _chat = default!;

    /// <summary>The round the sweep last started for, so a restart that follows an end does not run it twice.</summary>
    private int _sweptRound;

    private Task? _sweep;
    private readonly System.Diagnostics.Stopwatch _sweepClock = new();

    /// <summary>
    /// Set once the restart stopped waiting. The sweep reads it before every hull and files the
    /// rest as stranded rather than starting stores the restart is about to tear down.
    /// </summary>
    private bool _sweepCeilingPassed;

    private void InitializeSweep()
    {
        SubscribeLocalEvent<RoundEndedEvent>(OnRoundEnded);
        SubscribeLocalEvent<RoundEndWarningEvent>(OnRoundEndWarning);
    }

    /// <summary>True while a sweep is still filing hulls.</summary>
    internal bool SweepInFlight => _sweep is { IsCompleted: false };

    /// <summary>The sweep's fee share as a percent, from the fraction cvar, clamped the way every fee is.</summary>
    internal int RoundEndFeePercent
        => DrydockImpoundFee.ClampPercent((int)Math.Round(_cfg.GetCVar(TriadCCVars.DrydockImpoundRoundEndFraction) * 100f));

    /// <summary>
    /// The warning, on the round-end warning schedule players already hear: at the countdown's call and at
    /// each warning mark, every owner online with a drydock hull still out is told how many minutes
    /// are left to store it before it is impounded. The schedule belongs to
    /// <see cref="RoundEndWarningSystem"/>, so a recall and a new call warn again from the top. Sent
    /// to the owner's session rather than announced to the sector, since it is their ship and their fee.
    /// </summary>
    private void OnRoundEndWarning(ref RoundEndWarningEvent ev)
    {
        if (!DrydockWritable)
            return;

        var percent = RoundEndFeePercent;
        var minutes = (int)Math.Round(ev.Remaining.TotalMinutes);

        var query = AllEntityQuery<DrydockIdentityComponent, ShipOwnershipComponent>();
        while (query.MoveNext(out var grid, out var identity, out var ownership))
        {
            if (identity.ShipId == Guid.Empty || TerminatingOrDeleted(grid))
                continue;

            if (!_player.TryGetSessionById(ownership.OwnerUserId, out var session))
                continue;

            _chat.DispatchServerMessage(session, Loc.GetString("drydock-sweep-warning", ("ship", Name(grid)), ("minutes", minutes), ("percent", percent)));
        }
    }

    private void OnRoundEnded(RoundEndedEvent ev)
    {
        StartRoundEndSweep(ev.RoundId);
    }

    /// <summary>
    /// Called by <c>GameTicker.RestartRound</c> before it tears anything down. Starts the sweep for
    /// the current round if the round's end never did, which is an admin restart from inside a
    /// round, then answers whether the restart has to wait: true means a sweep is still filing
    /// hulls and the restart has been rescheduled through <paramref name="resume"/> for a second
    /// from now. Past the ceiling the restart goes ahead, the store in flight aborts on its next
    /// guard, and the hulls not reached are left checked out with a row saying why.
    /// </summary>
    public bool HoldRestartForSweep(Action resume)
    {
        if (!DrydockWritable)
            return false;

        var round = _ticker.RoundId;
        if (round > 0 && _sweptRound != round)
            StartRoundEndSweep(round);

        return HoldWhileSweeping(resume);
    }

    /// <summary>The hold itself, apart from the ticker so a fixture can drive it against a sweep of its own.</summary>
    internal bool HoldWhileSweeping(Action resume)
    {
        if (!SweepInFlight)
            return false;

        var ceiling = _cfg.GetCVar(TriadCCVars.DrydockImpoundRestartCeilingSeconds);
        var elapsed = _sweepClock.Elapsed.TotalSeconds;
        if (ceiling <= 0 || elapsed >= ceiling)
        {
            Log.Warning($"Drydock: the round-end sweep is still running after {elapsed:F0} s; the restart goes ahead and every hull it has not filed stays checked out.");
            _sweepCeilingPassed = true;
            return false;
        }

        Log.Info($"Drydock: holding the restart for the round-end sweep, {elapsed:F0} s in.");
        Timer.Spawn(TimeSpan.FromSeconds(1), resume);
        return true;
    }

    /// <summary>Starts the sweep for one round, once. Internal so a fixture can run it against a round of its own.</summary>
    internal void StartRoundEndSweep(int round)
    {
        if (_sweptRound == round)
            return;

        _sweptRound = round;
        _sweepCeilingPassed = false;

        if (!DrydockWritable)
        {
            Log.Info("Drydock: round-end sweep skipped, the drydock is off or read-only.");
            return;
        }

        _sweepClock.Restart();
        _sweep = RunRoundEndSweepSafe(round);
    }

    private async Task RunRoundEndSweepSafe(int round)
    {
        try
        {
            await RunRoundEndSweep(round);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: the round-end sweep of round {round} threw: {e}");
        }
    }

    /// <summary>
    /// The sweep itself: every row checked out in the round, judged in turn. Each row is re-read
    /// before it is judged, because a store or an admin may have moved it since the query, and the
    /// verdict writes are conditional on CheckedOut for the same reason.
    /// </summary>
    internal async Task RunRoundEndSweep(int round)
    {
        var rows = await _store.GetShipsCheckedOutInRound(round);
        if (rows.Count == 0)
            return;

        Log.Info($"Drydock: round-end sweep judging {rows.Count} hull(s) still out in round {round}.");

        // One world scan for the whole sweep; each row re-checks its grid, since the loop awaits.
        // The helm and dock scans are snapshots of the same moment, taken once rather than twice per
        // hull: a fitting built or taken apart while the sweep is filing is not seen.
        var liveGrids = LiveShipGridMap();
        var (gridsWithHelm, gridsWithDock) = GridsWithHelmsAndDocks();

        var percent = RoundEndFeePercent;
        var reason = Loc.GetString("drydock-sweep-impound-reason", ("round", round));
        int impounded = 0, destroyed = 0, stranded = 0, moved = 0;

        foreach (var row in rows)
        {
            var header = await _store.GetShipHeader(row.ShipGuid);
            if (header is not { State: DrydockShipState.CheckedOut } || header.CheckedOutRoundId != round)
            {
                moved++;
                continue;
            }

            if (_sweepCeilingPassed)
            {
                await WriteStranded(header, round, "the restart ceiling passed before the sweep reached this hull");
                stranded++;
                continue;
            }

            if (!liveGrids.TryGetValue(row.ShipGuid, out var grid) || TerminatingOrDeleted(grid))
            {
                // Blown apart, deleted, or its identity lost with a split: whatever it carried, no
                // hull was there to bring home.
                if (await _store.MarkDestroyed(row.ShipGuid, round, "no hull carried this ship at the end of the round"))
                    destroyed++;
                continue;
            }

            if (!CouldHaveComeHome(grid, gridsWithHelm, gridsWithDock))
            {
                if (await _store.MarkDestroyed(row.ShipGuid, round, "no piloting console or no dock aboard at the end of the round"))
                    destroyed++;
                continue;
            }

            var terms = new DrydockImpound(percent, reason, Redeemable: true, ActorUserId: null);
            var (result, _) = await TryImpoundShip(grid, header.OwnerUserId, round, terms, inline: true);
            if (result == DrydockStoreResult.Success)
            {
                impounded++;
                continue;
            }

            await WriteStranded(header, round, $"the sweep could not file it: {result}");
            stranded++;
        }

        Log.Info($"Drydock: round-end sweep of round {round} done: {impounded} impounded, {destroyed} written off, {stranded} stranded, {moved} already moved.");
    }

    /// <summary>
    /// The criterion, deliberately small: a piloting console and at least one dock aboard. Read
    /// as "could this hull have saved itself", never as "is this a ship": a console-less hull is
    /// still storable from the station's console if its owner gets it home, and this only decides
    /// what the sweep does for an owner who did not. The two sets come from
    /// <see cref="GridsWithHelmsAndDocks"/>.
    /// </summary>
    internal bool CouldHaveComeHome(EntityUid grid, HashSet<EntityUid> gridsWithHelm, HashSet<EntityUid> gridsWithDock)
    {
        return gridsWithHelm.Contains(grid) && gridsWithDock.Contains(grid);
    }

    /// <summary>
    /// Every grid carrying at least one shuttle console, and every grid carrying at least one dock,
    /// each from one world query. Taken once per sweep so the criterion is not two world scans per
    /// hull.
    /// </summary>
    internal (HashSet<EntityUid> Helms, HashSet<EntityUid> Docks) GridsWithHelmsAndDocks()
    {
        var helms = new HashSet<EntityUid>();
        var helmQuery = AllEntityQuery<ShuttleConsoleComponent, TransformComponent>();
        while (helmQuery.MoveNext(out _, out _, out var xform))
        {
            if (xform.GridUid is { } grid)
                helms.Add(grid);
        }

        var docks = new HashSet<EntityUid>();
        var dockQuery = AllEntityQuery<DockingComponent, TransformComponent>();
        while (dockQuery.MoveNext(out _, out _, out var xform))
        {
            if (xform.GridUid is { } grid)
                docks.Add(grid);
        }

        return (helms, docks);
    }

    /// <summary>The verdict row for a hull the sweep reached and could not file. The row itself stays checked out.</summary>
    private Task WriteStranded(DrydockShip header, int round, string reason)
    {
        return _store.WriteAudit(new DrydockAudit
        {
            ShipGuid = header.ShipGuid,
            ShipName = header.ShipName,
            BerthId = header.BerthId,
            Action = DrydockAuditAction.ShipStranded,
            ActorUserId = null,
            SubjectUserId = header.OwnerUserId,
            Revision = header.CurrentRevision,
            RoundId = round,
            Reason = reason,
        });
    }
}
