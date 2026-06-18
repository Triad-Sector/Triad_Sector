using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.IoC;

namespace Content.Server._Triad.Shipyard.Persistence;

public sealed class TriadShipyardAuditLogStore : ITriadShipyardAuditLog
{
    [Dependency] private readonly IServerDbManager _db = default!;

    public Task RecordAsync(TriadShipyardAuditEvent ev, CancellationToken ct)
    {
        return _db.RunTriadDbCommand(async (db, c) =>
        {
            db.TriadShipyardAuditEvents.Add(ev);
            await db.SaveChangesAsync(c);
        }, ct);
    }

    public Task RecordBatchAsync(IReadOnlyList<TriadShipyardAuditEvent> events, CancellationToken ct)
    {
        if (events.Count == 0)
            return Task.CompletedTask;

        return _db.RunTriadDbCommand(async (db, c) =>
        {
            // F8 fix: AddRange + SaveChangesAsync executes one transaction. EF may emit multiple
            // INSERTs internally but they share a single round-trip context, and a failure
            // mid-batch rolls back the whole transaction so we don't end up with a torn write.
            // The consumer retries; if retries exhaust the whole batch is dropped (with logging),
            // which is correct - we never commit a partial batch.
            db.TriadShipyardAuditEvents.AddRange(events);
            await db.SaveChangesAsync(c);
        }, ct);
    }

    public Task<PagedResult<TriadShipyardAuditEvent>> QueryAsync(AuditFilter filter, int page, int pageSize, CancellationToken ct)
    {
        return _db.RunTriadDbCommand(async (ServerDbContext db, CancellationToken c) =>
        {
            IQueryable<TriadShipyardAuditEvent> q = db.TriadShipyardAuditEvents;

            if (filter.From.HasValue)
                q = q.Where(a => a.At >= filter.From.Value);
            if (filter.To.HasValue)
                q = q.Where(a => a.At <= filter.To.Value);
            if (filter.PlayerUserId.HasValue)
                q = q.Where(a => a.PlayerUserId == filter.PlayerUserId.Value);
            if (filter.EventTypes is { Count: > 0 })
                q = q.Where(a => filter.EventTypes.Contains(a.EventType));
            if (!string.IsNullOrWhiteSpace(filter.ShipNameContains))
            {
                // F12 fix: four bugs in the previous one-liner.
                //   1. No length cap. An admin (or escalated user) could pass a 1MB needle and
                //      trigger a full-scan that pins the DB. Now capped at 100 chars.
                //   2. No SQL wildcard escaping. A player-saved ShipName containing % or _ would
                //      poison admin searches, and an admin typing _ would match every row.
                //      Now we escape \, %, _ in the needle and pass an explicit \ escape character
                //      to EF.Functions.Like so the escaped sequences match literals.
                //   3. .ToLower() on the input is culture-sensitive (Turkish 'I' -> dotless 'ı'
                //      mismatches Postgres ICU-collated LOWER). Now .ToLowerInvariant().
                //   4. The 3-arg EF.Functions.Like overload propagates the escape character to
                //      both Postgres and SQLite providers correctly.
                // The column-side .ToLower() still defeats any future index on ship_name; that
                // would want a functional index or a denormalized ship_name_lower column. Out of
                // scope for this fix.
                var needle = filter.ShipNameContains.Trim();
                if (needle.Length > 100)
                    needle = needle.Substring(0, 100);
                var escaped = needle
                    .Replace("\\", "\\\\")
                    .Replace("%", "\\%")
                    .Replace("_", "\\_")
                    .ToLowerInvariant();
                q = q.Where(a => a.ShipName != null
                                 && EF.Functions.Like(a.ShipName!.ToLower(), $"%{escaped}%", "\\"));
            }
            if (filter.RoundId.HasValue)
                q = q.Where(a => a.RoundId == filter.RoundId.Value);

            // Count + paged read are two queries, not one transaction, so a concurrent insert between
            // them can shift the page by one row. Intentional for a live-tailing audit feed (a transient
            // off-by-one in a log that re-pulls every tick is invisible) and matches the engine admin-log
            // paging. Review #15 (WONTFIX).
            var total = await q.CountAsync(c);
            var rows = await q
                .OrderByDescending(a => a.At)
                .Skip(page * pageSize)
                .Take(pageSize)
                .AsSplitQuery()
                .ToListAsync(c);

            return new PagedResult<TriadShipyardAuditEvent>(rows, page, pageSize, total);
        }, ct);
    }

    public Task<IReadOnlyList<AuditRoundPlayer>> GetRoundPlayersAsync(int roundId, CancellationToken ct)
    {
        return _db.RunTriadDbCommand(async (ServerDbContext db, CancellationToken c) =>
        {
            // Distinct (player, name) pairs in the round; a player who renamed mid-round can appear
            // more than once, so collapse to one entry per id (a representative name is all the dropdown
            // needs). Round player counts are tiny, so the in-memory dedupe is negligible and keeps the
            // SQL provider-simple (no GroupBy/order-sensitive Distinct to mistranslate).
            var pairs = await db.TriadShipyardAuditEvents
                .Where(a => a.RoundId == roundId)
                .Select(a => new { a.PlayerUserId, a.PlayerName })
                .Distinct()
                .ToListAsync(c);

            var seen = new HashSet<Guid>();
            var players = new List<AuditRoundPlayer>();
            foreach (var p in pairs)
            {
                if (seen.Add(p.PlayerUserId))
                    players.Add(new AuditRoundPlayer(p.PlayerUserId, p.PlayerName));
            }

            return (IReadOnlyList<AuditRoundPlayer>)players;
        }, ct);
    }

    public Task<DateTime?> GetRoundStartDateAsync(int roundId, CancellationToken ct)
    {
        return _db.RunTriadDbCommand(async (ServerDbContext db, CancellationToken c) =>
        {
            // StartDate is nullable; an unknown round or one with no recorded start both yield null,
            // which the caller treats as "can't anchor round-time".
            return await db.Round
                .Where(r => r.Id == roundId)
                .Select(r => r.StartDate)
                .FirstOrDefaultAsync(c);
        }, ct);
    }
}
