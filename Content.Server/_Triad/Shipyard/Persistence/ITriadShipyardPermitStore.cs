using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;

namespace Content.Server._Triad.Shipyard.Persistence;

/// <summary>
/// Per-player legacy-onboarding permits. While the server is in enforce mode, a player with an
/// active permit may load non-our-key (unsigned or foreign-signed) ships, which the load path
/// re-signs with the server key. The permit is the rollout exception for stragglers who did not get
/// a ship signed during the notify window; it clears on admin revoke or session end.
/// </summary>
public interface ITriadShipyardPermitStore
{
    /// <summary>
    /// Grants (or refreshes) a player's permit. Upsert on player_user_id so re-granting refreshes
    /// the granting admin and notes without racing. Updates the in-memory cache after the DB commit.
    /// </summary>
    Task GrantAsync(Guid playerUserId, Guid adminId, DateTime grantedAt, string? notes, CancellationToken ct);

    /// <summary>
    /// Revokes a player's permit (admin action or session-end clear). Removes the DB row and the
    /// cache entry. Returns whether a row was found and removed.
    /// </summary>
    Task<bool> RevokeAsync(Guid playerUserId, CancellationToken ct);

    /// <summary>
    /// Synchronous cache-only lookup for the load decision hot path. Returns false if the cache has
    /// not populated yet; callers needing DB-fallback semantics use <see cref="IsPermittedAsync"/>.
    /// </summary>
    bool HasPermitFor(Guid playerUserId);

    /// <summary>
    /// Async lookup that falls through to the DB if the cache has not populated (brief boot window).
    /// </summary>
    Task<bool> IsPermittedAsync(Guid playerUserId, CancellationToken ct);

    /// <summary>
    /// Lists active permits, optionally filtered by player. Reads from the DB so the admin EUI sees
    /// authoritative state.
    /// </summary>
    Task<IReadOnlyList<TriadShipyardMigrationPermit>> QueryActiveAsync(Guid? playerUserIdFilter, CancellationToken ct);

    /// <summary>
    /// Loads all current permits into the in-memory cache once at startup.
    /// </summary>
    Task PopulateAsync(CancellationToken ct);
}
