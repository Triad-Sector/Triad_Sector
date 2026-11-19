using System;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.IoC;

namespace Content.Server._Triad.Shipyard.Persistence;

public sealed class TriadShipyardConsumedStore : ITriadShipyardConsumedStore
{
    [Dependency] private IServerDbManager _db = default!;

    public Task<bool> IsConsumedAsync(byte[] shipHash, CancellationToken ct)
    {
        return _db.RunTriadDbCommand(async (db, c) =>
            await db.TriadShipyardConsumedShips.AnyAsync(r => r.ShipHash == shipHash, c), ct);
    }

    public Task<int> CountForPlayerAsync(Guid playerUserId, CancellationToken ct)
    {
        return _db.RunTriadDbCommand(async (db, c) =>
            await db.TriadShipyardConsumedShips.CountAsync(r => r.PlayerUserId == playerUserId, c), ct);
    }

    public Task<bool> TryConsumeAsync(
        byte[] shipHash,
        Guid playerUserId,
        Guid? shipGuid,
        string? shipName,
        int? roundId,
        CancellationToken ct)
    {
        return _db.RunTriadDbCommand(async (db, c) =>
        {
            db.TriadShipyardConsumedShips.Add(new TriadShipyardConsumedShip
            {
                ShipHash = shipHash,
                PlayerUserId = playerUserId,
                ShipGuid = shipGuid,
                ShipName = shipName,
                ImportedAt = DateTime.UtcNow,
                ImportedRoundId = roundId,
            });

            try
            {
                await db.SaveChangesAsync(c);
                return true;
            }
            catch (DbUpdateException e) when (IsHashUniqueViolation(e))
            {
                return false;
            }
        }, ct);
    }

    /// <summary>
    /// Whether a failed write was the hash unique index and nothing else. Swallowing every update
    /// fault here would turn a database outage into a silent "already imported", which reads to the
    /// player as their ship having been taken and to us as nothing at all.
    /// </summary>
    internal static bool IsHashUniqueViolation(DbUpdateException e)
    {
        return e.InnerException switch
        {
            Npgsql.PostgresException pg => pg.SqlState == "23505"
                && pg.ConstraintName is { } name
                && name.Contains("triad_shipyard_consumed_ships_ship_hash", StringComparison.Ordinal),
            Microsoft.Data.Sqlite.SqliteException sq => sq.SqliteErrorCode == 19
                && sq.Message.Contains("UNIQUE", StringComparison.Ordinal)
                && sq.Message.Contains("triad_shipyard_consumed_ships.ship_hash", StringComparison.Ordinal),
            _ => false,
        };
    }
}
