using System;
using System.Collections.Generic;
using Content.Server.Database;

namespace Content.Server._Triad.Shipyard.Persistence;

public sealed record AuditFilter(
    DateTime? From,
    DateTime? To,
    Guid? PlayerUserId,
    IReadOnlyCollection<TriadShipyardEventType>? EventTypes,
    string? ShipNameContains,
    // Scopes the query to a single game round. From/To still apply within it (the round-time boxes are
    // anchored to absolute timestamps before they get here). Null = no round scoping.
    int? RoundId = null);

// A distinct player who has tamper audit activity in a given round, used to build the audit panel's
// per-round player dropdown.
public sealed record AuditRoundPlayer(Guid UserId, string? Name);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Rows,
    int Page,
    int PageSize,
    int TotalCount);
