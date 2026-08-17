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
/// Players on rotated ship grids reported building structures a quarter turn away from the direction they
/// picked. <c>ConstructionSystem.TrySpawnGhost</c> wrote the placement manager's direction, which is a cardinal
/// in SCREEN space, straight into the ghost's <c>LocalRotation</c>, which is relative to the parent grid; the
/// value the ghost then sends to the server becomes the built structure's local rotation.
///
/// The two frames differ by the eye. <c>EyeLerpingSystem.GetRotation</c> ties the camera to
/// <c>-(gridWorldRotation + InputMoverComponent.RelativeRotation)</c>, and <c>RelativeRotation</c> snaps to the
/// nearest cardinal when a player walks onto a grid, so boarding an 85° ship parks it at -90°. On a station both
/// terms are zero and the bug cannot be seen, which is why it survived upstream.
///
/// The eye these tests set is <c>IEyeManager.CurrentEye</c>, the same eye the production code reads. Nothing
/// drives it on a headless client (see <c>ClickableTest</c>, which sets it the same way), so each case states the
/// value <c>EyeLerpingSystem</c> would have produced for that grid and camera offset.
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
    /// A ghost must be drawn facing the cardinal the player picked, whatever the ship is doing underneath it.
    /// </summary>
    /// <param name="gridDegrees">World rotation of the ship the player is standing on.</param>
    /// <param name="relativeDegrees">
    /// <c>InputMoverComponent.RelativeRotation</c>, the camera's offset from the grid. Zero means the camera
    /// exactly cancels the ship's rotation; -90 is where it parks after boarding a ship at any odd angle.
    /// </param>
    [Test]
    [TestCase(0, 0)] // Station. Control: nothing rotated, so old and new behaviour must agree.
    [TestCase(85, 0)] // Ship, camera exactly cancelling the grid. Also a no-op.
    [TestCase(85, -90)] // Ship, camera parked a quarter turn off. This is the reported case.
    [TestCase(85, 180)]
    public async Task GhostFacesWhereThePlayerPointed(double gridDegrees, double relativeDegrees)
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
                // An entity is drawn at worldRotation + eyeRotation (ClickableSystem uses the same identity).
                AssertAngle(ghost.World + eye, dir.ToAngle(),
                    $"a {dir} ghost on a {gridDegrees}° grid is not drawn facing {dir}");

                // This is the value TryStartConstruction puts on the wire, and the server writes it to the
                // structure verbatim. Off-cardinal here means a permanently skewed structure.
                AssertAngle(ghost.Local, dir.ToAngle() + relative,
                    $"a {dir} ghost should sit at {dir} + the camera offset in the ship's own frame");
                AssertCardinal(ghost.Local, $"a {dir} ghost is not aligned to the ship's tiles");
            });
        }
    }

    /// <summary>
    /// The camera offset is only a cardinal once its lerp settles, so mid-lerp the ghost's local rotation carries
    /// a fraction of a turn. Building during that window bakes the fraction into the structure permanently, where
    /// the pre-fix code always wrote an exact cardinal. Nothing used to snap the angle between the ghost and the
    /// spawn: the server passed it to <c>SpawnAttachedTo</c> as-is, and the placement <c>conditions</c> were the
    /// only thing that ever called <c>GetCardinalDir</c>.
    ///
    /// This is the acceptance test for the snap that closes that gap, and it reproduces at 3° off the nearest
    /// cardinal. <c>TrySpawnGhost</c> now reduces the ghost's <c>LocalRotation</c> to the parent's nearest cardinal
    /// after setting its world rotation, and <c>TryStartStructureConstruction</c> reduces the angle again before it
    /// reaches <c>SpawnAttachedTo</c>, so neither a camera caught mid-lerp nor a modified client can write a
    /// fraction of a turn onto a permanent structure.
    /// </summary>
    [Test]
    public async Task GhostStaysCardinalWhileTheCameraIsStillLerping()
    {
        var proto = ProtoMan.Index<ConstructionPrototype>(DiagonalWall);

        var grid = Angle.FromDegrees(85);
        // Three degrees short of the -90 it is heading for.
        var eye = -(grid + Angle.FromDegrees(-93));

        await BoardRotatedShip(grid);

        var ghost = await PlaceGhost(proto, Direction.East, eye);

        Assert.That(ghost.Spawned, Is.True, "failed to place the ghost");
        AssertCardinal(ghost.Local,
            "a ghost placed mid-camera-lerp would build a structure that is permanently off-grid");
    }

    /// <summary>
    /// End to end: the rotation the player saw on the ghost is the rotation the spawned structure keeps in the
    /// ship's frame. This is what the player-facing bug is actually about. The angle rides the wire as the ghost's
    /// <c>LocalRotation</c> and reaches <c>SpawnAttachedTo(..., rotation: angle)</c>, so the entity the graph's
    /// first edge creates is where it lands.
    /// </summary>
    [Test]
    public async Task BuiltStructureKeepsTheRotationTheGhostShowed()
    {
        var proto = ProtoMan.Index<ConstructionPrototype>(DiagonalWall);
        var eyeMan = Client.ResolveDependency<IEyeManager>();

        var grid = Angle.FromDegrees(85);
        var relative = Angle.FromDegrees(-90);
        var eye = -(grid + relative);
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

        await RunTicks(1);
        Assert.That(spawned, Is.True, $"failed to place a {dir} ghost");

        // The first edge of the graph is what carries the rotation, and it spawns the girder.
        await InteractUsing(Steel, 2);
        ClientAssertPrototype(Girder, Target);
        await RunTicks(5);

        var built = ToServer(Target);
        Assert.That(built, Is.Not.Null, "the girder never made it to the server");

        var xform = SEntMan.GetComponent<TransformComponent>(built!.Value);
        Assert.Multiple(() =>
        {
            AssertAngle(xform.LocalRotation, dir.ToAngle() + relative,
                $"a structure built facing {dir} on screen did not keep that facing on the ship");
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
