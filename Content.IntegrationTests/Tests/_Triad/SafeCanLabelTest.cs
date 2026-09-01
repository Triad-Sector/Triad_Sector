using Content.Server._Triad.Atmos.Components;
using Content.Server._Triad.Atmos.EntitySystems;
using Content.Server.Examine;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._Triad;

/// <summary>
/// The SafeCan examine label rides a marker component because the directed bus already has upstream subscribers on
/// the tank and canister components for ExaminedEvent. This proves the marker actually reaches the examine text on
/// the vessels players meet, and stays off the ones that only carry the gas components.
/// </summary>
[TestOf(typeof(GasVesselSuppressionSystem))]
public sealed class SafeCanLabelTest
{
    private static readonly string[] Labelled =
    {
        "AirTank",
        "OxygenTankFilled",
        "EmergencyOxygenTank",
        "JetpackBlue",
        "ClothingShoesBootsMagSyndie",
        "AirCanister",
        "PlasmaCanister",
        "StorageCanister",
    };

    private static readonly string[] Unlabelled =
    {
        "SpaceBladeBlue",
        "EnergyAirConverter",
    };

    [Test]
    public async Task VesselsAdvertiseSafeCanOnExamine()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var examine = entMan.System<ExamineSystem>();
        var testMap = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            // Ghosts pass the details-range gate unconditionally, so the examiner needs no positioning.
            var observer = entMan.SpawnEntity("MobObserver", testMap.GridCoords);

            Assert.Multiple(() =>
            {
                foreach (var id in Labelled)
                {
                    var vessel = entMan.SpawnEntity(id, testMap.GridCoords);
                    Assert.That(entMan.HasComponent<SafeCanLabelComponent>(vessel), Is.True, $"{id} lacks the marker");
                    Assert.That(examine.GetExamineText(vessel, observer).ToString(), Does.Contain("SafeCan"), id);
                }

                foreach (var id in Unlabelled)
                {
                    var vessel = entMan.SpawnEntity(id, testMap.GridCoords);
                    Assert.That(entMan.HasComponent<SafeCanLabelComponent>(vessel), Is.False, $"{id} carries the marker");
                    Assert.That(examine.GetExamineText(vessel, observer).ToString(), Does.Not.Contain("SafeCan"), id);
                }
            });
        });

        await pair.CleanReturnAsync();
    }
}
