#nullable enable
using Content.Server.Atmos.EntitySystems;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Coordinates;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.RPD;

[TestFixture]
public sealed class RPDCoexistenceTest
{
    // A straight pipe spawns longitudinal (North|South). Its alt-layer prototypes carry pipeLayer 1/2.
    private const string Straight = "GasPipeStraight";

    [Test]
    public async Task DifferentLayerCoexists()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var mapMan = server.ResolveDependency<IMapManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var overlap = entMan.System<PipeRestrictOverlapSystem>();

        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            var grid = mapMan.CreateGridEntity(mapId);
            mapSys.SetTile(grid, new Vector2i(0, 0), new Tile(1));
            entMan.SpawnEntity(Straight, grid.Owner.ToCoordinates(0, 0)); // Primary, anchored

            // Same proto, same rotation, DIFFERENT layer => no overlap, coexists.
            Assert.That(
                overlap.WouldPlacementOverlap((grid.Owner, grid.Comp), new Vector2i(0, 0), Straight, Angle.Zero, AtmosPipeLayer.Secondary),
                Is.False, "a Secondary pipe should coexist with a Primary pipe on the same tile");

            // Same proto, same rotation, SAME layer => overlap, rejected.
            Assert.That(
                overlap.WouldPlacementOverlap((grid.Owner, grid.Comp), new Vector2i(0, 0), Straight, Angle.Zero, AtmosPipeLayer.Primary),
                Is.True, "a second Primary pipe should conflict on the same tile");

            // Same layer, PERPENDICULAR (rotate 90 deg) => directions don't intersect, coexists.
            Assert.That(
                overlap.WouldPlacementOverlap((grid.Owner, grid.Comp), new Vector2i(0, 0), Straight, Angle.FromDegrees(90), AtmosPipeLayer.Primary),
                Is.False, "a perpendicular pipe on the same layer should coexist (no shared direction)");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ScrewdriverdLayerIsRespected()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var mapMan = server.ResolveDependency<IMapManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var layers = entMan.System<AtmosPipeLayersSystem>();
        var overlap = entMan.System<PipeRestrictOverlapSystem>();

        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            var grid = mapMan.CreateGridEntity(mapId);
            mapSys.SetTile(grid, new Vector2i(0, 0), new Tile(1));
            var pipe = entMan.SpawnEntity(Straight, grid.Owner.ToCoordinates(0, 0)); // Primary

            // Move the existing pipe to Secondary via the layer system (proto stays GasPipeStraight).
            var pipeLayers = entMan.GetComponent<AtmosPipeLayersComponent>(pipe);
            layers.SetPipeLayer((pipe, pipeLayers), AtmosPipeLayer.Secondary);

            // A new Secondary now conflicts (live layer match), even though protos differ.
            Assert.That(
                overlap.WouldPlacementOverlap((grid.Owner, grid.Comp), new Vector2i(0, 0), Straight, Angle.Zero, AtmosPipeLayer.Secondary),
                Is.True, "placement must use the existing pipe's live layer, not its prototype layer");

            // Primary is now free.
            Assert.That(
                overlap.WouldPlacementOverlap((grid.Owner, grid.Comp), new Vector2i(0, 0), Straight, Angle.Zero, AtmosPipeLayer.Primary),
                Is.False, "Primary should be free after the existing pipe moved to Secondary");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RpdValidationAllowsDifferentLayerRejectsSame()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var mapMan = server.ResolveDependency<IMapManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var layers = entMan.System<AtmosPipeLayersSystem>();
        var rcd = entMan.System<Content.Shared.RCD.Systems.RCDSystem>();

        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            var grid = mapMan.CreateGridEntity(mapId);
            mapSys.SetTile(grid, new Vector2i(0, 0), new Tile(1));
            var existing = entMan.SpawnEntity(Straight, grid.Owner.ToCoordinates(0, 0)); // Primary

            var user = entMan.SpawnEntity(null, grid.Owner.ToCoordinates(0, 0));
            var rpdTool = entMan.SpawnEntity("RPD", grid.Owner.ToCoordinates(0, 0));
            // The RPD's default recipe (first available) is a layer-capable pipe, and CurrentLayer defaults to
            // Primary. We drive the two cases by moving the EXISTING pipe's layer, avoiding any access-restricted
            // write to RPDComponent/RCDComponent from the test.
            var rcdComp = entMan.GetComponent<Content.Shared.RCD.Components.RCDComponent>(rpdTool);

            Assert.That(rcd.TryGetMapGridData(grid.Owner.ToCoordinates(0, 0), user, out var data), Is.True);

            // RPD (Primary) over an existing Primary pipe on the same tile is rejected.
            Assert.That(rcd.IsConstructionLocationValid(rpdTool, rcdComp, data!.Value, user, popMsgs: false), Is.False,
                "RPD should reject placing on a tile already occupied at the same pipe layer");

            // Move the existing pipe to Secondary; the RPD (still Primary) now coexists, so placement is valid.
            var existingLayers = entMan.GetComponent<AtmosPipeLayersComponent>(existing);
            layers.SetPipeLayer((existing, existingLayers), AtmosPipeLayer.Secondary);

            Assert.That(rcd.IsConstructionLocationValid(rpdTool, rcdComp, data!.Value, user, popMsgs: false), Is.True,
                "RPD should allow placing on a tile whose pipe sits on a different layer");
        });

        await pair.CleanReturnAsync();
    }
}
