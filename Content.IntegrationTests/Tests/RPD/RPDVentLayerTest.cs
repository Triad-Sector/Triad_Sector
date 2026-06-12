#nullable enable
using System.Linq;
using Content.Server.NodeContainer.Nodes;
using Content.Shared.NodeContainer;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Atmos.EntitySystems;
using Content.Shared.Coordinates;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.RPD;

[TestFixture]
public sealed class RPDVentLayerTest
{
    private const string Straight = "GasPipeStraight"; // longitudinal (North|South), Primary

    // Every layer-capable RPD recipe target (Vents + AtmosphericUtility categories, both mirror
    // prototypes included) and its expected alt-layer wiring.
    private static readonly (string Base, string Alt1, string Alt2)[] Devices =
    {
        ("GasVentPump", "GasVentPumpAlt1", "GasVentPumpAlt2"),
        ("GasPassiveVent", "GasPassiveVentAlt1", "GasPassiveVentAlt2"),
        ("GasVentScrubber", "GasVentScrubberAlt1", "GasVentScrubberAlt2"),
        ("GasOutletInjector", "GasOutletInjectorAlt1", "GasOutletInjectorAlt2"),
        ("GasDualPortVentPump", "GasDualPortVentPumpAlt1", "GasDualPortVentPumpAlt2"),
        ("GasFilter", "GasFilterAlt1", "GasFilterAlt2"),
        ("GasFilterFlipped", "GasFilterFlippedAlt1", "GasFilterFlippedAlt2"),
        ("GasMixer", "GasMixerAlt1", "GasMixerAlt2"),
        ("GasMixerFlipped", "GasMixerFlippedAlt1", "GasMixerFlippedAlt2"),
        ("PressureControlledValve", "PressureControlledValveAlt1", "PressureControlledValveAlt2"),
        ("GasPort", "GasPortAlt1", "GasPortAlt2"),
    };

    [Test]
    public async Task AlternativePrototypesResolve()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var compFactory = server.ResolveDependency<IComponentFactory>();
        var entMan = server.ResolveDependency<IEntityManager>();
        var layers = entMan.System<SharedAtmosPipeLayersSystem>();

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                foreach (var (baseId, alt1, alt2) in Devices)
                {
                    Assert.That(protoMan.TryIndex<EntityPrototype>(baseId, out var baseProto), Is.True, $"{baseId} missing");
                    Assert.That(baseProto!.TryGetComponent<AtmosPipeLayersComponent>(out var comp, compFactory), Is.True,
                        $"{baseId} has no AtmosPipeLayersComponent");

                    Assert.That(layers.TryGetAlternativePrototype(comp!, AtmosPipeLayer.Secondary, out var secondary), Is.True,
                        $"{baseId} has no Secondary alternative");
                    Assert.That(secondary.Id, Is.EqualTo(alt1));

                    Assert.That(layers.TryGetAlternativePrototype(comp!, AtmosPipeLayer.Tertiary, out var tertiary), Is.True,
                        $"{baseId} has no Tertiary alternative");
                    Assert.That(tertiary.Id, Is.EqualTo(alt2));

                    // The alt proto must carry its layer as prototype data: the layer has to be live before the
                    // anchor-time overlap check at spawn, which is the whole reason these are separate prototypes.
                    Assert.That(protoMan.TryIndex<EntityPrototype>(alt1, out var alt1Proto), Is.True, $"{alt1} missing");
                    Assert.That(alt1Proto!.TryGetComponent<AtmosPipeLayersComponent>(out var alt1Comp, compFactory), Is.True);
                    Assert.That(alt1Comp!.CurrentPipeLayer, Is.EqualTo(AtmosPipeLayer.Secondary), $"{alt1} pipeLayer");

                    Assert.That(protoMan.TryIndex<EntityPrototype>(alt2, out var alt2Proto), Is.True, $"{alt2} missing");
                    Assert.That(alt2Proto!.TryGetComponent<AtmosPipeLayersComponent>(out var alt2Comp, compFactory), Is.True);
                    Assert.That(alt2Comp!.CurrentPipeLayer, Is.EqualTo(AtmosPipeLayer.Tertiary), $"{alt2} pipeLayer");
                }
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AltVentCoexistsWithPrimaryPipe()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var mapMan = server.ResolveDependency<IMapManager>();
        var mapSys = entMan.System<SharedMapSystem>();

        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            var grid = mapMan.CreateGridEntity(mapId);
            mapSys.SetTile(grid, new Vector2i(0, 0), new Tile(1));
            entMan.SpawnEntity(Straight, grid.Owner.ToCoordinates(0, 0)); // North|South @ Primary

            // South-facing Secondary vent over a Primary pipe: layers differ, so the anchor-time overlap
            // check must let it stay, and its node must come up on Secondary before node groups form.
            var vent = entMan.SpawnEntity("GasVentPumpAlt1", grid.Owner.ToCoordinates(0, 0));

            Assert.Multiple(() =>
            {
                Assert.That(entMan.GetComponent<TransformComponent>(vent).Anchored, Is.True,
                    "a Secondary vent should anchor over a Primary pipe");
                Assert.That(entMan.GetComponent<AtmosPipeLayersComponent>(vent).CurrentPipeLayer,
                    Is.EqualTo(AtmosPipeLayer.Secondary));

                var nodes = entMan.GetComponent<NodeContainerComponent>(vent).Nodes.Values.OfType<PipeNode>().ToList();
                Assert.That(nodes, Is.Not.Empty, "vent has no pipe nodes");
                Assert.That(nodes.All(n => n.CurrentPipeLayer == AtmosPipeLayer.Secondary), Is.True,
                    "vent pipe node must be on the aimed layer at startup");
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SameLayerVentOverlapUnanchors()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var mapMan = server.ResolveDependency<IMapManager>();
        var mapSys = entMan.System<SharedMapSystem>();

        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            var grid = mapMan.CreateGridEntity(mapId);
            mapSys.SetTile(grid, new Vector2i(0, 0), new Tile(1));
            entMan.SpawnEntity(Straight, grid.Owner.ToCoordinates(0, 0)); // North|South @ Primary

            // Same layer, shared South direction: the startup overlap guard must still fire for vents.
            var vent = entMan.SpawnEntity("GasVentPump", grid.Owner.ToCoordinates(0, 0));

            Assert.That(entMan.GetComponent<TransformComponent>(vent).Anchored, Is.False,
                "a Primary vent sharing a direction with a Primary pipe must be rejected at anchor time");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SpawnRotationIsSeenByAnchorCheck()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var mapMan = server.ResolveDependency<IMapManager>();
        var mapSys = entMan.System<SharedMapSystem>();

        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            var grid = mapMan.CreateGridEntity(mapId);
            mapSys.SetTile(grid, new Vector2i(0, 0), new Tile(1));
            entMan.SpawnEntity(Straight, grid.Owner.ToCoordinates(0, 0)); // North|South @ Primary

            // Same layer but rotated out of conflict (vent port faces East). RCDSystem now passes the recipe
            // rotation into SpawnAttachedTo; this asserts the engine applies it before the anchor-time check,
            // which previously judged everything south-facing.
            var vent = entMan.SpawnAttachedTo("GasVentPump", grid.Owner.ToCoordinates(0, 0),
                rotation: Direction.East.ToAngle());

            Assert.That(entMan.GetComponent<TransformComponent>(vent).Anchored, Is.True,
                "an East-facing Primary vent does not share a direction with a North-South pipe and must anchor");
        });

        await pair.CleanReturnAsync();
    }
}
