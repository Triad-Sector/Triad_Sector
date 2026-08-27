using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Content.Shared._Triad.CCVar;
using Prometheus;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Market;

/// <summary>
/// Queues captured transactions and writes them in batches, off the game thread.
///
/// <para>The shape is copied from <c>AdminLogManager</c>, which solves the same problem at
/// production volume on this server: high-frequency game-thread events into the same Postgres. The
/// cvar defaults are deliberately identical so the two behave alike under load. One thing is
/// changed on purpose, see <see cref="Flush"/>.</para>
/// </summary>
public sealed class MarketDataManager : IMarketDataManager
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly ILogManager _logManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly MarketDataStore _store = default!;

    public const string SawmillId = "triad.market";

    private static readonly Gauge QueueDepth = Metrics.CreateGauge(
        "triad_market_queue",
        "How many market transactions are waiting to be written.");

    private static readonly Gauge PreRoundQueueDepth = Metrics.CreateGauge(
        "triad_market_pre_round_queue",
        "How many market transactions are held waiting for a round id.");

    private static readonly Gauge Dropped = Metrics.CreateGauge(
        "triad_market_dropped",
        "Market transactions discarded because the queue was over its drop threshold.");

    private static readonly Gauge Written = Metrics.CreateGauge(
        "triad_market_written",
        "Market transactions written to the database in a round.");

    private static readonly Gauge WriteFailures = Metrics.CreateGauge(
        "triad_market_write_failures",
        "Batches that failed to write. Every one of these is lost data.");

    private ISawmill _sawmill = default!;

    private bool _metricsEnabled;
    private bool _enabled;
    private bool _linesEnabled;
    private TimeSpan _sendDelay;
    private int _queueMax;
    private int _preRoundQueueMax;
    private int _dropThreshold;
    private int _retentionDays;

    private readonly ConcurrentQueue<PendingMarketRecord> _queue = new();
    private readonly ConcurrentQueue<PendingMarketRecord> _preRoundQueue = new();

    private TimeSpan _nextFlush;
    private int _currentRoundId;

    // 1 while a batch is in flight, 0 otherwise. Guards against a slow write overlapping the next.
    private int _writing;
    private int _dropCount;

    public bool Enabled => _enabled;
    public bool LinesEnabled => _enabled && _linesEnabled;

    public void Initialize()
    {
        _sawmill = _logManager.GetSawmill(SawmillId);

        _cfg.OnValueChanged(CVars.MetricsEnabled, v => _metricsEnabled = v, true);
        _cfg.OnValueChanged(TriadCCVars.MarketDataEnabled, v => _enabled = v, true);
        _cfg.OnValueChanged(TriadCCVars.MarketDataLinesEnabled, v => _linesEnabled = v, true);
        _cfg.OnValueChanged(TriadCCVars.MarketDataQueueSendDelay, v => _sendDelay = TimeSpan.FromSeconds(v), true);
        _cfg.OnValueChanged(TriadCCVars.MarketDataQueueMax, v => _queueMax = v, true);
        _cfg.OnValueChanged(TriadCCVars.MarketDataPreRoundQueueMax, v => _preRoundQueueMax = v, true);
        _cfg.OnValueChanged(TriadCCVars.MarketDataDropThreshold, v => _dropThreshold = v, true);
        _cfg.OnValueChanged(TriadCCVars.MarketDataRetentionDays, v => _retentionDays = v, true);
    }

    public void Record(MarketRecord record)
    {
        if (!_enabled)
            return;

        var roundId = _currentRoundId;

        var pending = new PendingMarketRecord
        {
            Record = record,
            OccurredAt = DateTime.UtcNow,
            RoundId = roundId > 0 ? roundId : null,
        };

        // No round yet. Hold it rather than writing a row nothing can be grouped by, and stamp it
        // when the round starts. Character spawn sits exactly on this boundary.
        if (pending.RoundId == null)
        {
            if (_preRoundQueue.Count >= _preRoundQueueMax)
            {
                CountDrop();
                return;
            }

            _preRoundQueue.Enqueue(pending);
            return;
        }

        if (_queue.Count >= _dropThreshold)
        {
            CountDrop();
            return;
        }

        _queue.Enqueue(pending);
    }

    private void CountDrop()
    {
        var dropped = Interlocked.Increment(ref _dropCount);

        // Loud on the first one, then quiet: a flood of these would itself cost frame time. The
        // gauge carries the real count.
        if (dropped == 1)
            _sawmill.Error($"Market data queue over its drop threshold of {_dropThreshold}. Dropping records.");

        if (_metricsEnabled)
            Dropped.Inc();
    }

    public void RoundStarting(int roundId)
    {
        _currentRoundId = roundId;

        while (_preRoundQueue.TryDequeue(out var pending))
        {
            pending.RoundId = roundId;
            _queue.Enqueue(pending);
        }

        if (_metricsEnabled)
        {
            PreRoundQueueDepth.Set(0);
            Dropped.Set(0);
            Written.Set(0);
            WriteFailures.Set(0);
        }

        Interlocked.Exchange(ref _dropCount, 0);
    }

    public void Update()
    {
        if (_metricsEnabled)
        {
            QueueDepth.Set(_queue.Count);
            PreRoundQueueDepth.Set(_preRoundQueue.Count);
        }

        if (_queue.IsEmpty)
            return;

        if (_timing.RealTime < _nextFlush && _queue.Count < _queueMax)
            return;

        // Already writing. Leave the queue alone; the in-flight batch will be followed by another
        // pass next tick rather than two overlapping writes fighting for the same connection.
        if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0)
            return;

        _nextFlush = _timing.RealTime + _sendDelay;
        _ = WriteBatch();
    }

    private readonly HashSet<(Guid, string)> _participants = new();

    public void RecordParticipant(Guid userId, string characterName)
    {
        if (!_enabled || string.IsNullOrEmpty(characterName))
            return;

        lock (_participants)
        {
            _participants.Add((userId, characterName));
        }
    }

    private async Task WriteParticipants(int roundId)
    {
        List<(Guid, string)> copy;
        lock (_participants)
        {
            if (_participants.Count == 0)
                return;
            copy = new List<(Guid, string)>(_participants);
            _participants.Clear();
        }

        try
        {
            await _store.RecordParticipants(roundId, copy);
        }
        catch (Exception e)
        {
            _sawmill.Error($"Failed to write market round participants: {e}");
        }
    }

    public void PurgeExpired()
    {
        // Zero or less keeps everything, which is the escape hatch for anyone who wants the full
        // history and has the disk for it.
        if (!_enabled || _retentionDays <= 0)
            return;

        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
        _ = PurgeAsync(cutoff);
    }

    private async Task PurgeAsync(DateTime cutoff)
    {
        try
        {
            var removed = await _store.PurgeOlderThan(cutoff);
            if (removed > 0)
                _sawmill.Info($"Purged {removed} market transactions older than {cutoff:O}.");
        }
        catch (Exception e)
        {
            _sawmill.Error($"Failed to purge expired market data: {e}");
        }
    }

    public void RecordAccountSamples(IReadOnlyList<(string Account, long Balance)> samples)
    {
        if (!_enabled || _currentRoundId <= 0 || samples.Count == 0)
            return;

        // Copied because the caller reuses its buffer between samples, and this outlives the call.
        var copy = new List<(string, long)>(samples);
        var roundId = _currentRoundId;
        var at = DateTime.UtcNow;

        _ = SampleAsync(roundId, at, copy);
    }

    private async Task SampleAsync(int roundId, DateTime at, IReadOnlyList<(string, long)> samples)
    {
        try
        {
            await _store.WriteAccountSamples(roundId, at, samples);
        }
        catch (Exception e)
        {
            _sawmill.Error($"Failed to write sector account samples: {e}");
        }
    }

    public async Task Flush()
    {
        // Wait out an in-flight batch rather than racing it.
        var roundId = _currentRoundId;

        while (Interlocked.CompareExchange(ref _writing, 1, 0) != 0)
            await Task.Delay(10);

        await WriteBatch();

        if (roundId > 0)
            await WriteParticipants(roundId);
    }

    private async Task WriteBatch()
    {
        try
        {
            var batch = new List<PendingMarketRecord>(_queue.Count + _preRoundQueue.Count);
            while (_queue.TryDequeue(out var pending))
                batch.Add(pending);

            // Pre-round records go out too, still carrying no round. Holding them back would lose
            // them entirely if the server never reaches RoundStarting before it stops, and a row
            // with a null round is a fact with a gap in it rather than no fact at all. Records held
            // here are stamped and moved to the main queue the moment a round starts, so this only
            // ever picks up what a round start did not.
            while (_preRoundQueue.TryDequeue(out var held))
                batch.Add(held);

            if (batch.Count == 0)
                return;

            var written = await _store.WriteBatch(batch);

            if (_metricsEnabled)
                Written.Inc(written);
        }
        catch (Exception e)
        {
            // The batch is already dequeued and is gone. Re-queueing on failure risks looping
            // forever against a database that will keep rejecting it, and this is telemetry: losing
            // a batch loudly beats stalling the writer. Every one of these is real lost data, so it
            // is an error and it is counted.
            _sawmill.Error($"Failed to write a market data batch, records lost: {e}");

            if (_metricsEnabled)
                WriteFailures.Inc();
        }
        finally
        {
            Interlocked.Exchange(ref _writing, 0);
        }
    }
}
