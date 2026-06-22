using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Monitor.Components;
using Content.Shared.Atmos;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.Atmos;

/// <summary>
/// Test for determining that an AtmosMonitoringComponent/System correctly references
/// the GasMixture of the tile it is on if the tile's GasMixture ever changes.
/// </summary>
/// <remarks>
/// Triad: adapted from the upstream test bundled with wizden #41585 — rewritten against
/// our PoolManager harness (we don't carry the AtmosTest fixture layer or its test maps).
/// </remarks>
[TestOf(typeof(Atmospherics))]
public sealed class AtmosMonitoringTest
{
    private const string AirSensorProtoId = "AirSensor";
    private const string WallProtoId = "WallSolid";

    /// <summary>
    /// Tests if the monitor properly nulls out its reference to the tile mixture
    /// when a wall is placed on top of it, and restores the reference when the wall is removed.
    /// </summary>
    [Test]
    public async Task NullOutTileAtmosphereGasMixture()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        var entMan = server.EntMan;
        var atmosSystem = entMan.System<AtmosphereSystem>();
        var transformSystem = entMan.System<SharedTransformSystem>();

        var testMap = await pair.CreateTestMap();

        EntityUid airSensor = default;
        await server.WaitPost(() =>
        {
            // Give the grid an atmosphere with roundstart air on every tile.
            var gridAtmos = entMan.EnsureComponent<GridAtmosphereComponent>(testMap.Grid);
            atmosSystem.RebuildGridAtmosphere((testMap.Grid.Owner, gridAtmos, testMap.Grid.Comp));

            airSensor = entMan.Spawn(AirSensorProtoId);
            transformSystem.SetCoordinates(airSensor, testMap.GridCoords);
            transformSystem.AnchorEntity(airSensor);
        });

        // Let atmos processing run so the monitor binds the tile mixture.
        server.RunTicks(60);
        await server.WaitIdleAsync();

        EntityUid wall = default;
        await server.WaitAssertion(() =>
        {
            var atmosMonitor = entMan.GetComponent<AtmosMonitorComponent>(airSensor);
            var tileMixture = atmosSystem.GetContainingMixture(airSensor);

            Assert.That(tileMixture, Is.Not.Null, "Tile mixture is null after grid atmosphere rebuild.");
            Assert.That(atmosMonitor.TileGas, Is.SameAs(tileMixture),
                "Atmos monitor's TileGas does not match actual tile mixture after spawn.");

            // Now drop a wall on the same tile; the tile's mixture should be removed.
            // SpawnEntity at coords so the wall's prototype anchoring engages on the grid tile.
            wall = entMan.SpawnEntity(WallProtoId, testMap.GridCoords);
        });

        server.RunTicks(60);
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            var atmosMonitor = entMan.GetComponent<AtmosMonitorComponent>(airSensor);
            Assert.That(atmosMonitor.TileGas, Is.Null,
                "Atmos monitor's TileGas is not null after wall placed on top. Possible dead reference.");

            entMan.DeleteEntity(wall);
        });

        server.RunTicks(60);
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            var atmosMonitor = entMan.GetComponent<AtmosMonitorComponent>(airSensor);
            var newTileMixture = atmosSystem.GetContainingMixture(airSensor);

            Assert.That(newTileMixture, Is.Not.Null, "Tile mixture is null after wall removed.");
            Assert.That(atmosMonitor.TileGas, Is.SameAs(newTileMixture),
                "Atmos monitor's TileGas does not match actual tile mixture after wall removed.");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Tests if the monitor properly updates its reference to the tile mixture
    /// when the grid atmosphere is rebuilt (fixgridatmos).
    /// </summary>
    [Test]
    public async Task FixGridAtmosReplaceMixtureOnTileChange()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        var entMan = server.EntMan;
        var atmosSystem = entMan.System<AtmosphereSystem>();
        var transformSystem = entMan.System<SharedTransformSystem>();

        var testMap = await pair.CreateTestMap();

        EntityUid airSensor = default;
        await server.WaitPost(() =>
        {
            var gridAtmos = entMan.EnsureComponent<GridAtmosphereComponent>(testMap.Grid);
            atmosSystem.RebuildGridAtmosphere((testMap.Grid.Owner, gridAtmos, testMap.Grid.Comp));

            airSensor = entMan.Spawn(AirSensorProtoId);
            transformSystem.SetCoordinates(airSensor, testMap.GridCoords);
            transformSystem.AnchorEntity(airSensor);
        });

        server.RunTicks(60);
        await server.WaitIdleAsync();

        GasMixture tileMixture = null;
        await server.WaitAssertion(() =>
        {
            var atmosMonitor = entMan.GetComponent<AtmosMonitorComponent>(airSensor);
            tileMixture = atmosSystem.GetContainingMixture(airSensor);

            Assert.That(tileMixture, Is.Not.Null, "Tile mixture is null after grid atmosphere rebuild.");
            Assert.That(atmosMonitor.TileGas, Is.SameAs(tileMixture),
                "Atmos monitor's TileGas does not match actual tile mixture after spawn.");

            // Rebuild the grid atmosphere: every tile gets a brand new mixture instance.
            var gridAtmos = entMan.GetComponent<GridAtmosphereComponent>(testMap.Grid);
            atmosSystem.RebuildGridAtmosphere((testMap.Grid.Owner, gridAtmos, testMap.Grid.Comp));
        });

        server.RunTicks(60);
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            var atmosMonitor = entMan.GetComponent<AtmosMonitorComponent>(airSensor);

            // EXTREMELY IMPORTANT: The reference to the tile mixture on the tile should be completely different.
            var newTileMixture = atmosSystem.GetContainingMixture(airSensor);
            Assert.That(newTileMixture, Is.Not.SameAs(tileMixture),
                "Tile mixture is the same instance after fixgridatmos was ran. It should be a new instance.");

            // The monitor's ref to the tile mixture should have updated too.
            Assert.That(atmosMonitor.TileGas, Is.SameAs(newTileMixture),
                "Atmos monitor's TileGas does not match actual tile mixture after fixgridatmos was ran.");
        });

        await pair.CleanReturnAsync();
    }
}
