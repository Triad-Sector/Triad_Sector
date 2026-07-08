using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.IoC;

namespace Content.Server._Triad.Shipyard.Persistence;

public sealed class TriadShipyardPermitStore : ITriadShipyardPermitStore
{
    [Dependency] private readonly IServerDbManager _db = default!;

    // Per-player permit cache. Presence of a key = an active permit. No expiry: a permit ends only
    // on admin revoke or session-end clear (both call RevokeAsync). Not authoritative until populated.
    private readonly ConcurrentDictionary<Guid, byte> _cache = new();
    private volatile bool _cachePopulated;

    public Task GrantAsync(Guid playerUserId, Guid adminId, DateTime grantedAt, string? notes, CancellationToken ct)
    {
        return _db.RunTriadDbCommand(async (db, c) =>
        {
            // Upsert on player_user_id so a re-grant refreshes the admin and notes atomically.
            // Postgres + SQLite both support ON CONFLICT (SQLite since 3.24).
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO triad_shipyard_migration_permits
                    (player_user_id, granted_by_admin_id, granted_at, notes)
                VALUES ({playerUserId}, {adminId}, {grantedAt}, {notes})
                ON CONFLICT (player_user_id) DO UPDATE SET
                    granted_by_admin_id = EXCLUDED.granted_by_admin_id,
                    granted_at = EXCLUDED.granted_at,
                    notes = EXCLUDED.notes
            ", c);
            _cache[playerUserId] = 0;
        }, ct);
    }

    public Task<bool> RevokeAsync(Guid playerUserId, CancellationToken ct)
    {
        return _db.RunTriadDbCommand(async (db, c) =>
        {
            var row = await db.TriadShipyardMigrationPermits
                .FirstOrDefaultAsync(p => p.PlayerUserId == playerUserId, c);
            if (row == null)
            {
                _cache.TryRemove(playerUserId, out _);
                return false;
            }
            db.TriadShipyardMigrationPermits.Remove(row);
            await db.SaveChangesAsync(c);
            _cache.TryRemove(playerUserId, out _);
            return true;
        }, ct);
    }

    public bool HasPermitFor(Guid playerUserId)
    {
        return _cachePopulated && _cache.ContainsKey(playerUserId);
    }

    public Task<bool> IsPermittedAsync(Guid playerUserId, CancellationToken ct)
    {
        if (_cachePopulated)
            return Task.FromResult(_cache.ContainsKey(playerUserId));

        // Cache not populated yet (brief startup window). Fall through to the DB.
        return _db.RunTriadDbCommand(async (db, c) =>
            await db.TriadShipyardMigrationPermits.AnyAsync(p => p.PlayerUserId == playerUserId, c), ct);
    }

    public Task<IReadOnlyList<TriadShipyardMigrationPermit>> QueryActiveAsync(Guid? playerUserIdFilter, CancellationToken ct)
    {
        return _db.RunTriadDbCommand(async (ServerDbContext db, CancellationToken c) =>
        {
            IQueryable<TriadShipyardMigrationPermit> q = db.TriadShipyardMigrationPermits;
            if (playerUserIdFilter.HasValue)
                q = q.Where(p => p.PlayerUserId == playerUserIdFilter.Value);
            var rows = await q.OrderByDescending(p => p.GrantedAt).ToListAsync(c);
            return (IReadOnlyList<TriadShipyardMigrationPermit>)rows;
        }, ct);
    }

    public Task PopulateAsync(CancellationToken ct)
    {
        return _db.RunTriadDbCommand(async (db, c) =>
        {
            var ids = await db.TriadShipyardMigrationPermits.Select(p => p.PlayerUserId).ToListAsync(c);
            // Don't Clear() first: a GrantAsync landing in the boot window (after we read ids, before we
            // flag populated) would be wiped and then read as "no permit" until restart. Boot runs this
            // once on an empty cache, so there's nothing to clear; add the rows idempotently and flag last.
            foreach (var id in ids)
                _cache.TryAdd(id, 0);
            _cachePopulated = true;
        }, ct);
    }
}
