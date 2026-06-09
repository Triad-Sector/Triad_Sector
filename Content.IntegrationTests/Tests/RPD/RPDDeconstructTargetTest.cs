#nullable enable
using Content.Server.RPD;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Coordinates;
using Content.Shared.RCD;
using Content.Shared.RPD.Components;
using Content.Shared.RPD.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.RPD;

[TestFixture]
public sealed class RPDDeconstructTargetTest
{
    [Test]
    public async Task DeconstructTargetsAimedLayer()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var mapMan = server.ResolveDependency<IMapManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var rcd = entMan.System<Content.Shared.RCD.Systems.RCDSystem>();
        var rpd = entMan.System<RPDSystem>();

        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            var grid = mapMan.CreateGridEntity(mapId);
            mapSys.SetTile(grid, new Vector2i(0, 0), new Tile(1));

            // Two coexisting pipes on the same tile: Primary and Secondary (alt-layer prototype on layer 1).
            var primary = entMan.SpawnEntity("GasPipeStraight", grid.Owner.ToCoordinates(0, 0));
            var secondary = entMan.SpawnEntity("GasPipeStraightAlt1", grid.Owner.ToCoordinates(0, 0));

            var rpdTool = entMan.SpawnEntity("RPD", grid.Owner.ToCoordinates(0, 0));
            var rpdComp = entMan.GetComponent<RPDComponent>(rpdTool);

            Assert.That(rcd.TryGetMapGridData(grid.Owner.ToCoordinates(0, 0), rpdTool, out var data), Is.True);

            // Aim at Secondary -> resolve picks the Secondary pipe.
            rpd.SetLayer((rpdTool, rpdComp), AtmosPipeLayer.Secondary);
            var r1 = new RCDDeconstructTargetResolveEvent(data!.Value, null);
            entMan.EventBus.RaiseLocalEvent(rpdTool, ref r1);
            Assert.That(r1.Target, Is.EqualTo(secondary), "aiming Secondary should target the Secondary pipe");

            // Aim at Primary -> resolve picks the Primary pipe.
            rpd.SetLayer((rpdTool, rpdComp), AtmosPipeLayer.Primary);
            var r2 = new RCDDeconstructTargetResolveEvent(data!.Value, null);
            entMan.EventBus.RaiseLocalEvent(rpdTool, ref r2);
            Assert.That(r2.Target, Is.EqualTo(primary), "aiming Primary should target the Primary pipe");
        });

        await pair.CleanReturnAsync();
    }
}
