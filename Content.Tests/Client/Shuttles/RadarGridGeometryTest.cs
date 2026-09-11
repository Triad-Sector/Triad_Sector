using System.Collections.Generic;
using System.Numerics;
using Content.Client.Shuttles.UI;
using NUnit.Framework;
using Robust.Shared.Maths;

namespace Content.Tests.Client.Shuttles;

[TestFixture]
[TestOf(typeof(RadarGridGeometry))]
public sealed class RadarGridGeometryTest
{
    /// <summary>
    /// The vertex loop every tile prototype currently uses, counter-clockwise from the bottom left.
    /// </summary>
    private static readonly IReadOnlyList<Vector2> Square = new[]
    {
        new Vector2(0, 0),
        new Vector2(0, 1),
        new Vector2(1, 1),
        new Vector2(1, 0),
    };

    private static (List<Vector2> Triangles, List<RadarGridGeometry.Segment> Edges) Build(params Vector2i[] indices)
    {
        var tiles = new Dictionary<Vector2i, IReadOnlyList<Vector2>>();
        foreach (var index in indices)
        {
            tiles[index] = Square;
        }

        var triangles = new List<Vector2>();
        var edges = new List<RadarGridGeometry.Segment>();
        RadarGridGeometry.Build(tiles, 1, triangles, edges);
        return (triangles, edges);
    }

    private static void AssertFinite(IEnumerable<RadarGridGeometry.Segment> edges)
    {
        foreach (var edge in edges)
        {
            Assert.That(float.IsFinite(edge.Start.X) && float.IsFinite(edge.Start.Y)
                        && float.IsFinite(edge.End.X) && float.IsFinite(edge.End.Y),
                Is.True,
                $"Edge {edge} carries a non-finite coordinate.");
        }
    }

    [Test]
    public void LoneTileGetsWholePerimeter()
    {
        var (triangles, edges) = Build(new Vector2i(0, 0));

        // A convex quad fans into two triangles.
        Assert.That(triangles, Has.Count.EqualTo(6));
        Assert.That(edges, Has.Count.EqualTo(4));
        AssertFinite(edges);
    }

    /// <summary>
    /// The regression this whole type exists for. Storing tile edges as Box2 collapsed every edge whose
    /// end vertex was numerically lower than its start, which is two of the four on a square. The
    /// collapsed neighbour then had zero length, so the parametric projection divided by zero and the
    /// shared edge came back as two NaN segments instead of being culled.
    /// </summary>
    [Test]
    public void SharedEdgeBetweenNeighboursIsCulled()
    {
        var (triangles, edges) = Build(new Vector2i(0, 0), new Vector2i(1, 0));

        Assert.That(triangles, Has.Count.EqualTo(12));
        AssertFinite(edges);

        // Eight tile edges, minus the two halves of the shared border.
        Assert.That(edges, Has.Count.EqualTo(6));
    }

    /// <summary>
    /// Two tiles side by side form a 2x1 rectangle, so the outline reduces to its four sides. The earlier
    /// forward-only pass produced five, joining the top but leaving the bottom split, because the bottom's
    /// two halves were emitted in the order that put the predecessor later in the list.
    /// </summary>
    [Test]
    public void CollinearRunsMergeIntoTheRectanglePerimeter()
    {
        var (_, edges) = Build(new Vector2i(0, 0), new Vector2i(1, 0));
        RadarGridGeometry.MergeCollinear(edges);

        Assert.That(edges, Has.Count.EqualTo(4));
        AssertFinite(edges);

        // Both long runs joined across the tile boundary, in whichever direction they were emitted.
        Assert.That(edges, Has.Some.EqualTo(new RadarGridGeometry.Segment(new Vector2(0, 1), new Vector2(2, 1))));
        Assert.That(edges, Has.Some.EqualTo(new RadarGridGeometry.Segment(new Vector2(2, 0), new Vector2(0, 0))));
    }

    /// <summary>
    /// Several edges can leave one point where two stretches of boundary touch at a single vertex, so the
    /// walk has to keep looking past a non-collinear candidate rather than give up on the first one it
    /// finds at that point.
    /// </summary>
    [Test]
    public void MergeLooksPastANonCollinearEdgeSharingTheSameStart()
    {
        var edges = new List<RadarGridGeometry.Segment>
        {
            new(new Vector2(0, 0), new Vector2(1, 0)),
            new(new Vector2(1, 0), new Vector2(2, 0)), // collinear continuation
            new(new Vector2(1, 0), new Vector2(1, 1)), // branch off the same point
        };

        RadarGridGeometry.MergeCollinear(edges);

        Assert.That(edges, Has.Count.EqualTo(2));
        Assert.That(edges, Has.Some.EqualTo(new RadarGridGeometry.Segment(new Vector2(0, 0), new Vector2(2, 0))));
        Assert.That(edges, Has.Some.EqualTo(new RadarGridGeometry.Segment(new Vector2(1, 0), new Vector2(1, 1))));
    }

    /// <summary>
    /// A closed ring has no collinear joins to make and must not spin in the chain walk.
    /// </summary>
    [Test]
    public void ClosedRingTerminates()
    {
        var edges = new List<RadarGridGeometry.Segment>
        {
            new(new Vector2(0, 0), new Vector2(1, 0)),
            new(new Vector2(1, 0), new Vector2(1, 1)),
            new(new Vector2(1, 1), new Vector2(0, 1)),
            new(new Vector2(0, 1), new Vector2(0, 0)),
        };

        Assert.DoesNotThrow(() => RadarGridGeometry.MergeCollinear(edges));
        Assert.That(edges, Has.Count.EqualTo(4));
    }

    [Test]
    public void EnclosedTileContributesNoOutline()
    {
        // A plus shape: the centre tile is bordered on all four sides.
        var (_, edges) = Build(
            new Vector2i(0, 0),
            new Vector2i(1, 0),
            new Vector2i(-1, 0),
            new Vector2i(0, 1),
            new Vector2i(0, -1));

        AssertFinite(edges);

        // Five tiles at four edges each, less both halves of the four shared borders.
        Assert.That(edges, Has.Count.EqualTo(20 - 8));
    }

    [Test]
    public void DegenerateTileIsSkipped()
    {
        var tiles = new Dictionary<Vector2i, IReadOnlyList<Vector2>>
        {
            [new Vector2i(0, 0)] = new[] { new Vector2(0, 0), new Vector2(1, 1) },
        };

        var triangles = new List<Vector2>();
        var edges = new List<RadarGridGeometry.Segment>();

        Assert.DoesNotThrow(() => RadarGridGeometry.Build(tiles, 1, triangles, edges));
        Assert.That(triangles, Is.Empty);
        Assert.That(edges, Is.Empty);
    }
}
