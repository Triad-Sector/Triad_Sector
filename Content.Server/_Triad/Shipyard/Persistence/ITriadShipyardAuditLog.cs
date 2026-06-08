using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;

namespace Content.Server._Triad.Shipyard.Persistence;

public interface ITriadShipyardAuditLog
{
    Task RecordAsync(TriadShipyardAuditEvent ev, CancellationToken ct);

    /// <summary>
    /// F8 fix: bulk-insert a batch of audit events in a single transaction. Used by the
    /// background audit consumer in <c>TriadTamperPolicyService</c> to drain the channel
    /// efficiently. EF's <c>AddRange + SaveChangesAsync</c> yields one transaction per
    /// batch, which is both faster and atomically all-or-nothing under failure.
    /// </summary>
    Task RecordBatchAsync(IReadOnlyList<TriadShipyardAuditEvent> events, CancellationToken ct);

    Task<PagedResult<TriadShipyardAuditEvent>> QueryAsync(AuditFilter filter, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Distinct players who have any tamper audit row in <paramref name="roundId"/>, each with their
    /// most recent name seen that round. Drives the audit panel's per-round player dropdown.
    /// </summary>
    Task<IReadOnlyList<AuditRoundPlayer>> GetRoundPlayersAsync(int roundId, CancellationToken ct);

    /// <summary>
    /// The engine-recorded start timestamp (UTC) of <paramref name="roundId"/>, used to anchor the
    /// round-time filter. Null when the round is unknown or has no recorded start.
    /// </summary>
    Task<DateTime?> GetRoundStartDateAsync(int roundId, CancellationToken ct);
}
