using Content.Server.GameTicking.Events;
using Content.Shared.GameTicking;

namespace Content.Server._Triad.Market;

/// <summary>
/// Drives <see cref="IMarketDataManager"/> from the entity system lifecycle. Holds no state of its
/// own; the queue and the writer live on the manager, which outlives any single round.
/// </summary>
public sealed class MarketDataSystem : EntitySystem
{
    [Dependency] private readonly IMarketDataManager _market = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RoundStartingEvent>(OnRoundStarting);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private void OnRoundStarting(RoundStartingEvent ev)
    {
        _market.RoundStarting(ev.Id);

        // Purge here rather than on a timer. Round start is the quietest moment the server has and
        // the delete is a bounded range scan on an indexed column, so it costs nothing anyone feels.
        _market.PurgeExpired();
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        // Drain here rather than at process shutdown. The deploy pipeline restarts this server at
        // round end, so a flush that only fires on shutdown loses the last few seconds of every
        // round, which is not random loss: it deletes the end-of-round selling rush specifically.
        //
        // Deliberately not awaited and deliberately not blocking. The server keeps ticking through
        // the post-round lobby, so the write has time to land; blocking the game thread on a
        // database round trip to guarantee it would be a worse trade.
        _ = _market.Flush();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _market.Update();
    }

    public override void Shutdown()
    {
        base.Shutdown();

        // Backstop only, and an honest one: this cannot be awaited from here, so a process exiting
        // immediately after can still cut it off. The round-restart drain above is the path that
        // actually carries the guarantee.
        _ = _market.Flush();
    }
}
