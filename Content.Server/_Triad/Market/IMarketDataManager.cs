using System.Collections.Generic;
using System.Threading.Tasks;

namespace Content.Server._Triad.Market;

/// <summary>
/// The capture surface. Everything here is fire and forget from the game thread's point of view:
/// nothing on this interface returns a Task that a capture site is expected to await, because a
/// capture site is on the tick.
/// </summary>
public interface IMarketDataManager
{
    void Initialize();

    /// <summary>
    /// Records one transaction. Safe to call from the game thread on any path, including a sell
    /// loop: it stamps the time, enqueues, and returns.
    ///
    /// <para>Does nothing when capture is disabled. Over the drop threshold the record is discarded
    /// and counted rather than queued, because a stalled tick is a worse outcome than a missing
    /// telemetry row.</para>
    /// </summary>
    void Record(MarketRecord record);

    /// <summary>Whether capture is on. Check before doing expensive work to build a record.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Whether per-item line capture is on. Separate from <see cref="Enabled"/> because lines are
    /// the expensive half and want to be disableable on their own.
    /// </summary>
    bool LinesEnabled { get; }

    /// <summary>Drives the flush timer. Called once a tick.</summary>
    void Update();

    /// <summary>Stamps held pre-round records with the round that just started.</summary>
    void RoundStarting(int roundId);

    /// <summary>
    /// Notes that a player used a character this round. Deduplicated in memory and written once at
    /// round end rather than on every transaction, which is the whole point of the participant table.
    /// </summary>
    void RecordParticipant(Guid userId, string characterName);

    /// <summary>
    /// Records a periodic snapshot of sector account balances. Low frequency by design, so it goes
    /// straight to the writer rather than through the transaction queue.
    /// </summary>
    void RecordAccountSamples(IReadOnlyList<(string Account, long Balance)> samples);

    /// <summary>
    /// Deletes raw rows past the retention window. Splits and lines go with them by cascade; the
    /// price rollup is permanent and untouched.
    /// </summary>
    void PurgeExpired();

    /// <summary>
    /// Flushes everything still queued and waits for it. Called at round restart, where there is
    /// still a live tick, rather than relying on process shutdown.
    /// </summary>
    Task Flush();
}
