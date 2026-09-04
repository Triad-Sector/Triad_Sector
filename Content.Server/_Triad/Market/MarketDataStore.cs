using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.IoC;

namespace Content.Server._Triad.Market;

/// <summary>
/// Every database query the market data system owns. It goes through
/// <see cref="IServerDbManager.RunTriadDbCommand{T}"/> rather than adding methods to
/// <see cref="IServerDbManager"/> itself, which is the seam this fork already uses for
/// feature-owned stores: these queries make no sense in core, and keeping them out of an upstream
/// file means an upstream pull conflicts on nothing.
/// </summary>
public sealed class MarketDataStore
{
    [Dependency] private readonly IServerDbManager _db = default!;

    /// <summary>
    /// Writes a batch of captured transactions, with their splits and line trees.
    ///
    /// <para>One transaction for the whole batch. A batch is telemetry, so a partial write is worse
    /// than none: half a pallet sale's splits landing without its header would read as sector income
    /// from nowhere.</para>
    ///
    /// <para>Line trees insert in a single pass because the tree is expressed in transaction-local
    /// indices rather than generated ids. Nothing here round-trips for a key.</para>
    /// </summary>
    /// <param name="stampRoundId">
    /// Round to stamp on records that were captured before a round existed. Records carrying their
    /// own round keep it.
    /// </param>
    /// <returns>How many transaction rows were written.</returns>
    public Task<int> WriteBatch(IReadOnlyList<PendingMarketRecord> batch, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            if (batch.Count == 0)
                return 0;

            await using var tx = await db.Database.BeginTransactionAsync(token);

            foreach (var pending in batch)
            {
                var record = pending.Record;

                var entity = new MarketTransaction
                {
                    RoundId = pending.RoundId,
                    OccurredAt = pending.OccurredAt,
                    Kind = record.Kind,
                    LedgerEntryType = record.LedgerEntryType,
                    ActorUserId = record.ActorUserId,
                    Currency = record.Currency,
                    Rail = record.Rail,
                    Gross = record.Gross,
                    Tax = record.Tax,
                    Net = record.Net,
                    ListPrice = record.ListPrice,
                    Succeeded = record.Succeeded,
                    FailReason = record.FailReason,
                    LocationName = record.LocationName,
                    ConsoleProto = record.ConsoleProto,
                    MarketMod = record.MarketMod,
                    ShipGuid = record.ShipGuid,
                    Calc = record.Calc,
                };

                foreach (var split in record.Splits)
                {
                    entity.Splits.Add(new MarketTransactionSplit
                    {
                        Account = split.Account,
                        EntryType = split.EntryType,
                        Amount = split.Amount,
                    });
                }

                foreach (var line in record.Lines)
                {
                    entity.Lines.Add(new MarketTransactionLine
                    {
                        // Denormalized from the header. Every pricing query starts from a line and
                        // filters by time; this is the one join worth not making.
                        OccurredAt = pending.OccurredAt,
                        LineIndex = line.LineIndex,
                        ParentLineIndex = line.ParentLineIndex,
                        EntityProto = line.EntityProto,
                        Direction = line.Direction,
                        Quantity = line.Quantity,
                        UnitPrice = line.UnitPrice,
                        LineTotal = line.LineTotal,
                        Multiplier = line.Multiplier,
                        PriceSource = line.PriceSource,
                    });
                }

                db.MarketTransaction.Add(entity);
            }

            var written = await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return batch.Count;
        }, ct);
    }

    /// <summary>
    /// Records which characters a player used in a round. Written once per character rather than on
    /// every transaction, which is the whole point of the table.
    /// </summary>
    public Task RecordParticipants(int roundId, IReadOnlyList<(Guid UserId, string CharacterName)> participants,
        CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            if (participants.Count == 0)
                return;

            var existing = await db.MarketRoundParticipant
                .Where(p => p.RoundId == roundId)
                .Select(p => new { p.UserId, p.CharacterName })
                .ToListAsync(token);

            var seen = existing.Select(e => (e.UserId, e.CharacterName)).ToHashSet();

            foreach (var (userId, name) in participants)
            {
                if (!seen.Add((userId, name)))
                    continue;

                db.MarketRoundParticipant.Add(new MarketRoundParticipant
                {
                    RoundId = roundId,
                    UserId = userId,
                    CharacterName = name,
                });
            }

            await db.SaveChangesAsync(token);
        }, ct);
    }

    /// <summary>
    /// Writes a periodic sector account balance sample. This is what replaces recording ticking
    /// income as transactions: five accounts on a ten second interval is about forty percent of the
    /// whole corpus, and it is reconstructible from the account's own rate.
    /// </summary>
    public Task WriteAccountSamples(int roundId, DateTime sampledAt,
        IReadOnlyList<(string Account, long Balance)> samples, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
        {
            if (samples.Count == 0)
                return;

            foreach (var (account, balance) in samples)
            {
                db.SectorAccountSample.Add(new SectorAccountSample
                {
                    RoundId = roundId,
                    SampledAt = sampledAt,
                    Account = account,
                    Balance = balance,
                });
            }

            await db.SaveChangesAsync(token);
        }, ct);
    }

    /// <summary>
    /// Deletes raw transactions older than the retention window. Splits and lines go with them by
    /// cascade. The price rollup is permanent and is not touched here.
    /// </summary>
    /// <returns>How many transaction rows were removed.</returns>
    public Task<int> PurgeOlderThan(DateTime cutoff, CancellationToken ct = default)
    {
        return _db.RunTriadDbCommand(async (db, token) =>
            await db.MarketTransaction
                .Where(t => t.OccurredAt < cutoff)
                .ExecuteDeleteAsync(token), ct);
    }
}

/// <summary>
/// A queued record plus the two things the writer stamps rather than the capture site: when it
/// happened, and which round it belongs to. Round is nullable at capture because a transaction can
/// be raised before a round has an id.
/// </summary>
public sealed class PendingMarketRecord
{
    public MarketRecord Record = null!;
    public DateTime OccurredAt;
    public int? RoundId;
}
