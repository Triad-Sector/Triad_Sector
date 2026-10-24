using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._Triad.CCVar;
using Timer = Robust.Shared.Timing.Timer;

namespace Content.Server._Triad.Drydock;

/// <summary>What the re-bake sweep did with one stored ship. Each value is also the <c>result</c> label on <see cref="DrydockMetrics.Rebakes"/>.</summary>
public enum DrydockRebakeShipResult : byte
{
    /// <summary>The document needs nothing: no rename, no format step, no repair.</summary>
    Clean,

    /// <summary>A re-baked revision was filed and is now current.</summary>
    Filed,

    /// <summary>
    /// The current document would not decompress, failed its checksum, or its manifest would not
    /// parse. Left alone: a corrupt current is the retrieve ladder's to fall past, and re-baking what
    /// cannot be trusted would file a new current on top of it.
    /// </summary>
    Corrupt,

    /// <summary>The ship's current revision moved between the read and the filing. Nothing filed; not retried this sweep.</summary>
    Stale,

    /// <summary>The ship left storage between the listing and the filing. Nothing filed.</summary>
    WrongState,

    /// <summary>The ship, or its current document, was gone by the time it was read.</summary>
    NotFound,

    /// <summary>Something threw. Nothing filed, and the error is logged.</summary>
    Failed,
}

/// <summary>Whether a request to start the re-bake sweep started one.</summary>
public enum DrydockRebakeStart : byte
{
    Started,
    AlreadyRunning,

    /// <summary>The drydock is off or read-only, the re-bake switch is off, or the pace is zero.</summary>
    Disabled,
}

/// <summary>What one pass of the re-bake sweep saw, per ship.</summary>
public sealed class DrydockRebakeSweepReport
{
    public Dictionary<Guid, DrydockRebakeShipResult> Ships { get; } = new();

    /// <summary>Ships whose current document, after the re-bake, still references content no mapping resolves.</summary>
    public List<Guid> Unresolvable { get; } = new();

    /// <summary>The sweep reached the end of the fleet rather than being stopped by a switch.</summary>
    public bool Completed { get; set; }

    public int Count(DrydockRebakeShipResult result) => Ships.Values.Count(r => r == result);
}

/// <summary>
/// Tier 1 of the re-bake ladder as a runner: a sweep over every stored ship that bakes the migration
/// mappings into its current document and files the result as a new
/// <see cref="DrydockRevisionKind.SystemRebake"/> revision, keeping the old one. The transform itself
/// is <see cref="DrydockDocumentRebake"/>; this is the loop, the gates and the filing.
///
/// <para><b>When it runs.</b> Once per server run, <see cref="RebakeBootDelaySeconds"/> of game loop
/// time after startup, through <c>Timer.Spawn</c>, which the round-end sweep already uses to defer
/// onto the running loop. Startup is the only moment the
/// mappings can have changed. It is scheduled only if the switches allow it when the system
/// initializes, which is after the server config is read, and the <c>drydockrebake</c> admin command
/// starts one by hand. Startup never waits on it.</para>
///
/// <para><b>Threads.</b> The database calls resume on the game thread and every byte of work
/// (decompress, checksum, transform, manifest rewrite, compress, drift read) runs on a worker in one
/// hop per ship. After every resume the switches are read again, so turning the re-bake off, or the
/// drydock off or read-only, stops the sweep before it reads or files another ship.</para>
///
/// <para><b>Conservative on purpose.</b> Production has no database backups. Each filing is conditional
/// on the ship still being stored on the revision the document came from, prunes nothing (a system
/// revision never costs a player document its place; the next player store prunes as usual), and
/// writes one <see cref="DrydockAuditAction.Rebake"/> row. A ship whose document drifted past what the
/// mappings heal is logged and counted toward <see cref="DrydockMetrics.UnresolvableShips"/> but gets
/// no timeline row: the retrieve gate writes that when someone reaches for the ship, and one per boot
/// would bury the timeline.</para>
/// </summary>
public sealed partial class DrydockSystem
{
    /// <summary>
    /// Which generation of the ladder produced a re-baked document, filed on the revision as
    /// <see cref="DrydockRevision.RebakeVersion"/>. Bump it whenever what
    /// <see cref="DrydockDocumentRebake.Transform(string, DrydockMigrationTable, int)"/> does to a document changes
    /// (a new format step, a new repair, a change to how renames apply), so revisions re-baked by the
    /// old behaviour can be told apart from ones re-baked by the new.
    /// </summary>
    internal const int LadderVersion = 1;

    /// <summary>How long after startup the boot sweep begins, in seconds of game loop time.</summary>
    private const int RebakeBootDelaySeconds = 60;

    /// <summary>Candidate rows per database read. Header columns only, so a page is small.</summary>
    private const int RebakeCandidatePageSize = 100;

    private bool _rebakeRunning;

    /// <summary>The switches every step of the sweep re-reads.</summary>
    private bool RebakeAllowed => DrydockWritable && _cfg.GetCVar(TriadCCVars.DrydockRebakeEnabled);

    /// <summary>The switches plus a non-zero pace: what a throttled sweep needs to start or keep going.</summary>
    private bool RebakeThrottledAllowed => RebakeAllowed && _cfg.GetCVar(TriadCCVars.DrydockRebakeShipsPerMinute) > 0;

    private void InitializeRebake()
    {
        if (!RebakeThrottledAllowed)
            return;

        Timer.Spawn(TimeSpan.FromSeconds(RebakeBootDelaySeconds), () => StartRebakeSweep("boot"));
    }

    /// <summary>
    /// Starts a throttled sweep in the background unless one is running or the switches refuse.
    /// Main thread only; the sweep is fire-and-forget and logs its own summary.
    /// </summary>
    /// <param name="trigger">Who asked, for the log.</param>
    public DrydockRebakeStart StartRebakeSweep(string trigger)
    {
        if (_rebakeRunning)
            return DrydockRebakeStart.AlreadyRunning;

        if (!RebakeThrottledAllowed)
            return DrydockRebakeStart.Disabled;

        Log.Info($"Drydock: re-bake sweep starting ({trigger}).");
        _ = RunRebakeSweep(throttle: true);
        return DrydockRebakeStart.Started;
    }

    /// <summary>
    /// One pass over every stored ship. Returns null without doing anything when a sweep is already
    /// running. Sets <see cref="_rebakeRunning"/> before its first await, so a second call from the
    /// main thread always sees it.
    /// </summary>
    /// <param name="throttle">
    /// Pace the sweep at <see cref="TriadCCVars.DrydockRebakeShipsPerMinute"/>. Off for fixtures, which
    /// want the sweep to finish while they pump ticks.
    /// </param>
    /// <param name="beforeFile">
    /// Fixture seam: awaited on the game thread after a changed document is ready and before it is
    /// filed, so a test can move the ship underneath the sweep. The switches are re-read after it.
    /// </param>
    /// <param name="pageSize">Candidates per read. A fixture passes a small one to walk more than one page.</param>
    internal async Task<DrydockRebakeSweepReport?> RunRebakeSweep(bool throttle, Func<Guid, Task>? beforeFile = null, int pageSize = RebakeCandidatePageSize)
    {
        if (_rebakeRunning)
            return null;

        _rebakeRunning = true;
        var report = new DrydockRebakeSweepReport();
        var clock = Stopwatch.StartNew();

        try
        {
            if (throttle ? !RebakeThrottledAllowed : !RebakeAllowed)
            {
                Log.Info("Drydock: re-bake sweep not run; the drydock is off or read-only, the re-bake switch is off, or its pace is zero.");
                return report;
            }

            // Read once here, on the game thread: the worker below only ever sees the built table.
            _ = MigrationTable;

            // A page of zero would read nothing forever.
            pageSize = Math.Max(1, pageSize);

            Guid? cursor = null;
            while (true)
            {
                var page = await _store.GetRebakeCandidates(cursor, pageSize);
                if (!RebakeAllowed)
                    return Stopped(report, clock);

                foreach (var candidate in page)
                {
                    cursor = candidate.ShipGuid;
                    var started = clock.Elapsed;

                    var result = await RebakeCandidate(candidate, report, beforeFile);
                    if (result is not { } outcome)
                        return Stopped(report, clock);

                    report.Ships[candidate.ShipGuid] = outcome;
                    DrydockMetrics.Rebakes.WithLabels(ResultLabel(outcome)).Inc();

                    if (!RebakeAllowed)
                        return Stopped(report, clock);

                    if (!throttle)
                        continue;

                    var perMinute = _cfg.GetCVar(TriadCCVars.DrydockRebakeShipsPerMinute);
                    if (perMinute <= 0)
                        return Stopped(report, clock);

                    var wait = TimeSpan.FromMinutes(1.0 / perMinute) - (clock.Elapsed - started);
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait);

                    if (!RebakeAllowed)
                        return Stopped(report, clock);
                }

                if (page.Count < pageSize)
                    break;
            }

            report.Completed = true;
            DrydockMetrics.UnresolvableShips.Set(report.Unresolvable.Count);
            Log.Info($"Drydock: re-bake sweep finished in {clock.Elapsed.TotalSeconds:F1} s. {Summary(report)}");
            return report;
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: the re-bake sweep threw and stopped. {Summary(report)} {e}");
            return report;
        }
        finally
        {
            _rebakeRunning = false;
        }
    }

    /// <summary>
    /// One ship: read, plan on a worker, file if changed. Null when a switch turned off during an await,
    /// which stops the sweep without an outcome for this ship.
    /// </summary>
    private async Task<DrydockRebakeShipResult?> RebakeCandidate(
        DrydockRebakeCandidate candidate,
        DrydockRebakeSweepReport report,
        Func<Guid, Task>? beforeFile)
    {
        var ship = candidate.ShipGuid;

        try
        {
            var load = await _store.LoadCurrent(ship);
            if (!RebakeAllowed)
                return null;

            if (load == null)
                return DrydockRebakeShipResult.NotFound;

            if (load.Ship.State != DrydockShipState.Stored)
                return DrydockRebakeShipResult.WrongState;

            var plan = await Task.Run(() => PlanRebake(load));
            if (!RebakeAllowed)
                return null;

            if (plan.Drift is { IsRefusal: true } drift)
            {
                report.Unresolvable.Add(ship);
                Log.Warning($"Drydock: stored ship {ship} ({candidate.ShipName}) revision {load.Revision.Revision} still references content nothing resolves: "
                    + $"unresolved [{string.Join(", ", drift.Unresolved)}], engine format {drift.EngineFormatVer}{(drift.EngineFormatOutOfWindow ? " (out of window)" : "")}, "
                    + $"drydock format {drift.DrydockFormatVer}{(drift.DrydockFormatOutOfWindow ? " (out of window)" : "")}.");
            }

            switch (plan.Outcome)
            {
                case DrydockRebakeShipResult.Corrupt:
                    Log.Warning($"Drydock: re-bake skipped {ship} ({candidate.ShipName}) revision {load.Revision.Revision}: {plan.Detail}.");
                    return plan.Outcome;
                case DrydockRebakeShipResult.Failed:
                    Log.Error($"Drydock: re-bake of {ship} ({candidate.ShipName}) revision {load.Revision.Revision} threw: {plan.Detail}");
                    return plan.Outcome;
                case not DrydockRebakeShipResult.Filed:
                    return plan.Outcome;
            }

            if (beforeFile != null)
            {
                await beforeFile(ship);
                if (!RebakeAllowed)
                    return null;
            }

            // Zero keeps every document: see the class remarks.
            var filed = await _store.FileRebakeRevision(plan.Request!, plan.Blob!, keepBlobs: 0);

            switch (filed.Outcome)
            {
                case DrydockRebakeResult.Success:
                    Log.Info($"Drydock: re-baked {ship} ({candidate.ShipName}) revision {load.Revision.Revision} into {filed.Revision}: "
                        + $"renamed [{string.Join(", ", plan.Transform!.AppliedRenames.Select(r => $"{r.From} -> {r.To}"))}], "
                        + $"steps [{string.Join(", ", plan.Transform.AppliedSteps)}].");
                    return DrydockRebakeShipResult.Filed;
                case DrydockRebakeResult.StaleSource:
                    Log.Info($"Drydock: re-bake of {ship} ({candidate.ShipName}) not filed; its current revision moved past {load.Revision.Revision} meanwhile.");
                    return DrydockRebakeShipResult.Stale;
                case DrydockRebakeResult.WrongState:
                    Log.Info($"Drydock: re-bake of {ship} ({candidate.ShipName}) not filed; it left storage meanwhile.");
                    return DrydockRebakeShipResult.WrongState;
                case DrydockRebakeResult.NotFound:
                    Log.Info($"Drydock: re-bake of {ship} ({candidate.ShipName}) not filed; the ship or its revision is gone.");
                    return DrydockRebakeShipResult.NotFound;
                default:
                    throw new ArgumentOutOfRangeException(nameof(filed.Outcome), filed.Outcome, null);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: re-bake of {ship} ({candidate.ShipName}) threw: {e}");
            return DrydockRebakeShipResult.Failed;
        }
    }

    /// <summary>
    /// Everything a re-bake computes from one loaded revision, off the game thread. Its
    /// <see cref="DrydockRebakePlan.Outcome"/> is <see cref="DrydockRebakeShipResult.Filed"/> when there
    /// is something to file, meaning "ready to file", not that anything was written.
    /// </summary>
    internal DrydockRebakePlan PlanRebake(DrydockLoad load)
    {
        try
        {
            byte[] bytes;
            try
            {
                bytes = DecompressZstd(load.Blob);
            }
            catch (Exception e)
            {
                return DrydockRebakePlan.Skip(DrydockRebakeShipResult.Corrupt, $"the document would not decompress ({e.Message})");
            }

            if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(load.Revision.Checksum))
                return DrydockRebakePlan.Skip(DrydockRebakeShipResult.Corrupt, "the document failed its checksum");

            var yaml = Encoding.UTF8.GetString(bytes);
            var (ids, engineFormat) = ReadDriftIds(yaml);
            var transform = DrydockDocumentRebake.Transform(
                yaml, ids, MigrationTable, load.Revision.DrydockFormatVer, DrydockDocumentRebake.FormatSteps, DrydockDocumentRebake.Repairs);

            // One scan per distinct text: an unchanged document keeps the ids already read.
            if (!ReferenceEquals(transform.Yaml, yaml))
                (ids, engineFormat) = ReadDriftIds(transform.Yaml);

            var drift = DetectDrift(ids, engineFormat, transform.DrydockFormatVer);

            if (!transform.Changed)
                return new DrydockRebakePlan(DrydockRebakeShipResult.Clean, null, null, transform, drift, null);

            // The manifest describes the document, so it takes the same renames. Captured keys and the
            // appraisal do not move with a prototype id, so the key hash is carried and the store copies
            // the appraisal.
            DrydockManifest? manifest;
            try
            {
                manifest = DrydockManifest.Deserialize(load.Revision.Manifest);
            }
            catch (JsonException)
            {
                manifest = null;
            }

            if (manifest == null)
                return DrydockRebakePlan.Skip(DrydockRebakeShipResult.Corrupt, "the manifest would not parse", drift);

            var renames = transform.AppliedRenames.ToDictionary(r => r.From, r => r.To);
            foreach (var entry in manifest.Entries)
            {
                if (renames.TryGetValue(entry.Proto, out var to))
                    entry.Proto = to;
            }

            var rebaked = ReferenceEquals(transform.Yaml, yaml) ? bytes : Encoding.UTF8.GetBytes(transform.Yaml);
            var fingerprint = DriftFingerprint(ids);

            var request = new DrydockRebakeRequest
            {
                ShipGuid = load.Ship.ShipGuid,
                SourceRevision = load.Revision.Revision,
                RebakeVersion = LadderVersion,
                EngineFormatVer = engineFormat,
                DrydockFormatVer = transform.DrydockFormatVer,
                ProtoFingerprint = fingerprint,
                CapturedKeyHash = load.Revision.CapturedKeyHash,
                Checksum = SHA256.HashData(rebaked),
                SizeBytes = rebaked.Length,
                Manifest = manifest.Serialize(),
            };

            return new DrydockRebakePlan(DrydockRebakeShipResult.Filed, request, CompressZstd(rebaked), transform, drift, null);
        }
        catch (Exception e)
        {
            return DrydockRebakePlan.Skip(DrydockRebakeShipResult.Failed, e.ToString());
        }
    }

    private DrydockRebakeSweepReport Stopped(DrydockRebakeSweepReport report, Stopwatch clock)
    {
        Log.Info($"Drydock: re-bake sweep stopped by a switch after {clock.Elapsed.TotalSeconds:F1} s. {Summary(report)}");
        return report;
    }

    private static string Summary(DrydockRebakeSweepReport report)
    {
        var counts = Enum.GetValues<DrydockRebakeShipResult>()
            .Select(r => $"{ResultLabel(r)}={report.Count(r)}");

        return $"Ships {report.Ships.Count}: {string.Join(", ", counts)}; unresolvable={report.Unresolvable.Count}.";
    }

    private static string ResultLabel(DrydockRebakeShipResult result) => result switch
    {
        DrydockRebakeShipResult.Clean => "clean",
        DrydockRebakeShipResult.Filed => "filed",
        DrydockRebakeShipResult.Corrupt => "corrupt",
        DrydockRebakeShipResult.Stale => "stale",
        DrydockRebakeShipResult.WrongState => "wrongstate",
        DrydockRebakeShipResult.NotFound => "notfound",
        DrydockRebakeShipResult.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, null),
    };
}

/// <summary>
/// What <see cref="DrydockSystem.PlanRebake"/> produced: the outcome so far, and for a changed document
/// the request and compressed bytes ready to file.
/// </summary>
/// <param name="Drift">The drift verdict on the document as it would be after the re-bake; null when the document could not be read.</param>
/// <param name="Detail">Why the ship was skipped, for the log.</param>
internal sealed record DrydockRebakePlan(
    DrydockRebakeShipResult Outcome,
    DrydockRebakeRequest? Request,
    byte[]? Blob,
    DrydockDocumentRebakeResult? Transform,
    DrydockDriftVerdict? Drift,
    string? Detail)
{
    public static DrydockRebakePlan Skip(DrydockRebakeShipResult outcome, string detail, DrydockDriftVerdict? drift = null) =>
        new(outcome, null, null, null, drift, detail);
}
