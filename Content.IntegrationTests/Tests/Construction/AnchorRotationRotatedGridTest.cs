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
/// Engine 287's <c>AnchorEntity</c> rewrites an entity's local rotation to preserve its WORLD rotation whenever
/// anchoring also reparents it, skewing the result by exactly the grid's rotation. That difference is zero on a
/// station and visible on any rotated ship, which is why it is invisible upstream. Until the engine carries the
/// fix, content re-asserts the grid-local rotation across its own anchor calls.
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
    /// structure, and it is how conveyors and most built structures end up anchored. If the entity is not already
    /// grid-parented, anchoring reparents it and the engine rewrite fires.
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
