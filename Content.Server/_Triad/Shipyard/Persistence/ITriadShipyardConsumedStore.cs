using System;
using System.Threading;
using System.Threading.Tasks;

namespace Content.Server._Triad.Shipyard.Persistence;

/// <summary>
/// The ledger of legacy save files that have been imported into the drydock. A save file names a
/// file, never a hull: the envelope carries no ship identity, so someone holding three saves of one
/// ship holds three separately valid files. This table is what stops a single file being spent
/// twice; the per-account import budget is what stops the other two becoming ships.
///
/// <para>Only an enforcing server reads or writes it. Under the tamper check off or in notify the
/// import is a rehearsal that leaves nothing behind, so a test box can import the same save as
/// often as it likes without spending it somewhere it still has to work.</para>
/// </summary>
public interface ITriadShipyardConsumedStore
{
    /// <summary>
    /// Whether this exact signed file has already been imported. Not a decision on its own: the
    /// insert in <see cref="TryConsumeAsync"/> is what actually settles a race between two imports.
    /// </summary>
    Task<bool> IsConsumedAsync(byte[] shipHash, CancellationToken ct);

    /// <summary>How many imports this account has spent, for the budget check.</summary>
    Task<int> CountForPlayerAsync(Guid playerUserId, CancellationToken ct);

    /// <summary>
    /// Burns the hash. Returns false when the unique index refused it, which means another import of
    /// the same file won the race and this one must not produce a second ship. Every other write
    /// fault throws, so a database problem cannot read as "already imported".
    /// </summary>
    Task<bool> TryConsumeAsync(
        byte[] shipHash,
        Guid playerUserId,
        Guid? shipGuid,
        string? shipName,
        int? roundId,
        CancellationToken ct);
}
