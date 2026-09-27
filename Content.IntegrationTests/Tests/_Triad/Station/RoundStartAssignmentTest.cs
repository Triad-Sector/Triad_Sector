#nullable enable

using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Tests.Station;
using Content.Server.Maps;
using Content.Server.Station.Systems;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.Station;

/// <summary>
/// Round-start assignment across stations that offer different jobs, which is every round here: the TDF outpost is the
/// only station with a Chief Enforcer, the High Command outpost the only one with its jobs.
/// </summary>
[TestFixture]
[TestOf(typeof(StationJobsSystem))]
public sealed class RoundStartAssignmentTest
{
    private const string OnlyHere = "TriadRoundStartOnlyHere";
    private const string Common = "TriadRoundStartCommon";

    [TestPrototypes]
    private const string Prototypes = $@"
- type: playTimeTracker
  id: PlayTime{OnlyHere}

- type: playTimeTracker
  id: PlayTime{Common}

- type: job
  id: {OnlyHere}
  playTimeTracker: PlayTime{OnlyHere}

- type: job
  id: {Common}
  playTimeTracker: PlayTime{Common}

- type: gameMap
  id: TriadRoundStartMap
  minPlayers: 0
  mapName: TriadRoundStartMap
  mapPath: /Maps/Test/empty.yml
  stations:
    Here:
      mapNameTemplate: Here
      stationProto: StandardNanotrasenStation
      components:
        - type: StationJobs
          availableJobs:
            {OnlyHere}: [1, 1]
    Elsewhere:
      mapNameTemplate: Elsewhere
      stationProto: StandardNanotrasenStation
      components:
        - type: StationJobs
          availableJobs:
            {Common}: [5, 5]
";

    private const int Rounds = 50;
    private const int CommonPlayers = 4;

    /// <summary>
    /// A player whose job only one station offers starts on that station. Shares follow slot counts, so here the
    /// station with the one unique slot gets none, and the player left over used to go to a random station whether it
    /// had their job or not.
    /// </summary>
    [Test]
    public async Task PlayersReachTheOnlyStationOfferingTheirJob()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var stationSystem = server.System<StationSystem>();
        var stationJobs = server.System<StationJobsSystem>();

        var map = prototypes.Index<GameMapPrototype>("TriadRoundStartMap");
        var here = EntityUid.Invalid;
        var elsewhere = EntityUid.Invalid;
        await server.WaitPost(() =>
        {
            here = stationSystem.InitializeNewStation(map.Stations["Here"], null, "Here");
            elsewhere = stationSystem.InitializeNewStation(map.Stations["Elsewhere"], null, "Elsewhere");
        });

        await server.WaitAssertion(() =>
        {
            var misplaced = 0;
            var commonMisplaced = 0;
            for (var i = 0; i < Rounds; i++)
            {
                var unique = new Dictionary<NetUserId, HumanoidCharacterProfile>().AddJob(OnlyHere, JobPriority.High);
                var players = unique.WithPlayers(
                    new Dictionary<NetUserId, HumanoidCharacterProfile>().AddJob(Common, JobPriority.High, CommonPlayers));

                var assigned = stationJobs.AssignJobs(players, new[] { here, elsewhere });

                if (!assigned.TryGetValue(unique.Keys.Single(), out var placed)
                    || placed.Item1?.Id != OnlyHere
                    || placed.Item2 != here)
                {
                    misplaced++;
                }

                commonMisplaced += players.Keys
                    .Except(unique.Keys)
                    .Count(p => !assigned.TryGetValue(p, out var common) || common.Item1?.Id != Common || common.Item2 != elsewhere);
            }

            Assert.Multiple(() =>
            {
                Assert.That(misplaced, Is.Zero,
                    $"In {misplaced} of {Rounds} round starts the player missed the only station with their job.");
                Assert.That(commonMisplaced, Is.Zero,
                    $"{commonMisplaced} players who wanted the common job missed its station.");
            });
        });

        await pair.CleanReturnAsync();
    }
}
