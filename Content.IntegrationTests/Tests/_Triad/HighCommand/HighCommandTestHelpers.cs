#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.HighCommand;
using Content.Server.GameTicking;
using Content.Server.Spawners.Components;
using Content.Server.Voting.Managers;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Roles;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.HighCommand;

/// <summary>
/// The pieces of a running TFA High Command outpost a test needs, copied out of the rule component.
/// </summary>
public readonly record struct RunningOutpost(EntityUid Rule, EntityUid Map, EntityUid Grid, EntityUid Station);

/// <summary>
/// A job spawn marker on the outpost grid.
/// </summary>
public readonly record struct HighCommandMarker(EntityUid Uid, ProtoId<JobPrototype>? Job, Vector2 Position);

public static class HighCommandTestHelpers
{
    public const string RuleId = "HighCommandOutpost";
    public const string ClearanceComponent = "TfaHighCommandClearance";

    public static readonly ProtoId<JobPrototype> Rep = "TfaHighCommandRep";
    public static readonly ProtoId<JobPrototype> Intern = "TfaHighCommandIntern";

    /// <summary>
    /// Reads the outpost a started rule built. Fails the test if the rule built nothing.
    /// </summary>
    public static RunningOutpost ReadOutpost(IEntityManager entMan, EntityUid rule)
    {
        var comp = entMan.GetComponent<HighCommandOutpostRuleComponent>(rule);
        EntityUid? map = comp.MapEntity;
        EntityUid? grid = comp.GridEntity;
        EntityUid? station = comp.Station;

        Assert.That(map, Is.Not.Null, "Fixture: the outpost rule created no map.");
        Assert.That(grid, Is.Not.Null, "Fixture: the outpost rule loaded no grid.");
        Assert.That(station, Is.Not.Null, "Fixture: the outpost rule built no station.");
        return new RunningOutpost(rule, map!.Value, grid!.Value, station!.Value);
    }

    /// <summary>
    /// Closes the votes the lobby opened and stops new ones. Mono's AutoVoteSystem opens a preset vote when the lobby
    /// starts, and VoteManager sends every open vote to each session that goes in game through
    /// <c>player.Channel.SendMessage</c>, which throws on a dummy session's channel. Call it before adding dummies:
    /// cancelling a vote messages every session too.
    /// </summary>
    public static async Task CloseLobbyVotes(TestPair pair)
    {
        var server = pair.Server;
        var votes = server.ResolveDependency<IVoteManager>();
        server.CfgMan.SetCVar(CCVars.AutoVoteEnabled, false);
        await server.WaitPost(() =>
        {
            foreach (var vote in votes.ActiveVotes.ToList())
                vote.Cancel();
        });
        await pair.RunTicksSync(5);
    }

    /// <summary>
    /// Starts a round nobody readied up for, so every connected session stays in the lobby, free to latejoin, then
    /// starts the outpost rule. Needs a pair made with <c>InLobby = true</c>.
    /// </summary>
    public static async Task<RunningOutpost> StartRoundWithOutpost(TestPair pair)
    {
        var server = pair.Server;
        var ticker = server.System<GameTicker>();
        Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.PreRoundLobby), "Fixture: the pair should start in the lobby.");

        await server.WaitPost(() =>
        {
            ticker.SetGamePreset("Greenshift");
            ticker.StartRound();
        });
        await pair.RunTicksSync(10);
        Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound), "Fixture: the round did not start.");

        var rule = EntityUid.Invalid;
        await server.WaitPost(() => Assert.That(ticker.StartGameRule(RuleId, out rule), "Fixture: the outpost rule did not start."));
        await pair.RunTicksSync(10);

        var outpost = default(RunningOutpost);
        await server.WaitPost(() => outpost = ReadOutpost(server.EntMan, rule));
        return outpost;
    }

    /// <summary>
    /// Every job spawn marker on a grid.
    /// </summary>
    public static List<HighCommandMarker> Markers(IEntityManager entMan, EntityUid grid)
    {
        var markers = new List<HighCommandMarker>();
        var query = entMan.AllEntityQueryEnumerator<SpawnPointComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var spawnPoint, out var xform))
        {
            if (xform.GridUid == grid)
                markers.Add(new HighCommandMarker(uid, spawnPoint.Job, xform.LocalPosition));
        }

        return markers;
    }
}
