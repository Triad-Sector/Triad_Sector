// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.IntegrationTests.Tests.Interaction;
using Content.Shared.Construction.Prototypes;
using Robust.Client.Graphics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Construction;

/// <summary>
/// Pins the construction ghost rotation contract: the placement direction is GRID-LOCAL, stored verbatim, and
/// nothing about the camera may leak into it.
///
/// The engine's placement preview (<c>PlacementMode.Render</c>) draws at
/// <c>gridWorldRotation + Direction.ToAngle()</c>, which makes Direction grid-local by definition.
/// <c>ConstructionSystem.TrySpawnGhost</c> must store plain <c>dir</c> as the ghost's <c>LocalRotation</c> so the
/// placed ghost and the preview the player aims by agree in every camera state; that value then rides the wire in
/// <c>TryStartConstruction</c> and becomes the built structure's local rotation, cardinal in the ship's frame by
/// construction. An earlier fix pair (#493/#516) treated Direction as screen-space and mixed the eye rotation in,
/// which made placed ghosts disagree with the preview by the camera's <c>RelativeRotation</c>; the previous
/// revision of this file asserted that behaviour, and these tests replace it with the preview's convention.
///
/// The set must also stay on the non-animating setter: the client <c>TransformSystem</c> override of
/// <c>SetLocalRotation</c> schedules a render lerp whose frame loop rewrites <c>LocalRotation</c> with samples
/// that never reach the target, and a client-only ghost never receives a server state to correct it, so the last
/// sample would be its rotation forever (observed live as a 180° ghost frozen at 175.69°, built into structures
/// as the reported ~85° skew). <c>SetLocalRotationNoLerp</c> is the path that writes once, exactly.
///
/// The eye these tests set is <c>IEyeManager.CurrentEye</c>, the same eye the old code read. Nothing drives it on
/// a headless client (see <c>ClickableTest</c>), so each case states the value <c>EyeLerpingSystem</c> would have
/// produced for that grid and camera offset, and the contract is that it must not matter.
/// </summary>
public sealed class ConstructionGhostRotationTest : InteractionTest
{
    /// <summary>
    /// Rotatable, no wall required, its only condition is <c>TileNotBlocked</c>, and its first edge takes a
    /// single material, so the build can be driven with one interaction.
    /// </summary>
    private const string DiagonalWall = "wallSolidDiagonal";

    /// <summary>What the first edge of the diagonal wall's graph actually spawns.</summary>
    private const string Girder = "Girder";

    private const double Tolerance = 0.01;

    /// <summary>
    /// Rotate the test grid and stand the player on it, the way boarding a ship leaves things.
    ///
    /// The base fixture hands out <see cref="InteractionTest.PlayerCoords"/> and
    /// <see cref="InteractionTest.TargetCoords"/> relative to the MAP, which is fine while nothing is rotated but
    /// silently drifts off the intended tiles the moment the grid turns, and would parent the ghost to the map
    /// rather than the ship. In game the placement manager hands construction grid coordinates, so re-derive both
    /// in the grid's own frame and move the player over.
    /// </summary>
    private async Task BoardRotatedShip(Angle gridRotation)
    {
        await Server.WaitPost(() => Transform.SetWorldRotation(MapData.Grid.Owner, gridRotation));

        // Same two tiles the base fixture already laid down, addressed in the frame that follows the ship.
        PlayerCoords = SEntMan.GetNetCoordinates(new EntityCoordinates(MapData.Grid.Owner, 0.5f, 0.5f));
        TargetCoords = SEntMan.GetNetCoordinates(new EntityCoordinates(MapData.Grid.Owner, 1.5f, 0.5f));

        await Server.WaitPost(() => Transform.SetCoordinates(SPlayer, SEntMan.GetCoordinates(PlayerCoords)));
        await RunTicks(5);

        Assert.That(SEntMan.GetComponent<TransformComponent>(SPlayer).GridUid, Is.EqualTo(MapData.Grid.Owner),
            "the player did not end up standing on the test grid");
    }

    /// <summary>What a placed ghost ended up holding, plus the eye it was placed under.</summary>
    private readonly record struct GhostReading(bool Spawned, Angle Local, Angle World, Angle Eye);

    /// <summary>
    /// Place a ghost with the camera at <paramref name="eye"/>, read its rotations back, and clear it again.
    ///
    /// Only client work happens inside the post and the assertions stay on the test thread: an assertion that
    /// throws inside <c>WaitPost</c> skips the cleanup on its way out and resurfaces as an unrelated dirty-pair
    /// <c>DebugAssertException</c> in teardown, which says nothing about the rotation that actually went wrong.
    /// </summary>
    private async Task<GhostReading> PlaceGhost(ConstructionPrototype proto, Direction dir, Angle eye)
    {
        var eyeMan = Client.ResolveDependency<IEyeManager>();
        var cXform = CEntMan.System<SharedTransformSystem>();
        var reading = default(GhostReading);

        await Client.WaitPost(() =>
        {
            eyeMan.CurrentEye.Rotation = eye;

            if (!CConSys.TrySpawnGhost(proto, CEntMan.GetCoordinates(TargetCoords), dir, out var ghost))
                return;

            var xform = CEntMan.GetComponent<TransformComponent>(ghost.Value);

            // The eye is read back rather than assumed: if anything ever starts driving CurrentEye on a headless
            // client, these cases would pass for the wrong reason.
            reading = new GhostReading(true, xform.LocalRotation, cXform.GetWorldRotation(xform),
                eyeMan.CurrentEye.Rotation);

            CConSys.ClearGhost(ghost.Value.GetHashCode());
        });

        await RunTicks(1);
        return reading;
    }

    /// <summary>
    /// A ghost stores the cardinal the player cycled, in the ship's own frame, and its world rotation lands on
    /// <c>grid + dir</c>: the exact quantity the placement preview draws, which is what keeps the placed ghost and
    /// the preview visually identical. The camera term is deliberately absent from both expectations, whatever the
    /// eye is doing.
    /// </summary>
    /// <param name="gridDegrees">World rotation of the ship the player is standing on.</param>
    /// <param name="relativeDegrees">
    /// <c>InputMoverComponent.RelativeRotation</c>, the camera's offset from the grid. Zero is a freshly spawned
    /// player; -90 is where the camera parks after boarding a rotated ship; 270 is the same quarter turn as the
    /// mover's <c>FlipPositive()</c> leaves it, which puts an un-normalized value like -323° on the eye.
    /// </param>
    [Test]
    [TestCase(0, 0)] // Station. Control: every convention agrees here.
    [TestCase(85, 0)] // Ship, camera exactly cancelling the grid.
    [TestCase(85, -90)] // Ship, camera parked a quarter turn off. The originally reported case.
    [TestCase(85, 180)]
    [TestCase(85, 270)] // FlipPositive flavour: same quarter turn as -90, un-normalized eye.
    public async Task GhostRotationIsTheDirectionThePlayerCycled(double gridDegrees, double relativeDegrees)
    {
        var proto = ProtoMan.Index<ConstructionPrototype>(DiagonalWall);

        var grid = Angle.FromDegrees(gridDegrees);
        var relative = Angle.FromDegrees(relativeDegrees);
        var eye = -(grid + relative);

        await BoardRotatedShip(grid);

        foreach (var dir in new[] { Direction.South, Direction.East, Direction.North, Direction.West })
        {
            var ghost = await PlaceGhost(proto, dir, eye);

            Assert.That(ghost.Spawned, Is.True, $"failed to place a {dir} ghost");
            AssertAngle(ghost.Eye, eye, "the eye moved out from under the test");

            Assert.Multiple(() =>
            {
                // This is the value TryStartConstruction puts on the wire, and the server writes it to the
                // structure verbatim. It must be the picked cardinal, untouched by the camera.
                AssertAngle(ghost.Local, dir.ToAngle(),
                    $"a {dir} ghost on a {gridDegrees}° grid with the camera {relativeDegrees}° off did not " +
                    "store the picked direction in the ship's frame");

                // The preview draws at grid + Direction (PlacementMode.Render); matching it here is what makes
                // the placed ghost look identical to the preview the player aimed with.
                AssertAngle(ghost.World, grid + dir.ToAngle(),
                    $"a {dir} ghost does not sit where the placement preview draws");

                AssertCardinal(ghost.Local, $"a {dir} ghost is not aligned to the ship's tiles");
            });
        }
    }

    /// <summary>
    /// The stored rotation is a pure function of the picked direction: a camera caught mid-lerp at some fractional
    /// angle must produce the identical ghost as a settled one. Under the old screen-space code this exact eye put
    /// a fraction of a turn into <c>LocalRotation</c> and needed a snap to paper over; under the grid-local
    /// convention the eye never enters the calculation at all.
    /// </summary>
    [Test]
    public async Task GhostRotationDoesNotDependOnTheCamera()
    {
        var proto = ProtoMan.Index<ConstructionPrototype>(DiagonalWall);

        var grid = Angle.FromDegrees(85);
        // Three degrees short of the -90 the camera is heading for: mid-lerp, nothing cardinal about it.
        var midLerpEye = -(grid + Angle.FromDegrees(-93));
        var settledEye = -(grid + Angle.FromDegrees(-90));

        await BoardRotatedShip(grid);

        var midLerp = await PlaceGhost(proto, Direction.East, midLerpEye);
        var settled = await PlaceGhost(proto, Direction.East, settledEye);

        Assert.Multiple(() =>
        {
            Assert.That(midLerp.Spawned && settled.Spawned, Is.True, "failed to place a ghost");
            AssertAngle(midLerp.Local, Direction.East.ToAngle(),
                "a ghost placed under a mid-lerp camera did not store the picked direction");
            AssertAngle(midLerp.Local, settled.Local,
                "the same placement under two camera states produced two different rotations");
            AssertCardinal(midLerp.Local,
                "a ghost placed mid-camera-lerp would build a structure that is permanently off-grid");
        });
    }

    /// <summary>
    /// End to end: the cardinal the player picked is the rotation the built structure keeps in the ship's frame.
    /// The angle rides the wire as the ghost's <c>LocalRotation</c> and reaches
    /// <c>SpawnAttachedTo(..., rotation: angle)</c>, so the entity the graph's first edge creates is where it
    /// lands. The read after <see cref="InteractionTest.RunTicks"/> doubles as the regression guard for the render
    /// lerp: with the animating setter the client frame loop rewrites a client-only ghost's rotation to a
    /// permanently-short sample within its first tick, so an exact cardinal surviving to the build proves the
    /// NoLerp path held.
    /// </summary>
    [Test]
    public async Task BuiltStructureKeepsTheRotationTheGhostShowed()
    {
        var proto = ProtoMan.Index<ConstructionPrototype>(DiagonalWall);
        var eyeMan = Client.ResolveDependency<IEyeManager>();

        var grid = Angle.FromDegrees(85);
        var eye = -(grid + Angle.FromDegrees(-90));
        const Direction dir = Direction.East;

        await BoardRotatedShip(grid);

        // This ghost is kept rather than cleared, so it does not go through PlaceGhost.
        var spawned = false;
        await Client.WaitPost(() =>
        {
            eyeMan.CurrentEye.Rotation = eye;

            if (!CConSys.TrySpawnGhost(proto, CEntMan.GetCoordinates(TargetCoords), dir, out var ghost))
                return;

            spawned = true;
            Target = CEntMan.GetNetEntity(ghost.Value);
            ConstructionGhostId = ghost.Value.GetHashCode();
        });

        await RunTicks(5);
        Assert.That(spawned, Is.True, $"failed to place a {dir} ghost");

        // Several ticks of client frame updates have run; an animating setter would have eaten the exact value.
        var ghostLocal = CEntMan.GetComponent<TransformComponent>(CEntMan.GetEntity(Target!.Value)).LocalRotation;
        AssertAngle(ghostLocal, dir.ToAngle(),
            "the ghost's rotation did not survive to the build, so something re-animated it after spawn");

        // The first edge of the graph is what carries the rotation, and it spawns the girder.
        await InteractUsing(Steel, 2);
        ClientAssertPrototype(Girder, Target);
        await RunTicks(5);

        var built = ToServer(Target);
        Assert.That(built, Is.Not.Null, "the girder never made it to the server");

        var xform = SEntMan.GetComponent<TransformComponent>(built!.Value);
        Assert.Multiple(() =>
        {
            AssertAngle(xform.LocalRotation, dir.ToAngle(),
                $"a structure built facing {dir} did not keep that facing in the ship's frame");
            AssertCardinal(xform.LocalRotation, "the spawned structure is not aligned to the ship's tiles");
        });
    }

    private static void AssertAngle(Angle actual, Angle expected, string because)
    {
        var off = Math.Abs(Angle.ShortestDistance(actual, expected).Degrees);
        Assert.That(off, Is.LessThan(Tolerance),
            $"{because} (off by {off:0.##}°: got {actual.Degrees:0.##}°, wanted {expected.Degrees:0.##}°)");
    }

    private static void AssertCardinal(Angle actual, string because)
    {
        var off = Math.Abs(Angle.ShortestDistance(actual, actual.GetCardinalDir().ToAngle()).Degrees);
        Assert.That(off, Is.LessThan(Tolerance),
            $"{because} (off the nearest cardinal by {off:0.##}°: {actual.Degrees:0.##}°)");
    }
}
