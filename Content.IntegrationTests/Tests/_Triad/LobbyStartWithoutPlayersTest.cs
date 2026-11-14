using Content.Server.GameTicking;
using Content.Shared._Triad.CCVar;
using Content.Shared.CCVar;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad;

/// <summary>
/// With the lobby on and nobody connected, a restart leaves the round waiting for a first player
/// unless triad.lobby.start_without_players is set, in which case the lobby countdown runs and the
/// round starts on its own. Both directions are asserted, so the test fails if either stops holding.
/// </summary>
[TestFixture]
[TestOf(typeof(GameTicker))]
public sealed class LobbyStartWithoutPlayersTest
{
    [TestCase(true, GameRunLevel.InRound)]
    [TestCase(false, GameRunLevel.PreRoundLobby)]
    public async Task EmptyLobbyStartsOnlyWhenEnabled(bool startWithoutPlayers, GameRunLevel expected)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            DummyTicker = false,
            // No InLobby: that setting connects a client. The lobby is switched on below instead.
            Connected = false,
            Dirty = true,
        });
        var server = pair.Server;
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var players = server.ResolveDependency<IPlayerManager>();
        var ticker = server.System<GameTicker>();

        await server.WaitPost(() =>
        {
            cfg.SetCVar(CCVars.GameLobbyEnabled, true);
            cfg.SetCVar(CCVars.GameLobbyDuration, 2);
            cfg.SetCVar(TriadCCVars.LobbyStartWithoutPlayers, startWithoutPlayers);
            ticker.RestartRound();
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(players.PlayerCount, Is.Zero, "The fixture needs an empty server.");
            Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.PreRoundLobby));
        });

        // Well past the two second lobby, with room for the map preload.
        await pair.RunTicksSync(timing.TickRate * 8);

        await server.WaitAssertion(() => Assert.That(ticker.RunLevel, Is.EqualTo(expected)));

        await server.WaitPost(() => cfg.SetCVar(TriadCCVars.LobbyStartWithoutPlayers, false));
        await pair.CleanReturnAsync();
    }
}
