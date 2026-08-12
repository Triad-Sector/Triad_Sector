#nullable enable
using System.Numerics;
using Content.Server.Construction.Completions;
using Content.Shared.Coordinates;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.Construction;

/// <summary>
/// Players on rotated ship grids reported structures being built skewed, or losing their rotation entirely,
/// across pipes, conveyors, tube light fixtures and wall buttons.
///
/// These are pins, not a fix. Both server-side rotation paths below test clean on a rotated grid, patched or not,
/// which is what ruled the server out and sent the search to the client; the cause turned out to be the screen-vs
/// -grid frame mix-up covered by <c>ConstructionGhostRotationTest</c>.
///
/// They are kept because engine 287's <c>AnchorEntity</c> genuinely does rewrite local rotation to preserve WORLD
/// rotation when anchoring also reparents, which would skew the result by exactly the grid's rotation. Content
/// cannot reach that branch today: anchoring refuses any grid other than the entity's own <c>GridUid</c>, and
/// <c>GridUid</c> is inherited down the transform tree, so the delta is always zero. These tests fail the day that
/// stops being true.
/// </summary>
[TestFixture]
public sealed class AnchorRotationRotatedGridTest
{
    // The reported set. Pipes and conveyors anchor to the floor; lights and buttons are wall-mounted.
    private const string Pipe = "GasPipeStraight";
    private const string Conveyor = "ConveyorBelt";
    private const string TubeLight = "Poweredlight";
    private const string WallButton = "SignalButton";

    private const double GridDegrees = 85;
    private const double PlacedDegrees = 90;

    /// <summary>
    /// The RCD/RPD path: spawn onto grid-local coordinates with an explicit rotation. The entity is already
    /// grid-parented when it anchors, so the engine rewrite never fires. Pins that this path stays clean.
    /// </summary>
    [Test]
    [TestCase(Pipe)]
    [TestCase(Conveyor)]
    [TestCase(TubeLight)]
    [TestCase(WallButton)]
    public async Task SpawnOnRotatedGrid_KeepsRotation(string proto)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();

        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            var grid = mapSys.CreateGridEntity(mapId);
            mapSys.SetTile(grid, new Vector2i(0, 0), new Tile(1));
            xformSys.SetWorldRotation(grid.Owner, Angle.FromDegrees(GridDegrees));

            var ent = entMan.SpawnAttachedTo(proto, grid.Owner.ToCoordinates(0, 0),
                rotation: Angle.FromDegrees(PlacedDegrees));
            var xform = entMan.GetComponent<TransformComponent>(ent);

            Assert.That(xform.LocalRotation.Degrees, Is.EqualTo(PlacedDegrees).Within(0.01),
                $"{proto} placed facing one way must stay facing that way relative to the ship");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The construction-graph path: <see cref="SetAnchor"/> is the completion action that anchors a finished
    /// structure, and it is how conveyors and most built structures end up anchored. This is the reparenting case,
    /// held out of hands rather than already grid-parented, so it is the closest content gets to the engine
    /// rewrite. The rotation survives, because the holder is on the grid and the rotation delta is therefore zero.
    /// </summary>
    [Test]
    [TestCase(Pipe)]
    [TestCase(Conveyor)]
    [TestCase(TubeLight)]
    [TestCase(WallButton)]
    public async Task SetAnchorOnRotatedGrid_KeepsRotation(string proto)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();

        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            var grid = mapSys.CreateGridEntity(mapId);
            mapSys.SetTile(grid, new Vector2i(0, 0), new Tile(1));
            xformSys.SetWorldRotation(grid.Owner, Angle.FromDegrees(GridDegrees));

            // Anchoring always targets the entity's own GridUid, and GridUid is inherited down the transform
            // tree. So the reparenting case is an entity held by something ON the grid: GridUid matches, but the
            // parent is the holder, not the grid. That is "built out of hands or a container".
            var holder = entMan.SpawnAttachedTo(null, grid.Owner.ToCoordinates(0, 0));
            var ent = entMan.SpawnAttachedTo(proto, grid.Owner.ToCoordinates(0, 0));
            var xform = entMan.GetComponent<TransformComponent>(ent);

            if (xform.Anchored)
                xformSys.Unanchor(ent, xform);

            xformSys.SetParent(ent, xform, holder);
            xformSys.SetLocalRotation(ent, Angle.FromDegrees(PlacedDegrees), xform);

            Assert.Multiple(() =>
            {
                Assert.That(xform.ParentUid, Is.EqualTo(holder), "entity should be held, not grid-parented");
                Assert.That(xform.GridUid, Is.EqualTo(grid.Owner), "GridUid is inherited from the holder");
            });

            new SetAnchor().PerformAction(ent, null, entMan);

            Assert.Multiple(() =>
            {
                Assert.That(xform.Anchored, Is.True, $"{proto} should have anchored");
                Assert.That(xform.LocalRotation.Degrees, Is.EqualTo(PlacedDegrees).Within(0.01),
                    $"{proto} must not be skewed by the grid's rotation when the construction graph anchors it");
            });
        });

        await pair.CleanReturnAsync();
    }
}
