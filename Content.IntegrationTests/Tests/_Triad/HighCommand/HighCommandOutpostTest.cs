#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Content.Server._Triad.HighCommand;
using Content.Server._Triad.Shipyard;
using Content.Server.GameTicking;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared._Triad.HighCommand;
using Content.Shared.Shuttles.Components;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using static Content.IntegrationTests.Tests._Triad.HighCommand.HighCommandTestHelpers;

namespace Content.IntegrationTests.Tests._Triad.HighCommand;

/// <summary>
/// The outpost the <c>HighCommandOutpost</c> rule stands up, and the hull clearance that opens it to FTL.
/// </summary>
[TestFixture]
[TestOf(typeof(HighCommandOutpostRuleSystem))]
public sealed class HighCommandOutpostTest
{
    private const string StationProtoId = "TfaHighCommandOutpostStation";

    /// <summary>
    /// Every <c>addgamerule</c> spawns a fresh rule entity, so a second start has to find the first outpost across
    /// rules and build nothing. This is also the first test to load the outpost map, so it checks the spawners the
    /// job slots depend on.
    /// </summary>
    [Test]
    public async Task StartingTheRuleTwiceBuildsOneOutpost()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            DummyTicker = false,
            Dirty = true,
        });
        var server = pair.Server;
        var entMan = server.EntMan;
        var ticker = server.System<GameTicker>();
        var stations = server.System<StationSystem>();

        var first = EntityUid.Invalid;
        await server.WaitPost(() => Assert.That(ticker.StartGameRule(RuleId, out first)));
        await pair.RunTicksSync(5);

        var outpost = default(RunningOutpost);
        await server.WaitAssertion(() =>
        {
            outpost = ReadOutpost(entMan, first);
            Assert.That(OutpostMaps(entMan), Is.EquivalentTo(new[] { outpost.Map }),
                "Fixture: one start should leave exactly one clearance-gated FTL destination.");

            // Both jobs set alwaysUseSpawner, so these markers are the only places the job slots can spawn.
            var markers = Markers(entMan, outpost.Grid);
            Assert.Multiple(() =>
            {
                Assert.That(markers.Count(m => m.Job == Rep), Is.EqualTo(2), "Representative spawners on the outpost.");
                Assert.That(markers.Count(m => m.Job == Intern), Is.EqualTo(3), "Intern spawners on the outpost.");
                Assert.That(markers.Select(m => stations.GetOwningStation(m.Uid)), Is.All.EqualTo(outpost.Station),
                    "A spawner on the outpost grid is not owned by the outpost station, so it can never be used.");
            });
        });

        await server.WaitPost(() => Assert.That(ticker.StartGameRule(RuleId, out _)));
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(OutpostMaps(entMan), Is.EquivalentTo(new[] { outpost.Map }),
                    "A second start built a second clearance-gated outpost map.");
                Assert.That(OutpostStations(entMan), Is.EqualTo(1), "A second start built a second outpost station.");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Clearance is granted per hull for one round. No grid save may write it, or a saved ship carries it into the
    /// next round.
    /// </summary>
    [Test]
    public async Task ClearanceIsNeverWrittenByAGridSave()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var mapLoader = entMan.System<MapLoaderSystem>();
        var shipSave = entMan.System<ShipyardGridSaveSystem>();

        var map = await pair.CreateTestMap();
        var grid = map.Grid.Owner;

        var engineYaml = string.Empty;
        string? shipYaml = null;
        await server.WaitAssertion(() =>
        {
            // The same call hcclearance makes.
            entMan.AddComponent<TfaHighCommandClearanceComponent>(grid);

            // Engine save first: the shipyard save mutates the grid before it serializes.
            var writer = new StringWriter();
            Assert.That(mapLoader.TrySaveGrid(grid, writer), "Fixture: the engine grid save failed.");
            engineYaml = writer.ToString();

            Assert.That(shipSave.TryBuildShipSaveYaml(grid, out shipYaml, out _), "Fixture: the shipyard save failed.");
        });

        Assert.Multiple(() =>
        {
            Assert.That(engineYaml, Does.Contain("MapGrid"), "Fixture: the engine save did not write the grid.");
            Assert.That(shipYaml, Does.Contain("MapGrid"), "Fixture: the shipyard save did not write the grid.");
            Assert.That(engineYaml, Does.Not.Contain(ClearanceComponent), "The engine grid save wrote hull clearance.");
            Assert.That(shipYaml, Does.Not.Contain(ClearanceComponent), "The shipyard save wrote hull clearance.");
            Assert.That(entMan.HasComponent<TfaHighCommandClearanceComponent>(grid),
                "Saving stripped clearance from the live hull; it should only stay out of the file.");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Maps registered as an FTL destination gated on hull clearance.
    /// </summary>
    private static List<EntityUid> OutpostMaps(IEntityManager entMan)
    {
        var maps = new List<EntityUid>();
        var query = entMan.AllEntityQueryEnumerator<FTLDestinationComponent>();
        while (query.MoveNext(out var uid, out var destination))
        {
            if (destination.Whitelist?.Components?.Contains(ClearanceComponent) == true)
                maps.Add(uid);
        }

        return maps;
    }

    private static int OutpostStations(IEntityManager entMan)
    {
        var count = 0;
        var query = entMan.AllEntityQueryEnumerator<StationDataComponent, MetaDataComponent>();
        while (query.MoveNext(out _, out _, out var meta))
        {
            if (meta.EntityPrototype?.ID == StationProtoId)
                count++;
        }

        return count;
    }
}
