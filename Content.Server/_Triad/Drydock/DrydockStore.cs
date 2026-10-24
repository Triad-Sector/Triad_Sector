using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._Triad.ShipSize;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Robust.Shared.IoC;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// Every database query the drydock owns. It goes through
/// <see cref="IServerDbManager.RunTriadDbCommand{T}"/> rather than adding methods to
/// <see cref="IServerDbManager"/> itself, which is the seam this fork already uses for
/// feature-owned stores: the queries here make no sense in core, and keeping them out of an
/// upstream file means an upstream pull conflicts on nothing.
/// </summary>
public sealed partial class DrydockStore
{
    [Dependency] private IServerDbManager _db = default!;

    /// <summary>
    /// The sale price lives only in the ShipSold audit row's reason text; this reads it back for
    /// the admin panel and the restore-from-sale dialog.
    /// </summary>
    private static readonly Regex SoldForPattern = new(@"sold for (\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Reads the sale price back out of a ShipSold audit row's reason text, the one place
    /// <see cref="SoldForPattern"/> is applied. Null when the row carries no reason or the reason
    /// does not match, which "sold for N" always does for a row this store wrote.
    /// </summary>
    private static int? ParseSoldPrice(string? reason)
    {
        if (reason == null)
            return null;

        var match = SoldForPattern.Match(reason);
        return match.Success && int.TryParse(match.Groups[1].Value, out var price) ? price : null;
    }

    /// <summary>
    /// Appends one timeline row. Every audit insert in this file goes through here so the set of
    /// columns a row can carry, and their null-ness when a call site has nothing for one, stays in
    /// one place. <paramref name="createdAt"/> is required rather than defaulted to
    /// <see cref="DateTime.UtcNow"/> because every call site already has a <c>now</c> local shared
    /// with the state write in the same transaction, and the audit row has to agree with it.
    /// </summary>
    private static void AddAudit(
        ServerDbContext db,
        DrydockAuditAction action,
        DateTime createdAt,
        Guid? shipGuid = null,
        int? berthId = null,
        string? shipName = null,
        Guid? actorUserId = null,
        Guid? subjectUserId = null,
        int? revision = null,
        int? roundId = null,
        string? reason = null)
    {
        db.DrydockAudit.Add(new DrydockAudit
        {
            ShipGuid = shipGuid,
            BerthId = berthId,
            ShipName = shipName,
            Action = action,
            ActorUserId = actorUserId,
            SubjectUserId = subjectUserId,
            Revision = revision,
            RoundId = roundId,
            Reason = reason,
            CreatedAt = createdAt,
        });
    }

    /// <summary>
    /// The fewest documents a prune ever leaves a ship holding, counting the one just filed: the
    /// current revision and one step back for the retrieve ladder to fall to. A floor rather than a
    /// pin, because it promises only that a second document exists, not that it still loads.
    /// </summary>
    private const int MinimumKeptBlobs = 2;

    /// <summary>
    /// Deletes this ship's blobs that fall outside retention, never revisions. Three rules, all of
    /// which have to agree before a document goes:
    ///
    /// <list type="bullet">
    /// <item>Keep-N: the newest <paramref name="keepBlobs"/> documents stay, counting
    /// <paramref name="keptRevision"/>, the one just filed or promoted, which is always the newest.</item>
    /// <item>The floor: never fewer than <see cref="MinimumKeptBlobs"/>, so <paramref name="keepBlobs"/>
    /// of 1 behaves exactly as 2, and a ship holding only its new document prunes nothing.</item>
    /// <item>Pins: a <see cref="DrydockRevision.Pinned"/> revision's document is never deleted, however
    /// far outside the window it falls. A pinned document inside the window still counts toward it.</item>
    /// </list>
    ///
    /// <para>Zero or less prunes nothing, rather than keeping nothing: of the two readings, only this
    /// one costs disk when it is misconfigured.</para>
    ///
    /// <para>The window is counted over the documents that exist rather than by revision arithmetic,
    /// so a gap in a ship's documents (a hand repair, a prune under a smaller window) cannot
    /// pull the edge past the only one left.
    /// <paramref name="keptRevision"/> is still unsaved when this runs (every caller adds it to the
    /// tracker first and saves after), which is why the count reads the table below it and reserves
    /// one place for it.</para>
    ///
    /// <para>Set-based rather than load-then-remove: a <see cref="DrydockBlob"/> row carries the
    /// compressed document, multiple megabytes each, and nothing here ever wants the bytes back. The
    /// window's edge is one integer read off the primary key; the delete is one statement with the
    /// pin exclusion inside it. Both run on the context's current transaction, so they commit or roll
    /// back with the revision being pruned around.</para>
    ///
    /// <para>Takes the ship row first (<see cref="LockShipRow"/>), the same row a pin takes before it
    /// moves the flag, so on Postgres a pin and a prune of the same ship serialize: a pin committed
    /// first is seen by the delete, and a pin arriving second waits for this commit and then sees what
    /// it left, refusing a document it deleted rather than reporting success on it. Reasoned from
    /// read-committed row locking, not exercised by a test: the integration suite runs on SQLite.</para>
    /// </summary>
    private static async Task PruneBlobs(ServerDbContext db, Guid shipGuid, int keptRevision, int keepBlobs, CancellationToken token)
    {
        if (keepBlobs <= 0)
            return;

        await LockShipRow(db, shipGuid, token);

        var keep = Math.Max(keepBlobs, MinimumKeptBlobs);

        // The oldest already-saved document that stays: the (keep - 1)th newest below the one just
        // filed. Null when fewer than that exist, which is the floor refusing to prune at all.
        var oldestKept = await db.DrydockBlob.AsNoTracking()
            .Where(b => b.ShipGuid == shipGuid && b.Revision < keptRevision)
            .OrderByDescending(b => b.Revision)
            .Select(b => (int?) b.Revision)
            .Skip(keep - 2)
            .FirstOrDefaultAsync(token);

        if (oldestKept is not { } edge)
            return;

        await db.DrydockBlob
            .Where(b => b.ShipGuid == shipGuid
                && b.Revision < edge
                && !db.DrydockRevision.Any(r => r.ShipGuid == b.ShipGuid && r.Revision == b.Revision && r.Pinned))
            .ExecuteDeleteAsync(token);
    }

    /// <summary>
    /// Takes the ship row's write lock for the rest of the transaction without changing anything, by
    /// assigning a column to itself. The ordering point between a prune and a pin of the same ship;
    /// SQLite serializes writers anyway, so this matters on Postgres. Every caller reaches it before
    /// writing any revision or blob row, so ship-then-revision is the one lock order and a pin and a
    /// prune cannot deadlock each other.
    /// </summary>
    /// <returns>The number of rows matched: zero means the ship does not exist (yet, for a first store).</returns>
    private static Task<int> LockShipRow(ServerDbContext db, Guid shipGuid, CancellationToken token)
    {
        return db.DrydockShip
            .Where(s => s.ShipGuid == shipGuid)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.CurrentRevision, s => s.CurrentRevision), token);
    }

    /// <summary>
    /// The seat-from-state move every path back into a berth applies: state to
    /// <see cref="DrydockShipState.Stored"/>, the round cleared, the old berth remembered, the new
    /// one taken. One conditional <c>ExecuteUpdate</c> on <paramref name="predicate"/>, which each
    /// caller writes to include whatever else has to be true of the row (its current state, and
    /// for a redemption the owner and the paid fee), so the WHERE stays exactly what that caller
    /// needs. The unique index on the berth column is still the arbiter of a race for the same
    /// slot; callers catch <see cref="IsBerthUniqueViolation"/> around this themselves, because
    /// what they return on that fault differs by caller.
    /// </summary>
    /// <returns>The number of rows moved: zero means the predicate matched nothing.</returns>
    private static Task<int> SeatFromState(
        ServerDbContext db,
        Expression<Func<DrydockShip, bool>> predicate,
        int? newBerthId,
        DateTime now,
        CancellationToken token)
    {
        return db.DrydockShip
            .Where(predicate)
            .ExecuteUpdateAsync(set => set
                .SetState(DrydockShipState.Stored, now)
                .ClearRound()
                .SetProperty(s => s.LastBerthId, s => s.BerthId)
                .SetProperty(s => s.BerthId, newBerthId), token);
    }

    /// <summary>
    /// Whether a store keeps the berth the hull already holds: it holds one, the caller named no
    /// other, and that berth still exists and is large enough. Shared by the tracked seat a filing
    /// does and by the advisory check a store makes before it touches anything, so both answer
    /// "would the current berth still work" the same way.
    /// </summary>
    private static async Task<bool> KeepsHeldBerth(ServerDbContext db, int? heldBerthId, int? requestedBerth, string? hullClass, CancellationToken token)
    {
        if (heldBerthId is not { } held || (requestedBerth != null && requestedBerth != held))
            return false;

        var current = await db.DrydockBerth.AsNoTracking()
            .SingleOrDefaultAsync(b => b.BerthId == held, token);

        return current != null && ShipSizeRules.Fits(hullClass, current.MaxSizeClass);
    }

    /// <summary>
    /// Files a new revision against a ship, creating the hull row if this is its first store.
    ///
    /// <para>Everything happens in one transaction: the ship row, the revision, the blob, the blob
    /// pruning, and the audit entry. A torn write here is the failure this design exists to prevent,
    /// where a prune deletes the old blob without the new one landing.</para>
    ///
    /// <para>Two stores of the same ship racing cannot dupe. The revision number is read from the
    /// ship row and incremented, so a race produces the same number twice, and the composite primary
    /// key on (ship_guid, revision) makes the second transaction fail loudly rather than overwrite.
    /// Failing loudly is the point: the caller still holds a live grid and can refuse.</para>
    ///
    /// <para>Not the way to file a re-bake, and refuses one by throwing: this path reads the pointer
    /// off a tracked row and refreshes the display cache from the request, where a re-bake has to
    /// advance the pointer only if the ship is still stored and still on the revision it was derived
    /// from. That is <see cref="FileRebakeRevision"/>.</para>
    /// </summary>
    /// <param name="keepBlobs">
    /// How many revisions keep their document, never fewer than two once two exist, plus any that
    /// are pinned. Zero or less prunes nothing. The revision just filed is never pruned, whatever
    /// this says. See <see cref="PruneBlobs"/>.
    /// </param>
    /// <returns>The outcome, the revision number filed, and the berth the ship now sits in.</returns>
    public Task<DrydockFileResult> FileRevision(DrydockRevisionRequest request, byte[] blob, int keepBlobs, CancellationToken ct = default)
    {
        if (request.Kind == DrydockRevisionKind.SystemRebake)
            throw new ArgumentException($"A re-bake is filed through {nameof(FileRebakeRevision)}, which checks the ship is still on its source revision.", nameof(request));

        return _db.RunTriadDbCommand(async (db, token) =>
        {
            // A store that picks a berth can lose it to another store committing in the same
            // instant, and the unique index on the ship's berth column is the arbiter. Each attempt
            // is its own transaction against a cleared tracker, the lost berth is excluded from the
            // next pick, and three attempts is more free berths than any real garage has. A ship
            // that kept the berth it already held never retries: a violation there is the revision
            // key, and that one has to stay loud.
            var excluded = new HashSet<int>();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                int? picked = null;
                try
                {
                    return await FileRevisionOnce(db, request, blob, keepBlobs, excluded, id => picked = id, token);
                }
                catch (DbUpdateException e) when (picked is { } lost && IsBerthUniqueViolation(e))
                {
                    db.ChangeTracker.Clear();
                    excluded.Add(lost);
                }
            }

            return new DrydockFileResult(DrydockBerthResult.Conflict, 0, null);
        }, ct);
    }

    /// <summary>
    /// Whether a failed write was the berth unique index and nothing else. The retry above must
    /// swallow that one fault only: catching every update exception turns an unrelated failure into
    /// a polite "no free berth" and hides it. Provider-typed on purpose; the constraint name is the
    /// one EF generates for the index on the berth column. Takes the raw provider exception as well
    /// as EF's wrapper: a conditional <c>ExecuteUpdate</c> throws the former, a tracked
    /// <c>SaveChanges</c> the latter.
    /// </summary>
    internal static bool IsBerthUniqueViolation(Exception e)
    {
        var cause = e is DbUpdateException update ? update.InnerException : e;
        return cause switch
        {
            Npgsql.PostgresException pg => pg.SqlState == "23505"
                && pg.ConstraintName is { } name
                && name.Contains("drydock_ship_berth_id", StringComparison.Ordinal),
            Microsoft.Data.Sqlite.SqliteException sq => sq.SqliteErrorCode == 19
                && sq.Message.Contains("UNIQUE", StringComparison.Ordinal)
                && sq.Message.Contains("drydock_ship.berth_id", StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// Makes a filed ship retrievable. The pipeline calls this after the grid is gone, never
    /// before: filing and marking are two steps because the filing write yields, and an occupant
    /// who boards during it has to refuse the store without leaving a retrievable row behind a
    /// live ship, which is a duplicate.
    /// </summary>
    public Task<bool> MarkStored(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var now = DateTime.UtcNow;
            var moved = await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid && s.State == DrydockShipState.CheckedOut)
                .ExecuteUpdateAsync(set => set
                    .SetState(DrydockShipState.Stored, now)
                    .ClearRound(), token);

            return moved > 0;
        }, ct);
    }

    /// <summary>
    /// Takes a ship that is sitting in a berth into the impound lot: no hull, no document, one
    /// transaction. The state move is conditional on <see cref="DrydockShipState.Stored"/>, which
    /// is what keeps a retrieve that claimed the row an instant earlier from being overwritten, and
    /// what refuses a row in escrow (withdraw the offer first) or a terminal one (its state is a
    /// verdict, not a hull). The berth is vacated and remembered, the terms are written, and the
    /// audit row carries the fee, all under the same commit, so no failure between them can leave a
    /// row that reads impounded while still holding its berth.
    ///
    /// <para>The fee is taken against the current revision's
    /// <see cref="DrydockRevision.AppraisedValue"/>, because a stored ship has nothing left to
    /// appraise; a revision that never recorded one charges nothing, which is the safe direction to
    /// be wrong in.</para>
    /// </summary>
    /// <returns>The outcome, and on success the credits the row was left owing.</returns>
    public Task<(DrydockBerthResult Outcome, int Fee)> TryImpoundStored(
        Guid shipGuid,
        DrydockImpound impound,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(DrydockBerthResult, int)>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var snapshot = await db.DrydockShip
                .AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.ShipName, s.OwnerUserId, s.CurrentRevision, s.BerthId, s.State })
                .SingleOrDefaultAsync(token);

            if (snapshot == null)
                return (DrydockBerthResult.NotFound, 0);

            if (snapshot.State != DrydockShipState.Stored)
                return (DrydockBerthResult.WrongState, 0);

            var appraisal = await db.DrydockRevision
                .AsNoTracking()
                .Where(r => r.ShipGuid == shipGuid && r.Revision == snapshot.CurrentRevision)
                .Select(r => r.AppraisedValue)
                .SingleOrDefaultAsync(token) ?? 0;

            var fee = impound.FeeAgainst(appraisal);
            var now = DateTime.UtcNow;

            var moved = await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid && s.State == DrydockShipState.Stored)
                .ExecuteUpdateAsync(set => set
                    .SetState(DrydockShipState.Impounded, now)
                    .SetProperty(s => s.ImpoundFee, fee)
                    .SetProperty(s => s.ImpoundReason, impound.Reason)
                    .SetProperty(s => s.ImpoundRedeemable, impound.Redeemable)
                    .VacateBerth(), token);

            if (moved == 0)
                return (DrydockBerthResult.WrongState, 0);

            AddAudit(db, DrydockAuditAction.Impound, now,
                shipGuid: shipGuid,
                berthId: snapshot.BerthId,
                shipName: snapshot.ShipName,
                actorUserId: impound.ActorUserId,
                subjectUserId: snapshot.OwnerUserId,
                revision: snapshot.CurrentRevision,
                roundId: roundId,
                reason: impound.AuditReason(fee, appraisal, evicted: 0));

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return (DrydockBerthResult.Success, fee);
        }, ct);
    }

    /// <summary>
    /// The impound's counterpart to <see cref="MarkStored"/>, called once the hull is gone. Two
    /// steps for the same reason a store is two steps: the filing write yields, and a row that says
    /// impounded while a live grid still carries the ship is the duplicate the split exists to
    /// prevent.
    ///
    /// <para>Unconditional in the state it moves from, unlike <see cref="MarkStored"/>, because the
    /// hull was in the world whatever the row said about it, and the world is what an impound
    /// answers to. The round it left in is cleared, because the hull is now in the lot and nothing
    /// is out.</para>
    /// </summary>
    public Task<bool> MarkImpounded(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var now = DateTime.UtcNow;
            var moved = await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid && s.State != DrydockShipState.Impounded)
                .ExecuteUpdateAsync(set => set
                    .SetState(DrydockShipState.Impounded, now)
                    .ClearRound(), token);

            return moved > 0;
        }, ct);
    }

    /// <summary>
    /// The hulls the round-end sweep has to judge: checked out in the round that just ended and
    /// never stored back. Rows from earlier rounds are the panel's Stranded chip, not the sweep's:
    /// their hulls are long gone, and a verdict needs a hull to look at.
    /// </summary>
    public Task<List<DrydockShip>> GetShipsCheckedOutInRound(int roundId, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) => await db.DrydockShip
            .AsNoTracking()
            .Where(s => s.State == DrydockShipState.CheckedOut && s.CheckedOutRoundId == roundId)
            .OrderBy(s => s.ShipName)
            .ToListAsync(token), ct);
    }

    /// <summary>
    /// The sweep's verdict on a hull that could not have brought itself home: the row goes to
    /// <see cref="DrydockShipState.Destroyed"/>, a berth it still showed is vacated and remembered,
    /// and the timeline says so with the reason. One conditional update from CheckedOut, so a store
    /// that beat the sweep to the row is not overwritten. Revisions stay: the panel's Restore to is
    /// the way back, and it is always a human decision.
    /// </summary>
    public Task<bool> MarkDestroyed(Guid shipGuid, int? roundId, string reason, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var snapshot = await db.DrydockShip
                .AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.ShipName, s.OwnerUserId, s.CurrentRevision, s.BerthId })
                .SingleOrDefaultAsync(token);

            if (snapshot == null)
                return false;

            var now = DateTime.UtcNow;
            var moved = await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid && s.State == DrydockShipState.CheckedOut)
                .ExecuteUpdateAsync(set => set
                    .SetState(DrydockShipState.Destroyed, now)
                    .ClearRound()
                    .VacateBerth(), token);

            if (moved == 0)
                return false;

            AddAudit(db, DrydockAuditAction.ShipDestroyed, now,
                shipGuid: shipGuid,
                berthId: snapshot.BerthId,
                shipName: snapshot.ShipName,
                subjectUserId: snapshot.OwnerUserId,
                revision: snapshot.CurrentRevision,
                roundId: roundId,
                reason: reason);

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return true;
        }, ct);
    }

    /// <summary>
    /// Lifts an impound into one of the owner's berths, for nothing. An impounded ship holds no
    /// berth, so the only way out of the lot is into one: the named berth when the caller names it,
    /// otherwise the hull's last berth if it is still free and fits, otherwise the smallest free
    /// berth that fits, which is the seating rule a store uses. With nowhere to seat it the ship
    /// stays impounded and the outcome says why; granting a berth is the admin's answer to that.
    ///
    /// <para>The impound terms stay on the row on the way out. A reversal needs something to restore
    /// to, and the timeline has to keep saying what the hull was taken for.</para>
    /// </summary>
    /// <returns>The outcome, and on success the berth the ship now sits in.</returns>
    public Task<(DrydockBerthResult Outcome, int? BerthId)> TryReleaseImpound(
        Guid shipGuid,
        int? berthId,
        Guid? actorUserId,
        int? roundId,
        string? reason,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(DrydockBerthResult, int?)>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.OwnerUserId, s.State, s.SizeClass, s.LastBerthId, s.ShipName, s.CurrentRevision })
                .SingleOrDefaultAsync(token);

            if (ship == null)
                return (DrydockBerthResult.NotFound, null);

            if (ship.State != DrydockShipState.Impounded)
                return (DrydockBerthResult.WrongState, null);

            var (seated, pick) = await ResolveBerth(db, shipGuid, ship.OwnerUserId, ship.SizeClass, berthId, ship.LastBerthId, token);
            if (seated != DrydockBerthResult.Success)
                return (seated, null);

            // One conditional update on the state the row was read in; the unique index on the
            // berth column is the arbiter when the pick loses a race to another seating.
            var now = DateTime.UtcNow;
            int moved;
            try
            {
                moved = await SeatFromState(db, s => s.ShipGuid == shipGuid && s.State == DrydockShipState.Impounded, pick, now, token);
            }
            catch (Exception e) when (IsBerthUniqueViolation(e))
            {
                return (DrydockBerthResult.BerthOccupied, null);
            }

            if (moved == 0)
                return (DrydockBerthResult.WrongState, null);

            AddAudit(db, DrydockAuditAction.ImpoundReleased, now,
                shipGuid: shipGuid,
                berthId: pick,
                shipName: ship.ShipName,
                actorUserId: actorUserId,
                subjectUserId: ship.OwnerUserId,
                revision: ship.CurrentRevision,
                roundId: roundId,
                reason: reason);

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return (DrydockBerthResult.Success, pick);
        }, ct);
    }

    /// <summary>
    /// The owner pays the fee and takes an impounded ship back into a berth of theirs. The money
    /// moved before this was called, so the fee it was charged against comes back in and the move
    /// refuses as <see cref="DrydockBerthResult.Conflict"/> when the row's fee is any different: an
    /// admin who re-impounded on new terms between the console's read and the press must not have
    /// the old price honoured, and the console refunds on every refusal. The berth gets the three
    /// checks a named berth gets, the impound has to be one the owner may act on, and the move is
    /// one conditional update from <see cref="DrydockShipState.Impounded"/>. A locked impound refuses
    /// whatever was paid.
    ///
    /// <para>The terms stay on the row on the way out, so a reversal has something to restore to.</para>
    /// </summary>
    public Task<DrydockBerthResult> TryRedeemImpound(
        Guid shipGuid,
        Guid ownerUserId,
        int berthId,
        int paidFee,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.OwnerUserId, s.State, s.SizeClass, s.ShipName, s.CurrentRevision, s.ImpoundFee, s.ImpoundRedeemable })
                .SingleOrDefaultAsync(token);

            if (ship == null || ship.OwnerUserId != ownerUserId)
                return DrydockBerthResult.NotFound;

            if (ship.State != DrydockShipState.Impounded || !ship.ImpoundRedeemable)
                return DrydockBerthResult.WrongState;

            if (ship.ImpoundFee != paidFee)
                return DrydockBerthResult.Conflict;

            var (fit, _) = await ResolveBerth(db, shipGuid, ownerUserId, ship.SizeClass, berthId, null, token);
            if (fit != DrydockBerthResult.Success)
                return fit;

            var now = DateTime.UtcNow;
            int moved;
            try
            {
                moved = await SeatFromState(db,
                    s => s.ShipGuid == shipGuid
                        && s.State == DrydockShipState.Impounded
                        && s.OwnerUserId == ownerUserId
                        && s.ImpoundRedeemable
                        && s.ImpoundFee == paidFee,
                    berthId, now, token);
            }
            catch (Exception e) when (IsBerthUniqueViolation(e))
            {
                return DrydockBerthResult.BerthOccupied;
            }

            if (moved == 0)
                return DrydockBerthResult.WrongState;

            AddAudit(db, DrydockAuditAction.ImpoundRedeemed, now,
                shipGuid: shipGuid,
                berthId: berthId,
                shipName: ship.ShipName,
                actorUserId: ownerUserId,
                subjectUserId: ownerUserId,
                revision: ship.CurrentRevision,
                roundId: roundId,
                reason: paidFee > 0 ? $"paid {paidFee}" : "no fee");

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return DrydockBerthResult.Success;
        }, ct);
    }

    /// <summary>
    /// The owner walks away from an impounded ship rather than pay for it. No money moves in
    /// either direction, which is the whole difference from a sale, and the row goes to a terminal
    /// state of its own so an admin reads "the owner chose this" rather than inferring it. A locked
    /// impound refuses: an owner must not be able to end an adjudication from their side. The
    /// typed-name check is the console's; this is the row, one conditional update from
    /// <see cref="DrydockShipState.Impounded"/>. Revisions stay, so an admin restore can undo it.
    /// </summary>
    /// <returns>The outcome, and on success the name the ship was given up under.</returns>
    public Task<(DrydockBerthResult Outcome, string? ShipName)> TryAbandonShip(
        Guid shipGuid,
        Guid ownerUserId,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(DrydockBerthResult, string?)>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.OwnerUserId, s.State, s.ShipName, s.CurrentRevision, s.ImpoundFee, s.ImpoundRedeemable })
                .SingleOrDefaultAsync(token);

            if (ship == null || ship.OwnerUserId != ownerUserId)
                return (DrydockBerthResult.NotFound, null);

            if (ship.State != DrydockShipState.Impounded || !ship.ImpoundRedeemable)
                return (DrydockBerthResult.WrongState, null);

            var now = DateTime.UtcNow;
            var moved = await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid
                    && s.State == DrydockShipState.Impounded
                    && s.OwnerUserId == ownerUserId
                    && s.ImpoundRedeemable)
                .ExecuteUpdateAsync(set => set
                    .SetState(DrydockShipState.Abandoned, now)
                    .ClearRound(), token);

            if (moved == 0)
                return (DrydockBerthResult.WrongState, null);

            AddAudit(db, DrydockAuditAction.ShipAbandoned, now,
                shipGuid: shipGuid,
                shipName: ship.ShipName,
                actorUserId: ownerUserId,
                subjectUserId: ownerUserId,
                revision: ship.CurrentRevision,
                roundId: roundId,
                reason: ship.ImpoundFee > 0 ? $"gave up rather than pay {ship.ImpoundFee}" : "gave up");

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return (DrydockBerthResult.Success, ship.ShipName);
        }, ct);
    }

    private static async Task<DrydockFileResult> FileRevisionOnce(
        ServerDbContext db,
        DrydockRevisionRequest request,
        byte[] blob,
        int keepBlobs,
        HashSet<int> excludedBerths,
        Action<int> berthPicked,
        CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token);

        var now = DateTime.UtcNow;

        var ship = await db.DrydockShip
            .SingleOrDefaultAsync(s => s.ShipGuid == request.ShipGuid, token);

        if (ship == null)
        {
            // A new hull is out in the world at the moment it is first filed. It becomes stored
            // below only if the caller says so, or by MarkStored once the grid is gone.
            ship = new DrydockShip
            {
                ShipGuid = request.ShipGuid,
                OwnerUserId = request.OwnerUserId,
                State = DrydockShipState.CheckedOut,
                StateChangedAt = now,
                CheckedOutRoundId = request.CreatedRoundId,
                CreatedAt = now,
            };
            db.DrydockShip.Add(ship);
        }

        // Display cache, refreshed on every store. Ownership is NOT refreshed here: a transfer
        // is its own operation with its own audit row, and a store must never quietly move a
        // ship to whoever happened to be flying it.
        ship.ShipName = request.ShipName;
        ship.VesselProto = request.VesselProto;
        ship.SizeClass = request.SizeClass;
        ship.UpdatedAt = now;

        // A player store or an import needs somewhere to put the hull. Refusing here rolls the whole
        // transaction back: nothing is filed for a ship with nowhere to go. (A re-bake never reaches
        // this method; FileRevision refuses the kind before it opens a transaction.)
        //
        // An impound is the third case: it has somewhere to go that is not a berth, so it vacates
        // instead of seating. LastBerthId keeps where the hull came from, which is what a release
        // offers as its default and what makes the redemption gate mean something, since a hull
        // still holding its own berth would satisfy "find a free berth" for nothing.
        int? impoundVacated = null;
        if (request.Impound is { } impound)
        {
            if (ship.BerthId is { } vacated)
            {
                ship.LastBerthId = vacated;
                impoundVacated = vacated;
            }

            ship.BerthId = null;
            ship.ImpoundFee = impound.FeeAgainst(request.AppraisedValue ?? 0);
            ship.ImpoundReason = impound.Reason;
            ship.ImpoundRedeemable = impound.Redeemable;
        }
        else if (request.Kind is DrydockRevisionKind.PlayerStore or DrydockRevisionKind.LegacyImport)
        {
            var seated = await SeatShip(db, ship, request.SizeClass, request.BerthId, excludedBerths, berthPicked, token);
            if (seated != DrydockBerthResult.Success)
                return new DrydockFileResult(seated, 0, null);
        }

        var revision = ship.CurrentRevision + 1;

        db.DrydockRevision.Add(new DrydockRevision
        {
            ShipGuid = request.ShipGuid,
            Revision = revision,
            Kind = request.Kind,
            ActorUserId = request.ActorUserId,
            CreatedRoundId = request.CreatedRoundId,
            CreatedAt = now,
            EngineFormatVer = request.EngineFormatVer,
            DrydockFormatVer = request.DrydockFormatVer,
            ProtoFingerprint = request.ProtoFingerprint,
            CapturedKeyHash = request.CapturedKeyHash,
            Checksum = request.Checksum,
            SizeBytes = request.SizeBytes,
            AppraisedValue = request.AppraisedValue,
            Manifest = request.Manifest,
        });

        db.DrydockBlob.Add(new DrydockBlob
        {
            ShipGuid = request.ShipGuid,
            Revision = revision,
            Blob = blob,
        });

        ship.CurrentRevision = revision;

        // An import has no live grid, so it is stored the moment it is filed. A player store is
        // marked stored by the pipeline after the grid is gone, unless the caller asks for it
        // here. Any other kind leaves the state alone.
        if (request.Kind == DrydockRevisionKind.LegacyImport
            || (request.Kind == DrydockRevisionKind.PlayerStore && request.MarkStored))
        {
            ship.State = DrydockShipState.Stored;
            ship.StateChangedAt = now;
            ship.CheckedOutRoundId = null;
        }

        // Prune blobs, never revisions. The revision we just filed is the one a retrieve reads, so
        // it survives whatever keepBlobs says; PruneBlobs carries the floor and the pin exclusion.
        await PruneBlobs(db, request.ShipGuid, revision, keepBlobs, token);

        // An impound's row says which berth was vacated, who took the hull and from whom, and what
        // it cost; a store's says where the hull was seated and who put it there.
        AddAudit(db,
            request.Impound != null
                ? DrydockAuditAction.Impound
                : DrydockAuditAction.Store,
            now,
            shipGuid: request.ShipGuid,
            berthId: request.Impound != null ? impoundVacated : ship.BerthId,
            shipName: request.ShipName,
            actorUserId: request.ActorUserId,
            subjectUserId: request.Impound != null ? ship.OwnerUserId : null,
            revision: revision,
            roundId: request.CreatedRoundId,
            reason: request.Impound?.AuditReason(ship.ImpoundFee, request.AppraisedValue ?? 0, request.Evicted));

        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);

        return new DrydockFileResult(DrydockBerthResult.Success, revision, ship.BerthId);
    }

    /// <summary>
    /// Puts a hull in a berth as part of a store. Keeps the berth it already holds if that still
    /// fits (a crash between confirm and vacate leaves one behind, and this is where it heals),
    /// otherwise takes the named berth, otherwise picks one: its own old slot if free, else the
    /// smallest free berth that fits. Owner and vacancy are enforced by the database as well, so a
    /// pick that turns out to be taken by the time this commits fails on the unique index rather
    /// than filing two hulls into one slot.
    /// </summary>
    private static async Task<DrydockBerthResult> SeatShip(
        ServerDbContext db,
        DrydockShip ship,
        string? hullClass,
        int? requestedBerth,
        HashSet<int> excludedBerths,
        Action<int> berthPicked,
        CancellationToken token)
    {
        if (await KeepsHeldBerth(db, ship.BerthId, requestedBerth, hullClass, token))
            return DrydockBerthResult.Success;

        var (outcome, pick) = await ResolveBerth(db, ship.ShipGuid, ship.OwnerUserId, hullClass, requestedBerth, ship.LastBerthId, token, excludedBerths);
        if (outcome != DrydockBerthResult.Success)
            return outcome;

        ship.LastBerthId = ship.BerthId;
        ship.BerthId = pick;
        berthPicked(pick!.Value);
        return DrydockBerthResult.Success;
    }

    /// <summary>
    /// The berth a hull would be seated in, without seating it: the named berth after the three
    /// checks a named berth gets (the owner's, large enough, empty), else the pick. Shared by the
    /// tracked seat a filing does and by the verbs that seat a hull inside one conditional update,
    /// so both answer the same question the same way.
    /// </summary>
    private static async Task<(DrydockBerthResult Outcome, int? BerthId)> ResolveBerth(
        ServerDbContext db,
        Guid shipGuid,
        Guid ownerUserId,
        string? hullClass,
        int? requestedBerth,
        int? preferredBerth,
        CancellationToken token,
        IReadOnlySet<int>? excludedBerths = null)
    {
        if (requestedBerth is not { } wanted)
            return await PickFreeBerth(db, ownerUserId, hullClass, preferredBerth, token, excludedBerths);

        var named = await db.DrydockBerth.AsNoTracking()
            .SingleOrDefaultAsync(b => b.BerthId == wanted && b.OwnerUserId == ownerUserId, token);

        if (named == null)
            return (DrydockBerthResult.NotFound, null);

        if (!ShipSizeRules.Fits(hullClass, named.MaxSizeClass))
            return (DrydockBerthResult.BerthTooSmall, null);

        if (await db.DrydockShip.AnyAsync(s => s.BerthId == wanted && s.ShipGuid != shipGuid, token))
            return (DrydockBerthResult.BerthOccupied, null);

        return (DrydockBerthResult.Success, wanted);
    }

    /// <summary>
    /// The owner's free berths that accept the hull, preferring the ship's own old slot and then
    /// the smallest that fits so the big ones stay available. Free means no ship row points at
    /// it, which the unique index on that column answers directly. <paramref name="excludedBerths"/>
    /// is the filing retry's lost picks; null excludes nothing.
    /// </summary>
    private static async Task<(DrydockBerthResult Outcome, int? BerthId)> PickFreeBerth(
        ServerDbContext db,
        Guid ownerUserId,
        string? hullClass,
        int? preferredBerth,
        CancellationToken token,
        IReadOnlySet<int>? excludedBerths = null)
    {
        // A hull class that does not parse is a taxonomy the berths cannot answer for. Fail closed.
        if (!ShipSizeRules.TryParseClass(hullClass, out var hull))
            return (DrydockBerthResult.BerthTooSmall, null);

        var free = await db.DrydockBerth.AsNoTracking()
            .Where(b => b.OwnerUserId == ownerUserId && !db.DrydockShip.Any(s => s.BerthId == b.BerthId))
            .ToListAsync(token);

        if (excludedBerths != null)
            free.RemoveAll(b => excludedBerths.Contains(b.BerthId));

        if (free.Count == 0)
            return (DrydockBerthResult.NoBerth, null);

        var fitting = free
            .Where(b => ShipSizeRules.TryParseClass(b.MaxSizeClass, out var max) && hull <= max)
            .ToList();

        if (fitting.Count == 0)
            return (DrydockBerthResult.BerthTooSmall, null);

        var pick = fitting.FirstOrDefault(b => b.BerthId == preferredBerth)
            ?? ShipSizeRules.OrderByFitPreference(fitting, b => b.MaxSizeClass, b => b.BerthId).First();

        return (DrydockBerthResult.Success, pick.BerthId);
    }

    /// <summary>
    /// Empties the ship's berth. Called as the LAST step of a successful retrieve, after the ship
    /// is docked and the claim is confirmed, and deliberately not inside the state claim: the
    /// claim is what blocks a second retrieve, and a failure after it releases the state without
    /// ever having to re-seat a berth somebody else may have taken. The old slot is remembered so
    /// the next store can put the ship back where it was.
    /// </summary>
    public Task VacateBerth(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid && s.BerthId != null)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.LastBerthId, s => s.BerthId)
                    .SetProperty(s => s.BerthId, (int?)null)
                    .SetProperty(s => s.UpdatedAt, DateTime.UtcNow), token);
        }, ct);
    }

    /// <summary>
    /// Whether a store for this hull has somewhere to go, checked before the pipeline mutates
    /// anything so a full garage refuses cheaply. The answer is advisory: the filing transaction
    /// checks again and the unique index makes that one final.
    /// </summary>
    public Task<DrydockBerthResult> CheckBerthForStore(Guid shipGuid, Guid ownerUserId, string hullClass, int? requestedBerth = null, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.OwnerUserId, s.BerthId, s.LastBerthId })
                .SingleOrDefaultAsync(token);

            // The row's owner, not the caller's: a store never moves a ship between garages.
            var owner = ship?.OwnerUserId ?? ownerUserId;

            if (await KeepsHeldBerth(db, ship?.BerthId, requestedBerth, hullClass, token))
                return DrydockBerthResult.Success;

            // The same checks the filing transaction makes for a named or a picked berth, so the
            // player hears "too small" or "occupied" before anything aboard is touched.
            var (outcome, _) = await ResolveBerth(db, shipGuid, owner, hullClass, requestedBerth, ship?.LastBerthId, token);
            return outcome;
        }, ct);
    }

    /// <summary>
    /// Reads a ship's current revision and its blob, or null when the ship is unknown or its blob
    /// has been pruned. A pruned current revision should be impossible, since pruning has a floor,
    /// so a null here with a live ship row is worth an operator's attention rather than a retry.
    /// </summary>
    public Task<DrydockLoad?> LoadCurrent(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<DrydockLoad?>((db, token) => LoadJoined(db, shipGuid, null, token), ct);
    }

    /// <summary>
    /// Reads one specific revision and its blob, which is what the retrieve fallback walks when the
    /// current revision fails to decompress or fails its checksum. Null when that revision has no
    /// blob left, which is the ordinary outcome once pruning has been past it.
    /// </summary>
    public Task<DrydockLoad?> LoadRevision(Guid shipGuid, int revision, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<DrydockLoad?>((db, token) => LoadJoined(db, shipGuid, revision, token), ct);
    }

    /// <summary>
    /// The read behind both loads: the ship, the revision numbered <paramref name="revision"/> or
    /// the ship's current one when that is null, and its blob. One query, two joins, rather than the
    /// three round trips a header read followed by a revision read followed by a blob read would
    /// cost. An INNER JOIN drops out exactly where each of those would return null: no ship, no
    /// revision row at that number, or no blob left for it.
    /// </summary>
    private static Task<DrydockLoad?> LoadJoined(ServerDbContext db, Guid shipGuid, int? revision, CancellationToken token)
    {
        return db.DrydockShip.AsNoTracking()
            .Where(s => s.ShipGuid == shipGuid)
            .Join(db.DrydockRevision.AsNoTracking(),
                s => new { s.ShipGuid, Revision = revision ?? s.CurrentRevision },
                r => new { r.ShipGuid, r.Revision },
                (s, r) => new { Ship = s, Revision = r })
            .Join(db.DrydockBlob.AsNoTracking(),
                sr => new { sr.Revision.ShipGuid, sr.Revision.Revision },
                b => new { b.ShipGuid, b.Revision },
                (sr, b) => new DrydockLoad(sr.Ship, sr.Revision, b.Blob))
            .SingleOrDefaultAsync(token);
    }

    /// <summary>
    /// The hull row alone, for the ownership and state checks a console makes before it commits
    /// to anything. Deliberately not <see cref="LoadCurrent"/>: that reads the document too, and a
    /// refusal should not cost a blob.
    /// </summary>
    public Task<DrydockShip?> GetShipHeader(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) => await db.DrydockShip
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.ShipGuid == shipGuid, token), ct);
    }

    /// <summary>The stored-ship list for a console, drawn from the display cache alone.</summary>
    public Task<List<DrydockShip>> GetShipsByOwner(Guid ownerUserId, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) => await db.DrydockShip
            .AsNoTracking()
            .Where(s => s.OwnerUserId == ownerUserId)
            .OrderBy(s => s.ShipName)
            .ToListAsync(token), ct);
    }

    /// <summary>
    /// Moves a ship's state and records why, as a single conditional update.
    ///
    /// <para>The condition is the point. Retrieve gates on this transition, so "is it stored" and
    /// "mark it checked out" have to be one statement: read-then-write lets two concurrent retrieves
    /// both read <see cref="DrydockShipState.Stored"/> and both proceed, which is a duplicated ship.
    /// SQLite serializes writers and would never show it; Postgres at read committed would. The
    /// database is the only thing that can close this window, since a process-local guard does not
    /// survive a restart and does not span two server processes.</para>
    ///
    /// <para>The audit row is written only when the update actually moved something, so the timeline
    /// can never claim a change that did not happen.</para>
    /// </summary>
    /// <param name="expected">
    /// The state the ship must currently be in for the move to happen. Null means the caller does
    /// not care, which is right for administrative actions and wrong for anything racing.
    /// </param>
    /// <returns>False when the ship is unknown, is not in <paramref name="expected"/>, or is already
    /// in the requested state.</returns>
    public Task<bool> TrySetState(
        Guid shipGuid,
        DrydockShipState? expected,
        DrydockShipState state,
        DrydockAuditAction action,
        Guid? actorUserId,
        int? roundId,
        string? reason,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var snapshot = await db.DrydockShip
                .AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.ShipName, s.CurrentRevision, s.BerthId })
                .SingleOrDefaultAsync(token);

            if (snapshot == null)
                return false;

            var now = DateTime.UtcNow;

            // Only a checkout records a round. Every other state clears it, so "checked out in round
            // N and never came back" stays answerable from the row rather than by reading the
            // timeline. An impound clears it too: the hull is always saved before the row can say
            // impounded, so there is no impounded-while-flying for the round to be evidence of.
            var checkedOutRound = state == DrydockShipState.CheckedOut ? roundId : null;

            var query = db.DrydockShip.Where(s => s.ShipGuid == shipGuid && s.State != state);
            if (expected is { } required)
                query = query.Where(s => s.State == required);

            var moved = await query.ExecuteUpdateAsync(setters => setters
                .SetState(state, now)
                .SetProperty(s => s.CheckedOutRoundId, checkedOutRound), token);

            if (moved == 0)
                return false;

            AddAudit(db, action, now,
                shipGuid: shipGuid,
                berthId: snapshot.BerthId,
                shipName: snapshot.ShipName,
                actorUserId: actorUserId,
                revision: snapshot.CurrentRevision,
                roundId: roundId,
                reason: reason);

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return true;
        }, ct);
    }

    /// <summary>
    /// Writes a standalone timeline entry, for the actions that record something without moving the
    /// ship's state: a transfer, a deletion, an adjudication.
    /// </summary>
    public Task WriteAudit(DrydockAudit entry, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            entry.CreatedAt = DateTime.UtcNow;
            db.DrydockAudit.Add(entry);
            await db.SaveChangesAsync(token);
        }, ct);
    }

    /// <summary>
    /// What one account has done, newest first. This is the read behind "what has this player been
    /// sending", which the ship timeline cannot answer for a refusal on a ship that has no row yet.
    /// </summary>
    public Task<List<DrydockAudit>> GetAuditByActor(Guid actorUserId, int limit, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) => await db.DrydockAudit
            .AsNoTracking()
            .Where(a => a.ActorUserId == actorUserId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(Math.Max(1, limit))
            .ToListAsync(token), ct);
    }

    /// <summary>The ship's timeline, oldest first.</summary>
    public Task<List<DrydockAudit>> GetAudit(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) => await db.DrydockAudit
            .AsNoTracking()
            .Where(a => a.ShipGuid == shipGuid)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(token), ct);
    }

    /// <summary>Every berth an owner has, each with the hull sitting in it, for the terminal and the admin panel.</summary>
    public Task<List<DrydockBerthSlot>> GetBerths(Guid ownerUserId, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var berths = await db.DrydockBerth.AsNoTracking()
                .Where(b => b.OwnerUserId == ownerUserId)
                .OrderBy(b => b.BerthId)
                .ToListAsync(token);

            // Filtering occupants by owner is sound because the composite foreign key guarantees
            // a berth's occupant is its owner's ship.
            var occupants = await db.DrydockShip.AsNoTracking()
                .Where(s => s.OwnerUserId == ownerUserId && s.BerthId != null)
                .ToListAsync(token);

            var byBerth = occupants.ToDictionary(s => s.BerthId!.Value);
            return berths.Select(b => new DrydockBerthSlot(b, byBerth.GetValueOrDefault(b.BerthId))).ToList();
        }, ct);
    }

    /// <summary>
    /// Creates a berth. A grant records a price of zero whatever is passed, so a grant can never be
    /// sold for credits. The money itself moves at the terminal before this is called, and the
    /// caller refunds if this throws.
    /// </summary>
    public Task<int> AddBerth(
        Guid ownerUserId,
        ShipSizeClass maxSizeClass,
        DrydockBerthKind kind,
        int pricePaid,
        Guid? actorUserId,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var now = DateTime.UtcNow;
            var berth = new DrydockBerth
            {
                OwnerUserId = ownerUserId,
                MaxSizeClass = maxSizeClass.ToString(),
                Kind = kind,
                PricePaid = kind == DrydockBerthKind.Granted ? 0 : Math.Max(0, pricePaid),
                PurchasedAt = now,
                PurchasedRoundId = roundId,
            };

            db.DrydockBerth.Add(berth);
            await db.SaveChangesAsync(token);

            AddAudit(db, kind == DrydockBerthKind.Granted ? DrydockAuditAction.BerthGrant : DrydockAuditAction.BerthPurchase, now,
                berthId: berth.BerthId,
                actorUserId: actorUserId,
                subjectUserId: ownerUserId,
                roundId: roundId,
                reason: $"{maxSizeClass} berth, {berth.PricePaid} paid");

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return berth.BerthId;
        }, ct);
    }

    /// <summary>
    /// Sells or deletes an empty berth; a null <paramref name="requiredOwner"/> is the admin path.
    /// Returns the removed row so the caller can compute a refund from what was actually paid. A
    /// store that lands between the vacancy check and the delete trips the foreign key instead,
    /// and reads as occupied, which it is.
    /// </summary>
    public Task<(DrydockBerthResult Outcome, DrydockBerth? Berth)> TryRemoveBerth(
        int berthId,
        Guid? requiredOwner,
        DrydockAuditAction action,
        Guid? actorUserId,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(DrydockBerthResult, DrydockBerth?)>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var berth = await db.DrydockBerth
                .SingleOrDefaultAsync(b => b.BerthId == berthId && (requiredOwner == null || b.OwnerUserId == requiredOwner), token);

            if (berth == null)
                return (DrydockBerthResult.NotFound, null);

            if (await db.DrydockShip.AnyAsync(s => s.BerthId == berthId, token))
                return (DrydockBerthResult.BerthOccupied, null);

            db.DrydockBerth.Remove(berth);

            AddAudit(db, action, DateTime.UtcNow,
                berthId: berthId,
                actorUserId: actorUserId,
                subjectUserId: berth.OwnerUserId,
                roundId: roundId,
                reason: $"{berth.Kind} {berth.MaxSizeClass} berth, {berth.PricePaid} paid");

            try
            {
                await db.SaveChangesAsync(token);
                await tx.CommitAsync(token);
            }
            catch (DbUpdateException)
            {
                return (DrydockBerthResult.BerthOccupied, null);
            }

            return (DrydockBerthResult.Success, berth);
        }, ct);
    }

    /// <summary>
    /// Raises a berth's class in place, for a hull that grew while it was out. The delta was really
    /// paid, so it is refundable even on a granted berth; only the free base of a grant stays worth
    /// nothing, which is what flipping the kind records.
    /// </summary>
    public Task<DrydockBerthResult> TryUpgradeBerth(
        int berthId,
        Guid ownerUserId,
        ShipSizeClass newClass,
        int priceDelta,
        Guid? actorUserId,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var berth = await db.DrydockBerth
                .SingleOrDefaultAsync(b => b.BerthId == berthId && b.OwnerUserId == ownerUserId, token);

            if (berth == null)
                return DrydockBerthResult.NotFound;

            if (!ShipSizeRules.TryParseClass(berth.MaxSizeClass, out var current) || newClass <= current)
                return DrydockBerthResult.WrongState;

            berth.MaxSizeClass = newClass.ToString();
            berth.PricePaid += Math.Max(0, priceDelta);
            if (berth.PricePaid > 0)
                berth.Kind = DrydockBerthKind.Purchased;

            AddAudit(db, DrydockAuditAction.BerthUpgrade, DateTime.UtcNow,
                berthId: berthId,
                actorUserId: actorUserId,
                subjectUserId: ownerUserId,
                roundId: roundId,
                reason: $"{current} to {newClass}, {priceDelta} paid");

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return DrydockBerthResult.Success;
        }, ct);
    }

    /// <summary>
    /// Admin: moves a stored ship to another of its owner's berths, or with a null target vacates
    /// the berth a ship that is out is still shown in. A cross-owner move is a transfer, and the
    /// composite foreign key refuses it before anything here has to.
    ///
    /// <para>Vacating is for a hull that is out: a crash between a retrieve's confirm and its vacate
    /// leaves a flying ship in its slot, and this is the repair. A stored ship lives in its berth
    /// and is moved, never vacated: a stored ship with nowhere to be is one the console cannot draw
    /// and the free-berth anti-join counts as room. A ship in escrow keeps its berth for the same
    /// reason: every resolution but an accept sets it Stored without touching the berth, so a
    /// vacate under a standing offer manufactures exactly that row.</para>
    ///
    /// <para>Both moves are one conditional update on the state the row was read in, so a retrieve
    /// claiming the ship in the same instant is not overwritten by a move that read it as stored.</para>
    /// </summary>
    public Task<DrydockBerthResult> TryMoveShip(
        Guid shipGuid,
        int? targetBerthId,
        Guid? actorUserId,
        int? roundId,
        string? reason,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.OwnerUserId, s.State, s.SizeClass, s.BerthId, s.ShipName, s.CurrentRevision })
                .SingleOrDefaultAsync(token);

            if (ship == null)
                return DrydockBerthResult.NotFound;

            var now = DateTime.UtcNow;
            int moved;

            if (targetBerthId is { } target)
            {
                // Seating a ship that is out is a restore, which is a different decision.
                if (ship.State != DrydockShipState.Stored)
                    return DrydockBerthResult.WrongState;

                var (fit, _) = await ResolveBerth(db, shipGuid, ship.OwnerUserId, ship.SizeClass, target, null, token);
                if (fit != DrydockBerthResult.Success)
                    return fit;

                try
                {
                    moved = await db.DrydockShip
                        .Where(s => s.ShipGuid == shipGuid && s.State == DrydockShipState.Stored)
                        .ExecuteUpdateAsync(set => set
                            .SetProperty(s => s.LastBerthId, s => s.BerthId)
                            .SetProperty(s => s.BerthId, (int?)target)
                            .SetProperty(s => s.UpdatedAt, now), token);
                }
                catch (Exception e) when (IsBerthUniqueViolation(e))
                {
                    return DrydockBerthResult.BerthOccupied;
                }
            }
            else
            {
                if (ship.State is DrydockShipState.Stored or DrydockShipState.InEscrow || ship.BerthId == null)
                    return DrydockBerthResult.WrongState;

                moved = await db.DrydockShip
                    .Where(s => s.ShipGuid == shipGuid
                        && s.State != DrydockShipState.Stored
                        && s.State != DrydockShipState.InEscrow
                        && s.BerthId != null)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(s => s.LastBerthId, s => s.BerthId)
                        .SetProperty(s => s.BerthId, (int?)null)
                        .SetProperty(s => s.UpdatedAt, now), token);
            }

            if (moved == 0)
                return DrydockBerthResult.WrongState;

            AddAudit(db, DrydockAuditAction.BerthMove, now,
                shipGuid: shipGuid,
                berthId: targetBerthId ?? ship.BerthId,
                shipName: ship.ShipName,
                actorUserId: actorUserId,
                subjectUserId: ship.OwnerUserId,
                revision: ship.CurrentRevision,
                roundId: roundId,
                reason: reason);

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return DrydockBerthResult.Success;
        }, ct);
    }

    // ---------------------------------------------------------------- Transfers

    /// <summary>
    /// Opens an offer: the ship goes into escrow, keeping its berth, and one pending transfer row
    /// says to whom and until when. The recipient must have a free berth the hull fits right now,
    /// so an offer that could never be accepted is refused at the start rather than after thirty
    /// minutes. The filtered unique index makes a second pending offer on the same ship fail at
    /// the database, which reads back as a conflict.
    ///
    /// <para>The move into escrow is one conditional update on <see cref="DrydockShipState.Stored"/>,
    /// so an offer cannot land on a row a retrieve or a sale claimed in the same instant.</para>
    /// </summary>
    public Task<(DrydockBerthResult Outcome, DrydockTransfer? Transfer)> TryOfferTransfer(
        Guid shipGuid,
        Guid fromUserId,
        Guid toUserId,
        TimeSpan duration,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(DrydockBerthResult, DrydockTransfer?)>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.OwnerUserId, s.State, s.SizeClass, s.ShipName, s.BerthId, s.CurrentRevision })
                .SingleOrDefaultAsync(token);

            if (ship == null || ship.OwnerUserId != fromUserId)
                return (DrydockBerthResult.NotFound, null);

            if (fromUserId == toUserId || ship.State != DrydockShipState.Stored)
                return (DrydockBerthResult.WrongState, null);

            var (fit, _) = await PickFreeBerth(db, toUserId, ship.SizeClass, null, token);
            if (fit != DrydockBerthResult.Success)
                return (fit, null);

            var now = DateTime.UtcNow;
            var moved = await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid && s.State == DrydockShipState.Stored && s.OwnerUserId == fromUserId)
                .ExecuteUpdateAsync(set => set.SetState(DrydockShipState.InEscrow, now), token);

            if (moved == 0)
                return (DrydockBerthResult.WrongState, null);

            var transfer = new DrydockTransfer
            {
                ShipGuid = shipGuid,
                FromUserId = fromUserId,
                ToUserId = toUserId,
                CreatedAt = now,
                ExpiresAt = now + duration,
                Resolution = DrydockTransferResolution.Pending,
                RoundId = roundId,
            };
            db.DrydockTransfer.Add(transfer);

            AddAudit(db, DrydockAuditAction.TransferOffered, now,
                shipGuid: shipGuid,
                berthId: ship.BerthId,
                shipName: ship.ShipName,
                actorUserId: fromUserId,
                subjectUserId: toUserId,
                revision: ship.CurrentRevision,
                roundId: roundId,
                reason: $"expires {transfer.ExpiresAt:u}");

            try
            {
                await db.SaveChangesAsync(token);
                await tx.CommitAsync(token);
            }
            catch (DbUpdateException)
            {
                return (DrydockBerthResult.Conflict, null);
            }

            return (DrydockBerthResult.Success, transfer);
        }, ct);
    }

    /// <summary>
    /// Ends a pending offer without moving the ship: declined by the recipient, cancelled by the
    /// owner, or expired by the sweep. The ship leaves escrow and is stored again. The actor has to
    /// be the right party for the resolution, or null for the sweep.
    ///
    /// <para>Both writes are conditional. The offer is resolved only while it is still pending, so a
    /// decline and an accept landing in the same instant cannot both succeed, and the ship goes back
    /// to stored only while it is still in escrow, so an accept that already moved it to its new
    /// owner is never undone by a late expiry.</para>
    /// </summary>
    /// <returns>The offer as resolved, or null when it was not pending or the actor was the wrong party.</returns>
    public Task<DrydockTransfer?> TryResolveTransfer(
        long transferId,
        DrydockTransferResolution resolution,
        Guid? actorUserId,
        int? roundId,
        CancellationToken ct = default,
        bool adminOverride = false,
        string? reason = null)
    {
        return _db.RunTriadDbCommand<DrydockTransfer?>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var transfer = await db.DrydockTransfer.AsNoTracking()
                .SingleOrDefaultAsync(t => t.Id == transferId && t.Resolution == DrydockTransferResolution.Pending, token);
            if (transfer == null)
                return null;

            if (!adminOverride)
            {
                var allowed = resolution switch
                {
                    DrydockTransferResolution.Declined => actorUserId == transfer.ToUserId,
                    DrydockTransferResolution.Cancelled => actorUserId == transfer.FromUserId,
                    DrydockTransferResolution.Expired => actorUserId == null,
                    _ => false,
                };
                if (!allowed)
                    return null;
            }

            var now = DateTime.UtcNow;
            var resolved = await db.DrydockTransfer
                .Where(t => t.Id == transferId && t.Resolution == DrydockTransferResolution.Pending)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(t => t.Resolution, resolution)
                    .SetProperty(t => t.ResolvedAt, (DateTime?)now), token);

            if (resolved == 0)
                return null;

            await db.DrydockShip
                .Where(s => s.ShipGuid == transfer.ShipGuid && s.State == DrydockShipState.InEscrow)
                .ExecuteUpdateAsync(set => set.SetState(DrydockShipState.Stored, now), token);

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == transfer.ShipGuid)
                .Select(s => new { s.ShipName, s.BerthId, s.CurrentRevision })
                .SingleOrDefaultAsync(token);

            AddAudit(db,
                resolution switch
                {
                    DrydockTransferResolution.Declined => DrydockAuditAction.TransferDeclined,
                    DrydockTransferResolution.Cancelled => DrydockAuditAction.TransferCancelled,
                    _ => DrydockAuditAction.TransferExpired,
                },
                now,
                shipGuid: transfer.ShipGuid,
                berthId: ship?.BerthId,
                shipName: ship?.ShipName,
                actorUserId: actorUserId,
                subjectUserId: resolution == DrydockTransferResolution.Cancelled ? transfer.ToUserId : transfer.FromUserId,
                revision: ship?.CurrentRevision,
                roundId: roundId,
                reason: reason);

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            transfer.Resolution = resolution;
            transfer.ResolvedAt = now;
            return transfer;
        }, ct);
    }

    /// <summary>The standing offer on one ship, or null when it has none.</summary>
    public Task<DrydockTransfer?> GetPendingOfferForShip(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) => await db.DrydockTransfer.AsNoTracking()
            .SingleOrDefaultAsync(t => t.ShipGuid == shipGuid && t.Resolution == DrydockTransferResolution.Pending, token), ct);
    }

    /// <summary>The standing offers on a set of ships, keyed by ship: the clock on the admin panel's rows.</summary>
    public Task<Dictionary<Guid, DrydockTransfer>> GetPendingOffersForShips(IEnumerable<Guid> shipGuids, CancellationToken ct = default)
    {
        var ids = shipGuids.Distinct().ToList();
        if (ids.Count == 0)
            return Task.FromResult(new Dictionary<Guid, DrydockTransfer>());

        return _db.RunTriadDbCommand(async (db, token) => await db.DrydockTransfer.AsNoTracking()
            .Where(t => ids.Contains(t.ShipGuid) && t.Resolution == DrydockTransferResolution.Pending)
            .ToDictionaryAsync(t => t.ShipGuid, token), ct);
    }

    /// <summary>
    /// The most recent sale of a ship, read back from its timeline row, which is where the price
    /// was written. Null when the ship has never been sold.
    /// </summary>
    public Task<(int Price, DateTime At)?> GetLastSale(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(int, DateTime)?>(async (db, token) =>
        {
            var row = await db.DrydockAudit.AsNoTracking()
                .Where(a => a.ShipGuid == shipGuid && a.Action == DrydockAuditAction.ShipSold)
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync(token);

            if (row == null)
                return null;

            return ParseSoldPrice(row.Reason) is { } price ? (price, row.CreatedAt) : null;
        }, ct);
    }

    /// <summary>
    /// The last sale of each ship in a set that has one, keyed by ship: the figure on the admin
    /// panel's Sold rows and the sale card under the selected hull, which the panel folds into the
    /// same set so the two are one read. Read the way <see cref="GetLastSale"/> reads, from the
    /// newest ShipSold timeline row.
    /// </summary>
    public Task<Dictionary<Guid, (int Price, DateTime At)>> GetLastSales(IEnumerable<Guid> shipGuids, CancellationToken ct = default)
    {
        var ids = shipGuids.Distinct().ToList();
        if (ids.Count == 0)
            return Task.FromResult(new Dictionary<Guid, (int Price, DateTime At)>());

        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var rows = await db.DrydockAudit.AsNoTracking()
                .Where(a => a.ShipGuid != null && ids.Contains(a.ShipGuid.Value) && a.Action == DrydockAuditAction.ShipSold)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => new { a.ShipGuid, a.Reason, a.CreatedAt })
                .ToListAsync(token);

            // Newest first, so the first row seen for a ship is its last sale.
            var sales = new Dictionary<Guid, (int Price, DateTime At)>();
            foreach (var row in rows)
            {
                if (row.ShipGuid is not { } ship || sales.ContainsKey(ship))
                    continue;

                if (ParseSoldPrice(row.Reason) is { } price)
                    sales[ship] = (price, row.CreatedAt);
            }

            return sales;
        }, ct);
    }

    /// <summary>
    /// The recipient takes the ship: owner and berth move in one transaction, the offer resolves,
    /// and the ship is stored again under its new owner. The berth is picked now, not when the
    /// offer was made, because the recipient's garage may have changed in the meantime; a
    /// recipient with nowhere left to put it is told so and the offer stands.
    ///
    /// <para>The offer is resolved first and conditionally, so a decline, a withdrawal or the expiry
    /// sweep landing in the same instant either wins outright or loses outright; then the ship moves
    /// conditionally on still being in escrow under the giver. A refusal after the first write rolls
    /// both back. The timeline row names the giver as actor and the recipient as subject, which is
    /// the direction the transfer went, whoever pressed the button.</para>
    /// </summary>
    /// <returns>The outcome, and on success the berth the ship landed in and its name.</returns>
    public Task<(DrydockBerthResult Outcome, int? BerthId, string? ShipName)> TryAcceptTransfer(
        long transferId,
        Guid toUserId,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(DrydockBerthResult, int?, string?)>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var transfer = await db.DrydockTransfer.AsNoTracking()
                .SingleOrDefaultAsync(t => t.Id == transferId && t.Resolution == DrydockTransferResolution.Pending, token);
            if (transfer == null || transfer.ToUserId != toUserId)
                return (DrydockBerthResult.NotFound, null, null);

            var now = DateTime.UtcNow;
            if (transfer.ExpiresAt <= now)
                return (DrydockBerthResult.WrongState, null, null);

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == transfer.ShipGuid)
                .Select(s => new { s.OwnerUserId, s.State, s.SizeClass, s.ShipName, s.CurrentRevision })
                .SingleOrDefaultAsync(token);

            if (ship == null || ship.OwnerUserId != transfer.FromUserId || ship.State != DrydockShipState.InEscrow)
                return (DrydockBerthResult.WrongState, null, null);

            var (outcome, pick) = await PickFreeBerth(db, toUserId, ship.SizeClass, null, token);
            if (outcome != DrydockBerthResult.Success)
                return (outcome, null, null);

            var resolved = await db.DrydockTransfer
                .Where(t => t.Id == transferId && t.Resolution == DrydockTransferResolution.Pending)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(t => t.Resolution, DrydockTransferResolution.Accepted)
                    .SetProperty(t => t.ResolvedAt, (DateTime?)now), token);

            if (resolved == 0)
                return (DrydockBerthResult.WrongState, null, null);

            int moved;
            try
            {
                moved = await db.DrydockShip
                    .Where(s => s.ShipGuid == transfer.ShipGuid && s.State == DrydockShipState.InEscrow && s.OwnerUserId == transfer.FromUserId)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(s => s.OwnerUserId, toUserId)
                        .SetProperty(s => s.BerthId, pick)
                        .SetProperty(s => s.LastBerthId, (int?)null)
                        .SetState(DrydockShipState.Stored, now), token);
            }
            catch (Exception e) when (IsBerthUniqueViolation(e))
            {
                return (DrydockBerthResult.Conflict, null, null);
            }

            if (moved == 0)
                return (DrydockBerthResult.WrongState, null, null);

            AddAudit(db, DrydockAuditAction.Transfer, now,
                shipGuid: transfer.ShipGuid,
                berthId: pick,
                shipName: ship.ShipName,
                actorUserId: transfer.FromUserId,
                subjectUserId: toUserId,
                revision: ship.CurrentRevision,
                roundId: roundId,
                reason: "offer accepted");

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return (DrydockBerthResult.Success, pick, ship.ShipName);
        }, ct);
    }

    /// <summary>
    /// Expires every pending offer past its deadline and returns the ships released. Run on boot,
    /// so a restart mid-offer cannot strand a ship in escrow, and on a slow tick after that.
    /// </summary>
    public async Task<List<Guid>> ExpireTransfers(DateTime now, int? roundId, CancellationToken ct = default)
    {
        var due = await _db.RunTriadDbCommand(async (db, token) => await db.DrydockTransfer
            .AsNoTracking()
            .Where(t => t.Resolution == DrydockTransferResolution.Pending && t.ExpiresAt <= now)
            .Select(t => new { t.Id, t.ShipGuid })
            .ToListAsync(token), ct);

        var released = new List<Guid>();
        foreach (var row in due)
        {
            if (await TryResolveTransfer(row.Id, DrydockTransferResolution.Expired, null, roundId, ct) != null)
                released.Add(row.ShipGuid);
        }

        return released;
    }

    /// <summary>
    /// One standing offer with the ship it names, or null when there is no pending offer by that
    /// id. The console reads this before answering an offer so a message from the wrong party
    /// can be refused by name and written to the timeline, rather than swallowed by the resolve.
    /// </summary>
    public Task<(DrydockTransfer Transfer, DrydockShip Ship)?> GetPendingTransfer(long transferId, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(DrydockTransfer, DrydockShip)?>(async (db, token) =>
        {
            var transfer = await db.DrydockTransfer.AsNoTracking()
                .SingleOrDefaultAsync(t => t.Id == transferId && t.Resolution == DrydockTransferResolution.Pending, token);
            if (transfer == null)
                return null;

            var ship = await db.DrydockShip.AsNoTracking().SingleOrDefaultAsync(s => s.ShipGuid == transfer.ShipGuid, token);
            return ship == null ? null : (transfer, ship);
        }, ct);
    }

    /// <summary>Pending offers addressed to an account, each with the ship it is for: the recipient's alert.</summary>
    public Task<List<(DrydockTransfer Transfer, DrydockShip Ship)>> GetPendingOffersFor(Guid toUserId, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var transfers = await db.DrydockTransfer.AsNoTracking()
                .Where(t => t.ToUserId == toUserId && t.Resolution == DrydockTransferResolution.Pending)
                .OrderBy(t => t.ExpiresAt)
                .ToListAsync(token);

            var guids = transfers.Select(t => t.ShipGuid).ToList();
            var ships = await db.DrydockShip.AsNoTracking()
                .Where(s => guids.Contains(s.ShipGuid))
                .ToDictionaryAsync(s => s.ShipGuid, token);

            return transfers
                .Where(t => ships.ContainsKey(t.ShipGuid))
                .Select(t => (t, ships[t.ShipGuid]))
                .ToList();
        }, ct);
    }

    // ---------------------------------------------------------------- Sell, rename, move

    /// <summary>
    /// The appraisal on each of an account's ships' current revision, for the sale quote on the
    /// tab. Null for a revision filed before the column existed; such a ship cannot be sold until
    /// it has been out and stored again.
    /// </summary>
    public Task<Dictionary<Guid, int?>> GetCurrentAppraisals(Guid ownerUserId, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) => await db.DrydockShip.AsNoTracking()
            .Where(s => s.OwnerUserId == ownerUserId)
            .Join(db.DrydockRevision.AsNoTracking(),
                s => new { s.ShipGuid, Revision = s.CurrentRevision },
                r => new { r.ShipGuid, r.Revision },
                (s, r) => new { s.ShipGuid, r.AppraisedValue })
            .ToDictionaryAsync(x => x.ShipGuid, x => x.AppraisedValue, token), ct);
    }

    /// <summary>
    /// The owner scraps a stored ship. The row goes to <see cref="DrydockShipState.Sold"/> and
    /// leaves its berth; revisions and blobs stay under normal retention so an admin can undo a
    /// sale made in anger. The price was computed by the caller from the appraisal it read; both
    /// are written to the timeline so the reversal knows what to take back.
    /// </summary>
    /// <returns>The outcome, and on success the name the ship was sold under.</returns>
    public Task<(DrydockBerthResult Outcome, string? ShipName)> TrySellShip(
        Guid shipGuid,
        Guid ownerUserId,
        int price,
        int appraisal,
        int? roundId,
        CancellationToken ct = default)
    {
        return MarkSold(shipGuid, DrydockShipState.Stored, ownerUserId, ownerUserId, price, appraisal, roundId, ct);
    }

    /// <summary>
    /// The shipyard scrapped a hull that was out on a retrieve. The credits moved at the console
    /// the way any live sale's do; this is the row catching up, so the drydock does not go on
    /// listing as checked out a ship that no longer exists, offering to restore it, and finding no
    /// sale on file when the restore is from a sale. The seller is whoever held the deed card,
    /// which need not be the owner: the timeline names both.
    /// </summary>
    public Task<(DrydockBerthResult Outcome, string? ShipName)> TrySellLiveShip(
        Guid shipGuid,
        Guid sellerUserId,
        int price,
        int appraisal,
        int? roundId,
        CancellationToken ct = default)
    {
        return MarkSold(shipGuid, DrydockShipState.CheckedOut, requiredOwner: null, sellerUserId, price, appraisal, roundId, ct);
    }

    /// <summary>
    /// One conditional update from the state the sale is legal in to <see cref="DrydockShipState.Sold"/>,
    /// vacating the berth and clearing the round, so a retrieve claiming the row in the same instant
    /// is not overwritten by a sale that read it as stored. The reason keeps the price in the form
    /// <see cref="SoldForPattern"/> reads back, and says so when the hull was sold out of CheckedOut,
    /// which is a live sale at the shipyard.
    /// </summary>
    private Task<(DrydockBerthResult Outcome, string? ShipName)> MarkSold(
        Guid shipGuid,
        DrydockShipState from,
        Guid? requiredOwner,
        Guid actorUserId,
        int price,
        int appraisal,
        int? roundId,
        CancellationToken ct)
    {
        var live = from == DrydockShipState.CheckedOut;

        return _db.RunTriadDbCommand<(DrydockBerthResult, string?)>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.OwnerUserId, s.State, s.ShipName, s.BerthId, s.CurrentRevision })
                .SingleOrDefaultAsync(token);

            if (ship == null || (requiredOwner is { } owner && ship.OwnerUserId != owner))
                return (DrydockBerthResult.NotFound, null);

            if (ship.State != from)
                return (DrydockBerthResult.WrongState, null);

            var now = DateTime.UtcNow;
            var moved = await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid && s.State == from)
                .ExecuteUpdateAsync(set => set
                    .SetState(DrydockShipState.Sold, now)
                    .ClearRound()
                    .VacateBerth(), token);

            if (moved == 0)
                return (DrydockBerthResult.WrongState, null);

            AddAudit(db, DrydockAuditAction.ShipSold, now,
                shipGuid: shipGuid,
                berthId: ship.BerthId,
                shipName: ship.ShipName,
                actorUserId: actorUserId,
                subjectUserId: ship.OwnerUserId,
                revision: ship.CurrentRevision,
                roundId: roundId,
                reason: live
                    ? $"sold for {price} (appraisal {appraisal}), live at the shipyard"
                    : $"sold for {price} (appraisal {appraisal})");

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return (DrydockBerthResult.Success, ship.ShipName);
        }, ct);
    }

    /// <summary>
    /// The owner renames a stored ship. A row update only: the hull and its deed take the name
    /// the next time the ship is retrieved. The old and new names go on the timeline, and the
    /// old one stays searchable there. Conditional on the row still being stored, so a rename
    /// cannot land on a hull a retrieve is stamping with the name it read a moment earlier.
    /// </summary>
    public Task<DrydockBerthResult> TryRenameShip(
        Guid shipGuid,
        Guid ownerUserId,
        string newName,
        int? roundId,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.OwnerUserId, s.State, s.ShipName, s.BerthId, s.CurrentRevision })
                .SingleOrDefaultAsync(token);

            if (ship == null || ship.OwnerUserId != ownerUserId)
                return DrydockBerthResult.NotFound;

            if (ship.State != DrydockShipState.Stored)
                return DrydockBerthResult.WrongState;

            var now = DateTime.UtcNow;
            var moved = await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid && s.State == DrydockShipState.Stored && s.OwnerUserId == ownerUserId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.ShipName, newName)
                    .SetProperty(s => s.UpdatedAt, now), token);

            if (moved == 0)
                return DrydockBerthResult.WrongState;

            var oldName = ship.ShipName;
            AddAudit(db, DrydockAuditAction.Renamed, now,
                shipGuid: shipGuid,
                berthId: ship.BerthId,
                shipName: oldName,
                actorUserId: ownerUserId,
                subjectUserId: ownerUserId,
                revision: ship.CurrentRevision,
                roundId: roundId,
                reason: $"{oldName} -> {newName}");

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return DrydockBerthResult.Success;
        }, ct);
    }

    /// <summary>Pending offers an account has made, keyed by ship: what the owner's berth rows say about escrow.</summary>
    public Task<Dictionary<Guid, DrydockTransfer>> GetPendingOffersFrom(Guid fromUserId, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) => await db.DrydockTransfer.AsNoTracking()
            .Where(t => t.FromUserId == fromUserId && t.Resolution == DrydockTransferResolution.Pending)
            .ToDictionaryAsync(t => t.ShipGuid, token), ct);
    }

    /// <summary>
    /// The classes of every free berth each of these accounts owns, in one query, so the transfer
    /// picker can say who has room without a round trip per online player.
    /// </summary>
    public Task<Dictionary<Guid, List<string>>> GetFreeBerthClasses(IEnumerable<Guid> owners, CancellationToken ct = default)
    {
        var ids = owners.Distinct().ToList();
        if (ids.Count == 0)
            return Task.FromResult(new Dictionary<Guid, List<string>>());

        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var free = await db.DrydockBerth.AsNoTracking()
                .Where(b => ids.Contains(b.OwnerUserId) && !db.DrydockShip.Any(s => s.BerthId == b.BerthId))
                .Select(b => new { b.OwnerUserId, b.MaxSizeClass })
                .ToListAsync(token);

            return free.GroupBy(b => b.OwnerUserId).ToDictionary(g => g.Key, g => g.Select(b => b.MaxSizeClass).ToList());
        }, ct);
    }

    /// <summary>
    /// A filtered page of hulls for the admin panel, newest activity first. The owner is joined only
    /// inside the filters; <see cref="DrydockShip.Owner"/> is not loaded on the rows, whose names the
    /// panel resolves itself.
    /// </summary>
    public Task<(List<DrydockShip> Rows, int Total)> QueryShips(DrydockShipFilter filter, int page, int pageSize, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var query = db.DrydockShip.AsNoTracking();

            if (filter.OwnerUserId is { } owner)
                query = query.Where(s => s.OwnerUserId == owner);
            else if (!string.IsNullOrWhiteSpace(filter.OwnerNameContains))
            {
                var needle = filter.OwnerNameContains.ToLowerInvariant();
                query = query.Where(s => s.Owner.LastSeenUserName.ToLower().Contains(needle));
            }

            if (!string.IsNullOrWhiteSpace(filter.ShipNameContains))
            {
                var needle = filter.ShipNameContains.ToLowerInvariant();
                query = query.Where(s => s.ShipName.ToLower().Contains(needle));
            }

            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var search = filter.Search.Trim();
                if (Guid.TryParse(search, out var id))
                {
                    query = query.Where(s => s.ShipGuid == id || s.OwnerUserId == id);
                }
                else
                {
                    // Past names live on the audit rows as snapshots, so a ship renamed to hide
                    // is still found under the name the complaint was filed with. A character name
                    // in any of the owner's slots finds the account, because an AHelp names the
                    // character rather than the account.
                    var needle = search.ToLowerInvariant();
                    query = query.Where(s => s.ShipName.ToLower().Contains(needle)
                        || s.Owner.LastSeenUserName.ToLower().Contains(needle)
                        || db.Profile.Any(p => p.Preference.UserId == s.OwnerUserId && p.CharacterName.ToLower().Contains(needle))
                        || db.DrydockAudit.Any(a => a.ShipGuid == s.ShipGuid && a.ShipName != null && a.ShipName.ToLower().Contains(needle)));
                }
            }

            if (filter.State is { } state)
                query = query.Where(s => s.State == state);

            // Checked out in a round that is over, or in no round at all: the adjudication list.
            if (filter.StrandedOnly)
            {
                var round = filter.CurrentRoundId;
                query = query.Where(s => s.State == DrydockShipState.CheckedOut
                    && (s.CheckedOutRoundId == null || s.CheckedOutRoundId != round));
            }

            var total = await query.CountAsync(token);
            var rows = await query
                .OrderByDescending(s => s.UpdatedAt)
                .Skip(Math.Max(0, page) * pageSize)
                .Take(pageSize)
                .ToListAsync(token);

            return (rows, total);
        }, ct);
    }

    /// <summary>
    /// A page of the drydock registry, newest activity first. The search matches the recorded
    /// captain and the ship's current name, callsign included, and never the account, which is not
    /// something a dock clerk knows.
    /// </summary>
    public Task<(List<DrydockShip> Rows, int Total)> QueryRegistry(string? search, DrydockShipState[]? states, int page, int pageSize, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var query = db.DrydockShip.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var needle = search.Trim().ToLowerInvariant();
                query = query.Where(s => s.ShipName.ToLower().Contains(needle)
                    || s.CaptainName != null && s.CaptainName.ToLower().Contains(needle));
            }

            if (states is { Length: > 0 })
                query = query.Where(s => states.Contains(s.State));

            var total = await query.CountAsync(token);
            var rows = await query
                .OrderByDescending(s => s.UpdatedAt)
                .Skip(Math.Max(0, page) * pageSize)
                .Take(pageSize)
                .ToListAsync(token);

            return (rows, total);
        }, ct);
    }

    /// <summary>
    /// Records the character who is the hull's captain for the registry. Display only, so it moves
    /// nothing else on the row, not even its activity stamp.
    /// </summary>
    public Task SetCaptainName(Guid shipGuid, string captainName, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.CaptainName, captainName), token);
            return true;
        }, ct);
    }

    /// <summary>
    /// One hull with its whole history and timeline, for the admin panel's detail view. The history
    /// is the scalar columns the panel draws, never the manifest, which is the one large column on a
    /// revision and is re-read on every refresh otherwise.
    /// </summary>
    public Task<DrydockShipDetail?> GetShipDetail(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<DrydockShipDetail?>(async (db, token) =>
        {
            var ship = await db.DrydockShip.AsNoTracking()
                .SingleOrDefaultAsync(s => s.ShipGuid == shipGuid, token);

            if (ship == null)
                return null;

            var revisions = await db.DrydockRevision.AsNoTracking()
                .Where(r => r.ShipGuid == shipGuid)
                .OrderByDescending(r => r.Revision)
                .Select(r => new DrydockRevisionSummary(
                    r.Revision,
                    r.Kind,
                    r.CreatedAt,
                    r.CreatedRoundId,
                    r.ActorUserId,
                    r.SizeBytes,
                    r.DerivedFromRevision,
                    r.AppraisedValue))
                .ToListAsync(token);

            var withBlob = await db.DrydockBlob.AsNoTracking()
                .Where(b => b.ShipGuid == shipGuid)
                .Select(b => b.Revision)
                .ToListAsync(token);

            var timeline = await db.DrydockAudit.AsNoTracking()
                .Where(a => a.ShipGuid == shipGuid)
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync(token);

            return new DrydockShipDetail(ship, revisions, withBlob.ToHashSet(), timeline);
        }, ct);
    }

    /// <summary>Display names for a set of players, from the player table. Online sessions are the caller's to prefer.</summary>
    public Task<Dictionary<Guid, string>> GetPlayerNames(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0)
            return Task.FromResult(new Dictionary<Guid, string>());

        return _db.RunTriadDbCommand(async (db, token) => await db.Player.AsNoTracking()
            .Where(p => ids.Contains(p.UserId))
            .Select(p => new { p.UserId, p.LastSeenUserName })
            .ToDictionaryAsync(p => p.UserId, p => p.LastSeenUserName, token), ct);
    }

    /// <summary>Admin scratch notes on a hull. Not on the timeline: the timeline is for decisions.</summary>
    public Task<bool> SetAdminNotes(Guid shipGuid, string? notes, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var moved = await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.AdminNotes, notes)
                    .SetProperty(s => s.UpdatedAt, DateTime.UtcNow), token);

            return moved > 0;
        }, ct);
    }

    /// <summary>
    /// Admin: promotes an older revision to current by filing it again as a new one, kind
    /// AdminRestore, derived from the original. History stays append-only; the promoted document
    /// is copied, never moved, along with the source's appraisal, and the usual pruning
    /// (<see cref="PruneBlobs"/>) runs around the new revision.
    /// </summary>
    public Task<(DrydockBerthResult Outcome, int Revision)> TryPromoteRevision(
        Guid shipGuid,
        int revision,
        Guid? actorUserId,
        int? roundId,
        string? reason,
        int keepBlobs,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand<(DrydockBerthResult, int)>(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.SingleOrDefaultAsync(s => s.ShipGuid == shipGuid, token);
            if (ship == null)
                return (DrydockBerthResult.NotFound, 0);

            var source = await db.DrydockRevision.AsNoTracking()
                .SingleOrDefaultAsync(r => r.ShipGuid == shipGuid && r.Revision == revision, token);

            // Refused before the blob read, so a revision that never existed costs no document.
            if (source == null)
                return (DrydockBerthResult.NotFound, 0);

            var blob = await db.DrydockBlob.AsNoTracking()
                .SingleOrDefaultAsync(b => b.ShipGuid == shipGuid && b.Revision == revision, token);

            // History without a document cannot be promoted; that is what pruning took.
            if (blob == null)
                return (DrydockBerthResult.NotFound, 0);

            var now = DateTime.UtcNow;
            var next = ship.CurrentRevision + 1;

            db.DrydockRevision.Add(new DrydockRevision
            {
                ShipGuid = shipGuid,
                Revision = next,
                Kind = DrydockRevisionKind.AdminRestore,
                DerivedFromRevision = revision,
                RebakeVersion = source.RebakeVersion,
                ActorUserId = actorUserId,
                CreatedRoundId = roundId,
                CreatedAt = now,
                EngineFormatVer = source.EngineFormatVer,
                DrydockFormatVer = source.DrydockFormatVer,
                ProtoFingerprint = source.ProtoFingerprint,
                CapturedKeyHash = source.CapturedKeyHash,
                Checksum = source.Checksum,
                SizeBytes = source.SizeBytes,
                // The appraisal rides with the document: it is what a sale quotes and an impound
                // charges against, and a promoted current revision without one sells for nothing.
                AppraisedValue = source.AppraisedValue,
                Manifest = source.Manifest,
            });

            db.DrydockBlob.Add(new DrydockBlob
            {
                ShipGuid = shipGuid,
                Revision = next,
                Blob = blob.Blob,
            });

            ship.CurrentRevision = next;
            ship.UpdatedAt = now;

            await PruneBlobs(db, shipGuid, next, keepBlobs, token);

            AddAudit(db, DrydockAuditAction.RevisionPromoted, now,
                shipGuid: shipGuid,
                berthId: ship.BerthId,
                shipName: ship.ShipName,
                actorUserId: actorUserId,
                subjectUserId: ship.OwnerUserId,
                revision: next,
                roundId: roundId,
                reason: string.IsNullOrWhiteSpace(reason) ? $"promoted revision {revision}" : $"promoted revision {revision}: {reason}");

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return (DrydockBerthResult.Success, next);
        }, ct);
    }

    /// <summary>
    /// Excludes a revision's document from pruning, whatever keep-N and the floor say, until
    /// <see cref="TryUnpinRevision"/> clears it. Meant for a checksum-valid document a retrieve had to
    /// step past, so that ordinary stores after a fallback cannot prune the newest state the player
    /// ever filed; also an admin's to set by hand. Refuses a revision whose document is already gone,
    /// since there is nothing left to protect.
    ///
    /// <para>One conditional update on the flag as it was read (and, for a pin, on the document still
    /// existing) plus a <see cref="DrydockAuditAction.RevisionPinned"/> row, in one transaction. The
    /// ship row is locked first, so the pin serializes with any prune of the same ship; see
    /// <see cref="PruneBlobs"/>.</para>
    /// </summary>
    /// <param name="actorUserId">Who pinned it; null for the system.</param>
    /// <param name="reason">Why, for the timeline. Appended to "pinned revision N" when given.</param>
    public Task<DrydockPinResult> TryPinRevision(
        Guid shipGuid,
        int revision,
        Guid? actorUserId,
        int? roundId,
        string? reason,
        CancellationToken ct = default)
    {
        return SetRevisionPinned(shipGuid, revision, pinned: true, actorUserId, roundId, reason, ct);
    }

    /// <summary>
    /// Clears a pin, handing the document back to ordinary retention: the next store, promote or
    /// re-bake prunes it if keep-N and the floor no longer cover it. Works on a revision whose document
    /// is already gone, so a stale flag can always be cleared. One conditional update plus a
    /// <see cref="DrydockAuditAction.RevisionUnpinned"/> row, in one transaction.
    /// </summary>
    /// <param name="actorUserId">Who unpinned it; null for the system.</param>
    /// <param name="reason">Why, for the timeline. Appended to "unpinned revision N" when given.</param>
    public Task<DrydockPinResult> TryUnpinRevision(
        Guid shipGuid,
        int revision,
        Guid? actorUserId,
        int? roundId,
        string? reason,
        CancellationToken ct = default)
    {
        return SetRevisionPinned(shipGuid, revision, pinned: false, actorUserId, roundId, reason, ct);
    }

    private Task<DrydockPinResult> SetRevisionPinned(
        Guid shipGuid,
        int revision,
        bool pinned,
        Guid? actorUserId,
        int? roundId,
        string? reason,
        CancellationToken ct)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            // The ship row before the revision row, the order a prune takes them in.
            if (await LockShipRow(db, shipGuid, token) == 0)
                return DrydockPinResult.NotFound;

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.ShipName, s.BerthId, s.OwnerUserId })
                .SingleAsync(token);

            var query = db.DrydockRevision
                .Where(r => r.ShipGuid == shipGuid && r.Revision == revision && r.Pinned != pinned);

            if (pinned)
                query = query.Where(r => db.DrydockBlob.Any(b => b.ShipGuid == r.ShipGuid && b.Revision == r.Revision));

            var moved = await query.ExecuteUpdateAsync(set => set.SetProperty(r => r.Pinned, pinned), token);

            if (moved == 0)
            {
                // Classification only; nothing is written on this branch.
                var current = await db.DrydockRevision.AsNoTracking()
                    .Where(r => r.ShipGuid == shipGuid && r.Revision == revision)
                    .Select(r => (bool?) r.Pinned)
                    .SingleOrDefaultAsync(token);

                return current == pinned ? DrydockPinResult.AlreadyInState : DrydockPinResult.NotFound;
            }

            var verb = pinned ? "pinned" : "unpinned";
            AddAudit(db, pinned ? DrydockAuditAction.RevisionPinned : DrydockAuditAction.RevisionUnpinned, DateTime.UtcNow,
                shipGuid: shipGuid,
                berthId: ship.BerthId,
                shipName: ship.ShipName,
                actorUserId: actorUserId,
                subjectUserId: ship.OwnerUserId,
                revision: revision,
                roundId: roundId,
                reason: string.IsNullOrWhiteSpace(reason) ? $"{verb} revision {revision}" : $"{verb} revision {revision}: {reason}");

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return DrydockPinResult.Success;
        }, ct);
    }

    /// <summary>
    /// The revisions of a ship that still hold a document, newest first, whether retention or a pin
    /// kept them. Revision numbers only, read off the blob table's primary key without touching the
    /// document bytes. This is the honest lower bound for a retrieve's fallback walk: counting down
    /// <c>keepBlobs</c> from the current revision misses pinned documents below the window, and walks
    /// revisions whose documents are already gone. Empty for an unknown ship.
    /// </summary>
    public Task<List<int>> ListRetrievableRevisions(Guid shipGuid, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) => await db.DrydockBlob.AsNoTracking()
            .Where(b => b.ShipGuid == shipGuid)
            .OrderByDescending(b => b.Revision)
            .Select(b => b.Revision)
            .ToListAsync(token), ct);
    }

    /// <summary>
    /// Files a system re-bake: a new <see cref="DrydockRevisionKind.SystemRebake"/> revision derived
    /// from <see cref="DrydockRebakeRequest.SourceRevision"/>, carrying the re-baked document, with a
    /// null actor and a null round, and the source's appraisal copied forward since a stored hull has
    /// nothing left to appraise. It becomes current only if the ship is still
    /// <see cref="DrydockShipState.Stored"/> and still on the source revision.
    ///
    /// <para>That condition is one <c>ExecuteUpdate</c> that also advances the pointer, so a player
    /// store, a promote or a retrieve's claim landing between the worker's read and this write cannot
    /// be overwritten by a document derived from what the ship used to be. When it matches nothing the
    /// transaction is rolled back before the revision or its blob is added, and the outcome says
    /// which condition failed. The revision row, the blob, the prune around it and a
    /// <see cref="DrydockAuditAction.Rebake"/> row naming the source share the transaction.</para>
    ///
    /// <para>Not <see cref="FileRevision"/> with a different kind. That path reads the pointer off a
    /// tracked row and relies on the primary key to fail a race, which would let a re-bake land on a
    /// ship that moved underneath it as long as the numbers did not collide; it refreshes the display
    /// cache from the request, which a re-bake has no business setting; and it seats berths and carries
    /// impound terms that mean nothing here. The shared parts are the audit writer and the prune.</para>
    /// </summary>
    /// <param name="keepBlobs">As for <see cref="FileRevision"/>; see <see cref="PruneBlobs"/>.</param>
    public Task<DrydockRebakeFileResult> FileRebakeRevision(DrydockRebakeRequest request, byte[] blob, int keepBlobs, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var shipGuid = request.ShipGuid;
            var sourceRevision = request.SourceRevision;

            var source = await db.DrydockRevision.AsNoTracking()
                .Where(r => r.ShipGuid == shipGuid && r.Revision == sourceRevision)
                .Select(r => new { r.AppraisedValue })
                .SingleOrDefaultAsync(token);

            if (source == null)
                return new DrydockRebakeFileResult(DrydockRebakeResult.NotFound, 0);

            var now = DateTime.UtcNow;
            var next = sourceRevision + 1;

            var moved = await db.DrydockShip
                .Where(s => s.ShipGuid == shipGuid
                    && s.State == DrydockShipState.Stored
                    && s.CurrentRevision == sourceRevision)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.CurrentRevision, next)
                    .SetProperty(s => s.UpdatedAt, now), token);

            if (moved == 0)
            {
                // Classification only. The transaction is disposed uncommitted, so nothing lands.
                var state = await db.DrydockShip.AsNoTracking()
                    .Where(s => s.ShipGuid == shipGuid)
                    .Select(s => (DrydockShipState?) s.State)
                    .SingleOrDefaultAsync(token);

                var outcome = state switch
                {
                    null => DrydockRebakeResult.NotFound,
                    not DrydockShipState.Stored => DrydockRebakeResult.WrongState,
                    _ => DrydockRebakeResult.StaleSource,
                };

                return new DrydockRebakeFileResult(outcome, 0);
            }

            // Read after the update, so the snapshot is the row this transaction now holds.
            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.ShipName, s.BerthId, s.OwnerUserId })
                .SingleAsync(token);

            db.DrydockRevision.Add(new DrydockRevision
            {
                ShipGuid = shipGuid,
                Revision = next,
                Kind = DrydockRevisionKind.SystemRebake,
                DerivedFromRevision = sourceRevision,
                RebakeVersion = request.RebakeVersion,
                ActorUserId = null,
                CreatedRoundId = null,
                CreatedAt = now,
                EngineFormatVer = request.EngineFormatVer,
                DrydockFormatVer = request.DrydockFormatVer,
                ProtoFingerprint = request.ProtoFingerprint,
                CapturedKeyHash = request.CapturedKeyHash,
                Checksum = request.Checksum,
                SizeBytes = request.SizeBytes,
                AppraisedValue = source.AppraisedValue,
                Manifest = request.Manifest,
            });

            db.DrydockBlob.Add(new DrydockBlob
            {
                ShipGuid = shipGuid,
                Revision = next,
                Blob = blob,
            });

            await PruneBlobs(db, shipGuid, next, keepBlobs, token);

            AddAudit(db, DrydockAuditAction.Rebake, now,
                shipGuid: shipGuid,
                berthId: ship.BerthId,
                shipName: ship.ShipName,
                subjectUserId: ship.OwnerUserId,
                revision: next,
                reason: $"re-baked revision {sourceRevision} at ladder version {request.RebakeVersion}");

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return new DrydockRebakeFileResult(DrydockRebakeResult.Success, next);
        }, ct);
    }

    /// <summary>
    /// One page of the ships a re-bake sweep may look at: every <see cref="DrydockShipState.Stored"/>
    /// hull with its current revision's number and drydock format, and no document bytes. Keyset paged
    /// on the ship id rather than offset paged, so a ship that leaves storage or gets re-baked while the
    /// sweep is between pages neither shifts a later ship out of the walk nor brings an earlier one
    /// back into it.
    ///
    /// <para>Checked out, impounded, in escrow and terminal hulls are not listed, and the filing refuses
    /// them anyway. The order is the provider's own ordering of the id column, which is all a cursor
    /// needs: it only has to agree with itself.</para>
    /// </summary>
    /// <param name="after">The last ship id of the previous page, or null for the first.</param>
    /// <param name="pageSize">How many rows at most.</param>
    public Task<List<DrydockRebakeCandidate>> GetRebakeCandidates(Guid? after, int pageSize, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            var ships = db.DrydockShip.AsNoTracking().Where(s => s.State == DrydockShipState.Stored);
            if (after is { } cursor)
                ships = ships.Where(s => s.ShipGuid.CompareTo(cursor) > 0);

            var rows = await ships
                .Join(db.DrydockRevision.AsNoTracking(),
                    s => new { s.ShipGuid, Revision = s.CurrentRevision },
                    r => new { r.ShipGuid, r.Revision },
                    (s, r) => new { s.ShipGuid, s.ShipName, s.CurrentRevision, r.DrydockFormatVer })
                .OrderBy(c => c.ShipGuid)
                .Take(pageSize)
                .ToListAsync(token);

            return rows.Select(c => new DrydockRebakeCandidate(c.ShipGuid, c.ShipName, c.CurrentRevision, c.DrydockFormatVer)).ToList();
        }, ct);
    }

    /// <summary>
    /// Admin: deletes a hull and, by cascade, its revisions and blobs. The timeline row is written
    /// first and has no foreign key, so the evidence of the deletion outlives the thing deleted.
    /// The berth the hull sat in is left empty rather than removed.
    /// </summary>
    public Task<DrydockBerthResult> TryDeleteShip(Guid shipGuid, Guid? actorUserId, int? roundId, string? reason, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.SingleOrDefaultAsync(s => s.ShipGuid == shipGuid, token);
            if (ship == null)
                return DrydockBerthResult.NotFound;

            AddAudit(db, DrydockAuditAction.Delete, DateTime.UtcNow,
                shipGuid: shipGuid,
                berthId: ship.BerthId,
                shipName: ship.ShipName,
                actorUserId: actorUserId,
                subjectUserId: ship.OwnerUserId,
                revision: ship.CurrentRevision,
                roundId: roundId,
                reason: reason);

            db.DrydockShip.Remove(ship);

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return DrydockBerthResult.Success;
        }, ct);
    }

    /// <summary>
    /// Admin: returns a ship that is out, impounded or terminal to the drydock, into a named berth.
    /// Whether the ship is really lost is the admin's call. The one thing this cannot know is
    /// whether a live grid still carries the id, and the system checks that before calling.
    ///
    /// <para>Out of the impound lot this is a release into a chosen berth, and the timeline says
    /// so: the row is <see cref="DrydockAuditAction.ImpoundReleased"/>, the same as the release that
    /// picks the berth itself, and never "restored", which is the word for a hull judged lost. Out
    /// of an abandon it is the admin's undo of the owner's decision, and the row is
    /// <see cref="DrydockAuditAction.AbandonReversed"/> for the same reason.</para>
    ///
    /// <para>A sold hull comes back only through the sale reversal, which decides about the money
    /// before anything else; a plain restore would hand it back with the price left with the
    /// owner. A hull in escrow is spoken for: seating it under a standing offer leaves the offer
    /// resolving against a row that already moved. The move is one conditional update on the state
    /// the row was read in.</para>
    /// </summary>
    /// <param name="fromSale">Set by the sale reversal, which is the one caller allowed to restore a sold hull.</param>
    public Task<DrydockBerthResult> TryRestoreShip(
        Guid shipGuid,
        int berthId,
        Guid? actorUserId,
        int? roundId,
        string reason,
        bool fromSale = false,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            var ship = await db.DrydockShip.AsNoTracking()
                .Where(s => s.ShipGuid == shipGuid)
                .Select(s => new { s.OwnerUserId, s.State, s.SizeClass, s.ShipName, s.CurrentRevision })
                .SingleOrDefaultAsync(token);

            if (ship == null)
                return DrydockBerthResult.NotFound;

            // The panel hides the verb on both; this is what stops a forged message.
            if (ship.State is DrydockShipState.Stored or DrydockShipState.InEscrow)
                return DrydockBerthResult.WrongState;

            if (ship.State == DrydockShipState.Sold && !fromSale)
                return DrydockBerthResult.WrongState;

            var (fit, _) = await ResolveBerth(db, shipGuid, ship.OwnerUserId, ship.SizeClass, berthId, null, token);
            if (fit != DrydockBerthResult.Success)
                return fit;

            var now = DateTime.UtcNow;
            var from = ship.State;
            var action = from switch
            {
                DrydockShipState.Impounded => DrydockAuditAction.ImpoundReleased,
                DrydockShipState.Abandoned => DrydockAuditAction.AbandonReversed,
                _ => DrydockAuditAction.Restore,
            };

            int moved;
            try
            {
                moved = await SeatFromState(db, s => s.ShipGuid == shipGuid && s.State == from, berthId, now, token);
            }
            catch (Exception e) when (IsBerthUniqueViolation(e))
            {
                return DrydockBerthResult.BerthOccupied;
            }

            if (moved == 0)
                return DrydockBerthResult.WrongState;

            AddAudit(db, action, now,
                shipGuid: shipGuid,
                berthId: berthId,
                shipName: ship.ShipName,
                actorUserId: actorUserId,
                subjectUserId: ship.OwnerUserId,
                revision: ship.CurrentRevision,
                roundId: roundId,
                reason: reason);

            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);

            return DrydockBerthResult.Success;
        }, ct);
    }
}

/// <summary>
/// Everything a single <see cref="DrydockStore.FileRevision"/> needs. A record rather than a long
/// parameter list because every field here is a column, and a positional argument that silently
/// swaps two hashes is not a mistake worth being able to make.
/// </summary>
public sealed class DrydockRevisionRequest
{
    public required Guid ShipGuid { get; init; }

    /// <summary>Only used when creating the hull row. An existing ship keeps its owner.</summary>
    public required Guid OwnerUserId { get; init; }

    public required string ShipName { get; init; }

    public string? VesselProto { get; init; }

    public string? SizeClass { get; init; }

    /// <summary>
    /// Name the berth rather than letting the store pick one. The console's store passes the berth
    /// the player chose, or null to have one picked, and the shipyard import passes the berth it just
    /// granted for the hull; an impound passes null and vacates instead. It still has to be the
    /// owner's, free, and large enough, and the store checks all three.
    /// </summary>
    public int? BerthId { get; init; }

    /// <summary>
    /// Mark the ship stored in the same transaction that files the revision. The pipeline leaves
    /// this false and calls <see cref="DrydockStore.MarkStored"/> once the grid is despawned, so
    /// a store that is refused after the write never leaves a retrievable row behind a live ship.
    /// Callers with no live grid, such as tests filing documents directly, set it.
    /// </summary>
    public bool MarkStored { get; init; }

    /// <summary>
    /// A player store or an import. <see cref="DrydockRevisionKind.SystemRebake"/> is refused: a
    /// re-bake goes through <see cref="DrydockRebakeRequest"/>, which is why this request carries no
    /// provenance fields.
    /// </summary>
    public required DrydockRevisionKind Kind { get; init; }

    /// <summary>Null for the system, which is how the round-end sweep's impound files.</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>Null when no round is running.</summary>
    public int? CreatedRoundId { get; init; }

    public required int EngineFormatVer { get; init; }

    public int DrydockFormatVer { get; init; } = DrydockFormat.Current;

    public required byte[] ProtoFingerprint { get; init; }

    public required byte[] CapturedKeyHash { get; init; }

    public required byte[] Checksum { get; init; }

    public required int SizeBytes { get; init; }

    /// <summary>The shipyard's appraisal of the live hull, so a sale of the stored ship has a price. Null when nothing appraised it.</summary>
    public int? AppraisedValue { get; init; }

    public required string Manifest { get; init; }

    /// <summary>
    /// Set to file the hull into the impound lot rather than into a berth. The berth is left empty
    /// and <see cref="DrydockShip.LastBerthId"/> keeps whichever one the hull came from, because
    /// redemption needs a free berth and a hull still sitting in its own would satisfy that for
    /// nothing. Null files the ordinary way.
    /// </summary>
    public DrydockImpound? Impound { get; init; }

    /// <summary>
    /// Impound only: how many occupants the pipeline moved off the hull before filing it. Not a
    /// column; it goes into the audit row's reason, so the timeline says the hull was taken with
    /// people on it.
    /// </summary>
    public int Evicted { get; init; }
}

/// <summary>
/// Everything <see cref="DrydockStore.FileRebakeRevision"/> needs from the re-bake worker: the
/// revision the new document was derived from and the new document's own columns. What a re-bake
/// does not know (who, which round, what the hull appraised at, which berth) is not asked for: the
/// actor and round are null by definition and the appraisal is copied from the source revision.
/// </summary>
public sealed class DrydockRebakeRequest
{
    public required Guid ShipGuid { get; init; }

    /// <summary>
    /// The revision the document was re-baked from. The filing advances the pointer only while the
    /// ship's current revision is still this one.
    /// </summary>
    public required int SourceRevision { get; init; }

    /// <summary>Which generation of the re-bake ladder produced the document.</summary>
    public required int RebakeVersion { get; init; }

    public required int EngineFormatVer { get; init; }

    public int DrydockFormatVer { get; init; } = DrydockFormat.Current;

    public required byte[] ProtoFingerprint { get; init; }

    public required byte[] CapturedKeyHash { get; init; }

    /// <summary>Over the uncompressed re-baked document, the same as a store's.</summary>
    public required byte[] Checksum { get; init; }

    public required int SizeBytes { get; init; }

    public required string Manifest { get; init; }
}

/// <summary>
/// A stored ship as the re-bake sweep first sees it, from <see cref="DrydockStore.GetRebakeCandidates"/>:
/// enough to log and to find the document, and nothing that costs a blob read.
/// </summary>
public sealed record DrydockRebakeCandidate(Guid ShipGuid, string ShipName, int CurrentRevision, int DrydockFormatVer);

/// <summary>What a retrieve reads: the hull row, the revision it is about to rebuild, and the document.</summary>
public sealed record DrydockLoad(DrydockShip Ship, DrydockRevision Revision, byte[] Blob);

/// <summary>
/// The <c>State</c> / <c>StateChangedAt</c> / <c>UpdatedAt</c> triple almost every state move in
/// <see cref="DrydockStore"/> sets together, factored so each call site chains it with whatever
/// else that move touches. EF composes <see cref="UpdateSettersBuilder{TSource}"/> into one SQL
/// SET clause; every assignment reads the row's pre-update values regardless of chain order, so
/// where this sits among a caller's other <c>SetProperty</c> calls changes nothing about what
/// lands.
/// </summary>
internal static class DrydockShipUpdateExtensions
{
    public static UpdateSettersBuilder<DrydockShip> SetState(
        this UpdateSettersBuilder<DrydockShip> set,
        DrydockShipState state,
        DateTime now)
    {
        return set
            .SetProperty(s => s.State, state)
            .SetProperty(s => s.StateChangedAt, now)
            .SetProperty(s => s.UpdatedAt, now);
    }

    /// <summary>
    /// Empties the berth and remembers it, keeping the previous memory when the row held no berth,
    /// so a move out of a state that had already vacated never overwrites where the hull last sat.
    /// </summary>
    public static UpdateSettersBuilder<DrydockShip> VacateBerth(this UpdateSettersBuilder<DrydockShip> set)
    {
        return set
            .SetProperty(s => s.LastBerthId, s => s.BerthId ?? s.LastBerthId)
            .SetProperty(s => s.BerthId, (int?)null);
    }

    /// <summary>Clears the round a checkout recorded, for a move that leaves the hull anywhere but out.</summary>
    public static UpdateSettersBuilder<DrydockShip> ClearRound(this UpdateSettersBuilder<DrydockShip> set)
    {
        return set.SetProperty(s => s.CheckedOutRoundId, (int?)null);
    }
}

/// <summary>The admin panel's list filter. Every field null or false means "any".</summary>
public sealed record DrydockShipFilter(
    Guid? OwnerUserId,
    string? OwnerNameContains,
    string? ShipNameContains,
    DrydockShipState? State,
    bool StrandedOnly,
    int? CurrentRoundId,
    // One box from the admin panel: a ship id or account id when it parses as one, else text
    // matched against the owner's account name, any of the owner's character names, the ship's
    // name, and every name the ship has had.
    string? Search = null);

/// <summary>One hull with its history and timeline, newest first, and which revisions still have a document.</summary>
public sealed record DrydockShipDetail(
    DrydockShip Ship,
    List<DrydockRevisionSummary> Revisions,
    HashSet<int> RevisionsWithBlob,
    List<DrydockAudit> Timeline);

/// <summary>One row of a hull's history as the admin panel draws it: a revision's columns without its manifest.</summary>
public sealed record DrydockRevisionSummary(
    int Revision,
    DrydockRevisionKind Kind,
    DateTime CreatedAt,
    int? CreatedRoundId,
    Guid? ActorUserId,
    int SizeBytes,
    int? DerivedFromRevision,
    int? AppraisedValue);
