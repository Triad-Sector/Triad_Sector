using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Components;
using Content.Server.NodeContainer.Nodes;
using Content.Shared.NodeContainer;
using Content.Shared.Atmos;
using Content.Shared.Damage;
using Content.Shared._Triad.Atmos;
using Content.Shared._Triad.CCVar;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad;

/// <summary>
/// Behavioral tests for the Triad radiator rework: temperature-driven
/// body-as-hub cooling, flow-scaled conductance, the overheat ceiling and the
/// thermal bucket. See the radiator overhaul design spec.
/// </summary>
[TestOf(typeof(HeatExchangerSystem))]
public sealed class RadiatorTest
{
    private const string RadiatorProtoId = "HeatExchanger";

    private static async Task<(EntityUid Radiator, PipeNode Inlet, PipeNode Outlet)> SpawnRadiator(
        Pair.TestPair pair, TestMapData testMap)
    {
        var server = pair.Server;
        var entMan = server.EntMan;
        var atmosSystem = entMan.System<AtmosphereSystem>();
        var transformSystem = entMan.System<SharedTransformSystem>();

        EntityUid radiator = default;
        await server.WaitPost(() =>
        {
            var gridAtmos = entMan.EnsureComponent<GridAtmosphereComponent>(testMap.Grid);
            atmosSystem.RebuildGridAtmosphere((testMap.Grid.Owner, gridAtmos, testMap.Grid.Comp));

            radiator = entMan.Spawn(RadiatorProtoId);
            transformSystem.SetCoordinates(radiator, testMap.GridCoords);
            transformSystem.AnchorEntity(radiator);
        });

        // Let node groups form and the device join the grid atmosphere.
        server.RunTicks(30);
        await server.WaitIdleAsync();

        PipeNode inlet = default!;
        PipeNode outlet = default!;
        await server.WaitPost(() =>
        {
            var nodes = entMan.GetComponent<NodeContainerComponent>(radiator);
            inlet = (PipeNode) nodes.Nodes["inlet"];
            outlet = (PipeNode) nodes.Nodes["outlet"];
        });

        return (radiator, inlet, outlet);
    }

    private static float PipeHeatContent(AtmosphereSystem atmos, PipeNode node)
        => atmos.GetHeatCapacity(node.Air, true) * node.Air.Temperature;

    /// <summary>
    /// The headline regression: a radiator with hot gas and ZERO pressure
    /// differential across it must still cool. Under the old ΔP-gated law this
    /// setup transferred nothing at all.
    /// </summary>
    [Test]
    public async Task IdleRadiatorCoolsWithoutPressureDifferential()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var atmosSystem = entMan.System<AtmosphereSystem>();

        var testMap = await pair.CreateTestMap();
        var (radiator, inlet, outlet) = await SpawnRadiator(pair, testMap);

        float startTemp = 600f;
        await server.WaitPost(() =>
        {
            // Identical moles and temperature on both nets: equal pressure, no flow.
            inlet.Air.AdjustMoles(Gas.Nitrogen, 50f);
            outlet.Air.AdjustMoles(Gas.Nitrogen, 50f);
            inlet.Air.Temperature = startTemp;
            outlet.Air.Temperature = startTemp;
        });

        server.RunTicks(300);
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            // Test-map tiles are space, so this is the vacuum case: the only
            // rejection path is Stefan-Boltzmann radiation toward TCMB, which
            // the old ΔP-gated law could never exercise at zero flow.
            // (Convection into a real room is covered by the pump-loop
            // benchmark rig, not this test.)
            var environment = atmosSystem.GetContainingMixture(radiator, true, true);
            var envMoles = environment?.TotalMoles ?? 0f;
            Assert.Multiple(() =>
            {
                Assert.That(envMoles, Is.EqualTo(0f).Within(0.001f),
                    "Rig assumption broken: expected a vacuum tile (this test covers the radiation path).");
                Assert.That(inlet.Air.Temperature, Is.LessThan(startTemp - 20f),
                    "Idle radiator failed to cool its inlet net without a pressure differential.");
                Assert.That(outlet.Air.Temperature, Is.LessThan(startTemp - 20f),
                    "Idle radiator failed to cool its outlet net without a pressure differential.");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The anti-inline check: a radiator with flow through it must cool
    /// substantially faster than the same radiator with flow disabled.
    /// Without this split, static stubs would rebuild the inline exploit.
    /// </summary>
    [Test]
    public async Task FlowingRadiatorBeatsStaticRadiator()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var atmosSystem = entMan.System<AtmosphereSystem>();

        // Two identical rigs on separate maps (same-tile pipe nodes would merge
        // into one pipenet and wreck the comparison): A flows (throttle enabled
        // and a big ΔP to drive it), B has its throttle zeroed so it only ever
        // gets the static conductance floor.
        var testMapA = await pair.CreateTestMap();
        var testMapB = await pair.CreateTestMap();
        var (radiatorA, inletA, outletA) = await SpawnRadiator(pair, testMapA);
        var (radiatorB, inletB, outletB) = await SpawnRadiator(pair, testMapB);

        await server.WaitPost(() =>
        {
            var compA = entMan.GetComponent<HeatExchangerComponent>(radiatorA);
            var compB = entMan.GetComponent<HeatExchangerComponent>(radiatorB);

            // Isolate the gas↔body conductance split: no environment exchange
            // (alpha = K = 0), body pinned cold as an effectively infinite sink.
            // Otherwise body↔environment rejection bottlenecks both rigs
            // identically and masks the difference.
            foreach (var comp in new[] { compA, compB })
            {
                comp.alpha = 0f;
                comp.K = 0f;
                comp.BodyHeatCapacity = 1e9f;
                comp.BodyTemperature = Atmospherics.T20C;
            }
            compB.G = 0f; // no flow ever; static floor only

            // A gets nearly all its gas on the inlet side so ΔP drives a big
            // flow through the device (the outlet keeps a seed so the throttle
            // formula's temperature linearization stays sane); B is split
            // evenly, so no flow even before G=0. Heavy loads keep per-update
            // convergence partial so the rate difference stays visible.
            inletA.Air.AdjustMoles(Gas.Nitrogen, 990f);
            outletA.Air.AdjustMoles(Gas.Nitrogen, 10f);
            inletB.Air.AdjustMoles(Gas.Nitrogen, 500f);
            outletB.Air.AdjustMoles(Gas.Nitrogen, 500f);
            inletA.Air.Temperature = 600f;
            outletA.Air.Temperature = 600f;
            inletB.Air.Temperature = 600f;
            outletB.Air.Temperature = 600f;
        });

        // Deterministic: raise exactly one device update by hand instead of
        // racing the atmos scheduler (whose cadence let both rigs converge
        // fully to the sink, hiding the conductance split). One update is the
        // honest window: A's flow bonus only lasts until its nets equalize.
        // Null grid skips the tile gate; the physics path is identical.
        await server.WaitPost(() =>
        {
            var ev = new AtmosDeviceUpdateEvent(0.5f, null, null);
            entMan.EventBus.RaiseLocalEvent(radiatorA, ref ev);
            entMan.EventBus.RaiseLocalEvent(radiatorB, ref ev);
        });

        await server.WaitAssertion(() =>
        {
            // Moles-weighted mean temperature per rig; the body is the only sink.
            var tempA = (inletA.Air.Temperature * inletA.Air.TotalMoles + outletA.Air.Temperature * outletA.Air.TotalMoles)
                        / (inletA.Air.TotalMoles + outletA.Air.TotalMoles);
            var tempB = (inletB.Air.Temperature * inletB.Air.TotalMoles + outletB.Air.Temperature * outletB.Air.TotalMoles)
                        / (inletB.Air.TotalMoles + outletB.Air.TotalMoles);

            Assert.Multiple(() =>
            {
                Assert.That(tempB, Is.LessThan(600f),
                    "Static radiator transferred no heat at all; the static floor is dead.");
                Assert.That(600f - tempA, Is.GreaterThan((600f - tempB) * 2f),
                    $"Flow-scaled conductance too weak: flowing rig cooled to {tempA:F0} K vs static rig {tempB:F0} K. " +
                    "The forced/natural split is what keeps pumped loops ahead of passive stubs.");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The mass-flux law: two rigs at the same pressure ratio, one ten times
    /// denser, must NOT hand the same joules to the body. Under the old clamp
    /// both saturated at flowFactor 1 and transferred identically; now the
    /// dense loop moves ten times the moles per pass and its wall conductance
    /// follows, up to the cap. This is what makes loop pressure a decision.
    /// </summary>
    [Test]
    public async Task DenserLoopTransfersMoreHeatThroughTheWall()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var atmosSystem = entMan.System<AtmosphereSystem>();

        var testMapThin = await pair.CreateTestMap();
        var testMapDense = await pair.CreateTestMap();
        var (radiatorThin, inletThin, outletThin) = await SpawnRadiator(pair, testMapThin);
        var (radiatorDense, inletDense, outletDense) = await SpawnRadiator(pair, testMapDense);

        const float dt = 0.1f;
        await server.WaitPost(() =>
        {
            var compThin = entMan.GetComponent<HeatExchangerComponent>(radiatorThin);
            var compDense = entMan.GetComponent<HeatExchangerComponent>(radiatorDense);

            foreach (var comp in new[] { compThin, compDense })
            {
                // Body pinned as an infinite cold sink, no environment exchange,
                // and a deliberately small wall conductance so the exchange
                // stays in its linear regime (dE ≈ conductance × ΔT × dt) and
                // the joules moved read the conductance directly instead of
                // both rigs converging to the sink.
                comp.alpha = 0f;
                comp.K = 0f;
                comp.BodyHeatCapacity = 1e9f;
                comp.BodyTemperature = Atmospherics.T20C;
                comp.PipeConductance = 100f;
            }

            // Same 100:1 pressure ratio, 10x the absolute pressure. The thin
            // rig's pass lands at about RatedFlow (factor ~1); the dense rig's
            // lands well past it and hits MaxFlowFactor.
            inletThin.Air.AdjustMoles(Gas.Nitrogen, 20f);
            outletThin.Air.AdjustMoles(Gas.Nitrogen, 0.2f);
            inletDense.Air.AdjustMoles(Gas.Nitrogen, 200f);
            outletDense.Air.AdjustMoles(Gas.Nitrogen, 2f);
            foreach (var node in new[] { inletThin, outletThin, inletDense, outletDense })
                node.Air.Temperature = 600f;
        });

        float heatThinBefore = 0f, heatDenseBefore = 0f;
        await server.WaitPost(() =>
        {
            heatThinBefore = PipeHeatContent(atmosSystem, inletThin) + PipeHeatContent(atmosSystem, outletThin);
            heatDenseBefore = PipeHeatContent(atmosSystem, inletDense) + PipeHeatContent(atmosSystem, outletDense);

            // One hand-raised update each; gas moved between nets conserves
            // heat content, so the drop is exactly what crossed into the body.
            var ev = new AtmosDeviceUpdateEvent(dt, null, null);
            entMan.EventBus.RaiseLocalEvent(radiatorThin, ref ev);
            entMan.EventBus.RaiseLocalEvent(radiatorDense, ref ev);
        });

        await server.WaitAssertion(() =>
        {
            var compThin = entMan.GetComponent<HeatExchangerComponent>(radiatorThin);
            var joulesThin = heatThinBefore - PipeHeatContent(atmosSystem, inletThin) - PipeHeatContent(atmosSystem, outletThin);
            var joulesDense = heatDenseBefore - PipeHeatContent(atmosSystem, inletDense) - PipeHeatContent(atmosSystem, outletDense);

            // Expected ratio is (floor + (1-floor) × cap) / (floor + (1-floor) × ~1)
            // ≈ 2.8 at the defaults; assert the clamp-era answer (1.0) is gone
            // by a wide margin, and that the cap actually bounds it.
            var capRatio = (compThin.StaticConductanceFloor + (1f - compThin.StaticConductanceFloor) * compThin.MaxFlowFactor)
                           / compThin.StaticConductanceFloor;
            Assert.Multiple(() =>
            {
                Assert.That(joulesThin, Is.GreaterThan(0f), "Thin rig transferred nothing; the wall is dead.");
                Assert.That(joulesDense, Is.GreaterThan(joulesThin * 2f),
                    $"Mass-flux law not biting: dense loop moved {joulesDense:F0} J vs thin {joulesThin:F0} J. " +
                    "Loop pressure is supposed to buy wall conductance.");
                Assert.That(joulesDense, Is.LessThan(joulesThin * capRatio),
                    $"Dense loop moved {joulesDense:F0} J vs thin {joulesThin:F0} J, past what MaxFlowFactor allows.");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Overheat ceiling: a white-hot radiator takes structural damage while the
    /// cvar is on and none while it is off. Also proves the radiator's damage
    /// container actually accepts the Structural type.
    /// </summary>
    [Test]
    public async Task WhiteHotRadiatorTakesDamageWhenCvarEnabled()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;

        var testMap = await pair.CreateTestMap();
        var (radiator, _, _) = await SpawnRadiator(pair, testMap);

        // Cvar off first: no damage expected.
        await server.WaitPost(() =>
        {
            server.CfgMan.SetCVar(TriadCCVars.RadiatorOverheatDamage, false);
            var comp = entMan.GetComponent<HeatExchangerComponent>(radiator);
            // Huge body capacity so radiation can't pull it out of the White
            // bucket during the test window.
            comp.BodyHeatCapacity = 1e9f;
            comp.BodyTemperature = 2000f;
        });

        server.RunTicks(120);
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            var damageable = entMan.GetComponent<DamageableComponent>(radiator);
            Assert.That(damageable.TotalDamage.Float(), Is.EqualTo(0f).Within(0.01f),
                "Radiator took overheat damage with the cvar disabled.");

            var comp = entMan.GetComponent<HeatExchangerComponent>(radiator);
            Assert.That(comp.Bucket, Is.EqualTo(RadiatorThermalBucket.White),
                "Radiator body at 2000 K is not in the White bucket.");

            server.CfgMan.SetCVar(TriadCCVars.RadiatorOverheatDamage, true);
        });

        server.RunTicks(120);
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            var damageable = entMan.GetComponent<DamageableComponent>(radiator);
            Assert.That(damageable.TotalDamage.Float(), Is.GreaterThan(0f),
                "White-hot radiator took no structural damage with the cvar enabled. " +
                "Check that the radiator's damage container accepts the Structural type.");

            // Pool hygiene: restore the default.
            server.CfgMan.SetCVar(TriadCCVars.RadiatorOverheatDamage, true);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The thermal bucket tracks body temperature and lands where the
    /// boundaries say it should.
    /// </summary>
    [Test]
    public async Task BucketTracksBodyTemperature()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;

        var testMap = await pair.CreateTestMap();
        var (radiator, _, _) = await SpawnRadiator(pair, testMap);

        await server.WaitPost(() =>
        {
            var comp = entMan.GetComponent<HeatExchangerComponent>(radiator);
            comp.BodyHeatCapacity = 1e9f; // hold the temperature still
            comp.BodyTemperature = 1150f; // CherryRed band (1088-1255, Chapman's cherry heat)
        });

        server.RunTicks(30);
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<HeatExchangerComponent>(radiator);
            Assert.That(comp.Bucket, Is.EqualTo(RadiatorThermalBucket.CherryRed),
                "Radiator body at 700 K did not land in the CherryRed bucket.");

            var appearance = entMan.GetComponent<AppearanceComponent>(radiator);
            var appearanceSystem = entMan.System<SharedAppearanceSystem>();
            Assert.That(
                appearanceSystem.TryGetData<RadiatorThermalBucket>(radiator, RadiatorVisuals.Bucket, out var bucket, appearance)
                && bucket == RadiatorThermalBucket.CherryRed,
                "Appearance data does not carry the CherryRed bucket.");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Three radiators stacked port-to-port report end/middle/end connection
    /// states via appearance; unanchoring the middle reverts all to isolated.
    /// </summary>
    [Test]
    public async Task RadiatorLinks_ThreeInARow_AppearanceStates()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var atmos = entMan.System<AtmosphereSystem>();
        var mapSys = entMan.System<SharedMapSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var appearance = entMan.System<SharedAppearanceSystem>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();

        var testMap = await pair.CreateTestMap();
        var rads = new EntityUid[3];
        EntityUid loner = default;

        await server.WaitPost(() =>
        {
            var plating = new Tile(tileDefs["Plating"].TileId);
            for (var i = 0; i < 3; i++)
                mapSys.SetTile(testMap.Grid, new Vector2i(0, -i), plating);
            mapSys.SetTile(testMap.Grid, new Vector2i(5, 0), plating);

            var gridAtmos = entMan.EnsureComponent<GridAtmosphereComponent>(testMap.Grid);
            atmos.RebuildGridAtmosphere((testMap.Grid.Owner, gridAtmos, testMap.Grid.Comp));

            for (var i = 0; i < 3; i++)
            {
                rads[i] = entMan.SpawnEntity("HeatExchanger",
                    mapSys.GridTileToLocal(testMap.Grid.Owner, testMap.Grid.Comp, new Vector2i(0, -i)));
                if (!entMan.GetComponent<TransformComponent>(rads[i]).Anchored)
                    xformSys.AnchorEntity(rads[i]);
            }
            loner = entMan.SpawnEntity("HeatExchanger",
                mapSys.GridTileToLocal(testMap.Grid.Owner, testMap.Grid.Comp, new Vector2i(5, 0)));
            if (!entMan.GetComponent<TransformComponent>(loner).Anchored)
                xformSys.AnchorEntity(loner);
        });

        // Links refresh on the atmos update cadence (~0.5 s); give it two.
        server.RunTicks(60);
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            // Inlet faces North (up, +y); the column runs downward, so the
            // top radiator's OUTLET meets the middle's INLET.
            Assert.That(GetConnState(entMan, appearance, rads[0]), Is.EqualTo(RadiatorConnectionState.CapIn),
                "top: outlet linked, cap should remain at inlet end");
            Assert.That(GetConnState(entMan, appearance, rads[1]), Is.EqualTo(RadiatorConnectionState.Middle));
            Assert.That(GetConnState(entMan, appearance, rads[2]), Is.EqualTo(RadiatorConnectionState.CapOut),
                "bottom: inlet linked, cap should remain at outlet end");
            Assert.That(GetConnState(entMan, appearance, loner), Is.EqualTo(RadiatorConnectionState.Isolated));
        });

        await server.WaitPost(() => xformSys.Unanchor(rads[1]));
        server.RunTicks(60);
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            Assert.That(GetConnState(entMan, appearance, rads[0]), Is.EqualTo(RadiatorConnectionState.Isolated));
            Assert.That(GetConnState(entMan, appearance, rads[1]), Is.EqualTo(RadiatorConnectionState.Isolated));
            Assert.That(GetConnState(entMan, appearance, rads[2]), Is.EqualTo(RadiatorConnectionState.Isolated));
        });

        await pair.CleanReturnAsync();
    }

    private static RadiatorConnectionState GetConnState(IEntityManager entMan,
        SharedAppearanceSystem appearance, EntityUid uid)
    {
        // Isolated radiators may never have had data set; that counts as Isolated.
        return appearance.TryGetData<RadiatorConnectionState>(uid, RadiatorVisuals.Connections, out var state)
            ? state
            : RadiatorConnectionState.Isolated;
    }

    /// <summary>
    /// Two linked radiators with no gas and environment exchange zeroed:
    /// body heat must walk from the hot one to the cold one, conserving
    /// total body energy (equal heat capacities -> symmetric convergence).
    /// </summary>
    [Test]
    public async Task RadiatorDiffusion_TwoLinked_HeatWalksAndConserves()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var atmos = entMan.System<AtmosphereSystem>();
        var mapSys = entMan.System<SharedMapSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();

        var testMap = await pair.CreateTestMap();
        EntityUid hot = default, cold = default;
        HeatExchangerComponent hotComp = default!, coldComp = default!;

        await server.WaitPost(() =>
        {
            var plating = new Tile(tileDefs["Plating"].TileId);
            mapSys.SetTile(testMap.Grid, new Vector2i(0, 0), plating);
            mapSys.SetTile(testMap.Grid, new Vector2i(0, -1), plating);

            var gridAtmos = entMan.EnsureComponent<GridAtmosphereComponent>(testMap.Grid);
            atmos.RebuildGridAtmosphere((testMap.Grid.Owner, gridAtmos, testMap.Grid.Comp));

            hot = entMan.SpawnEntity("HeatExchanger",
                mapSys.GridTileToLocal(testMap.Grid.Owner, testMap.Grid.Comp, new Vector2i(0, 0)));
            cold = entMan.SpawnEntity("HeatExchanger",
                mapSys.GridTileToLocal(testMap.Grid.Owner, testMap.Grid.Comp, new Vector2i(0, -1)));
            foreach (var uid in new[] { hot, cold })
            {
                if (!entMan.GetComponent<TransformComponent>(uid).Anchored)
                    xformSys.AnchorEntity(uid);
            }

            hotComp = entMan.GetComponent<HeatExchangerComponent>(hot);
            coldComp = entMan.GetComponent<HeatExchangerComponent>(cold);

            // Isolate the diffusion term: no radiation, no convection.
            foreach (var comp in new[] { hotComp, coldComp })
            {
                comp.alpha = 0f;
                comp.K = 0f;
            }

            hotComp.BodyTemperature = 1000f;
            coldComp.BodyTemperature = 300f;
        });

        var energyBefore = 0f;
        await server.WaitPost(() =>
            energyBefore = hotComp.BodyTemperature * hotComp.BodyHeatCapacity
                         + coldComp.BodyTemperature * coldComp.BodyHeatCapacity);

        server.RunTicks(300); // ~10 s: several atmos updates of diffusion
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            Assert.That(hotComp.BodyTemperature - coldComp.BodyTemperature, Is.LessThan(200f),
                "bodies failed to converge: diffusion term not running");
            Assert.That(hotComp.BodyTemperature, Is.LessThan(900f), "hot body never cooled");
            Assert.That(coldComp.BodyTemperature, Is.GreaterThan(400f), "cold body never warmed");
            Assert.That(coldComp.BodyTemperature, Is.LessThanOrEqualTo(hotComp.BodyTemperature + 0.5f),
                "diffusion overshot past equilibrium");

            var energyAfter = hotComp.BodyTemperature * hotComp.BodyHeatCapacity
                            + coldComp.BodyTemperature * coldComp.BodyHeatCapacity;
            Assert.That(MathF.Abs(energyAfter - energyBefore) / energyBefore, Is.LessThan(0.001f),
                "body-to-body diffusion must conserve energy");
        });

        await pair.CleanReturnAsync();
    }
}
