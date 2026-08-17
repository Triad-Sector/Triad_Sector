using System.Numerics;
using Robust.Shared.Maths;
using Robust.Shared.Physics;

namespace Content.Client.Shuttles.UI;

/// <summary>
/// Builds a grid's radar draw geometry: filled tile triangles plus a deduplicated outline, both in
/// grid-local space.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from Monolith's rewritten <c>BaseShuttleControl.DrawGrid</c> so the decomposition can be
/// exercised without a UI tree. The overlap classification is unchanged; the storage type is not.
/// </para>
/// <para>
/// Per-tile edges are kept as directed <see cref="Segment"/>s. They were previously stored as
/// <see cref="Box2"/>, whose constructor keeps the passed bottom-left but raises the top-right to
/// <c>Vector2.Max</c> of the two. A tile's vertex list is a closed loop, so two of every four edges run
/// in the negative direction and collapsed to a zero-length box at the wrong end. Downstream that made
/// <c>otherEdgeVec</c> zero, so dividing by its zero length squared produced NaN, every comparison
/// against NaN was false, and the "fully encompassed" branch pushed two NaN-coordinate edges instead of
/// culling the shared edge. In a build with asserts compiled in the inverted box tripped
/// <c>Box2.Validate</c> first and took the client down.
/// </para>
/// <para>
/// Reversing a segment leaves the classification untouched: the projected parameters map t to 1-t while
/// the endpoints swap, which both overlap tests are symmetric under, and the reconstructed points are
/// identical because the base point and the edge vector flip together.
/// </para>
/// </remarks>
public static class RadarGridGeometry
{
    /// <summary>
    /// A directed tile edge in tile-local units, before the tile size and grid offset are applied.
    /// </summary>
    public readonly record struct Segment(Vector2 Start, Vector2 End);

    /// <summary>
    /// Decomposes a set of tiles into triangles and an outline with shared edges removed.
    /// </summary>
    /// <param name="tiles">Grid tile index to that tile's convex vertex loop, in unit-tile space.</param>
    /// <param name="tileSize">Grid tile size, applied to every emitted coordinate.</param>
    /// <param name="triangles">Receives a triangle list. Cleared first.</param>
    /// <param name="edges">Receives the outline as a line list. Cleared first.</param>
    public static void Build(
        IReadOnlyDictionary<Vector2i, IReadOnlyList<Vector2>> tiles,
        int tileSize,
        List<Vector2> triangles,
        List<Segment> edges)
    {
        triangles.Clear();
        edges.Clear();

        var dirEdges = new Dictionary<Vector2i, Segment?[]>(tiles.Count);

        foreach (var (index, verts) in tiles)
        {
            if (verts.Count < 3)
                continue;

            var bl = new Vector2(index.X * tileSize, index.Y * tileSize);

            // Convex, so fan from the first vertex.
            var origin = bl + verts[0] * tileSize;
            var prev = bl + verts[1] * tileSize;
            for (var i = 2; i < verts.Count; i++)
            {
                var vert = bl + verts[i] * tileSize;
                triangles.Add(origin);
                triangles.Add(prev);
                triangles.Add(vert);
                prev = vert;
            }

            dirEdges[index] = BuildDirectionEdges(verts);
        }

        foreach (var (index, verts) in tiles)
        {
            if (verts.Count < 3)
                continue;

            var bl = new Vector2(index.X * tileSize, index.Y * tileSize);
            var prev = verts[^1];

            for (var i = 0; i < verts.Count; i++)
            {
                var vert = verts[i];
                var wasPrev = prev;
                prev = vert;

                var dirFlag = ClassifyEdge(wasPrev, vert);
                if (dirFlag == DirectionFlag.None)
                {
                    edges.Add(new Segment(bl + wasPrev * tileSize, bl + vert * tileSize));
                    continue;
                }

                var dirDir = dirFlag.AsDir();
                var dirVec = dirDir.ToIntVec();

                if (!dirEdges.TryGetValue(index + dirVec, out var otherEdges)
                    || otherEdges[GetDirIndex(dirDir.GetOpposite().ToIntVec())] is not { } neighbor)
                {
                    edges.Add(new Segment(bl + wasPrev * tileSize, bl + vert * tileSize));
                    continue;
                }

                var offset = (Vector2)dirVec;
                var otherPrev = neighbor.Start + offset;
                var otherVert = neighbor.End + offset;
                var otherEdgeVec = otherVert - otherPrev;
                var lengthSq = otherEdgeVec.LengthSquared();

                // A zero-length neighbour cannot overlap anything; drawing our own edge is the safe answer.
                if (lengthSq == 0f)
                {
                    edges.Add(new Segment(bl + wasPrev * tileSize, bl + vert * tileSize));
                    continue;
                }

                // Map both of our endpoints onto the parametric form of the neighbour's edge.
                var otherEdgeAdj = otherEdgeVec / lengthSq;
                var relPrevPos = Vector2.Dot(wasPrev - otherPrev, otherEdgeAdj);
                var relVertPos = Vector2.Dot(vert - otherPrev, otherEdgeAdj);
                if (relPrevPos > relVertPos)
                    (relVertPos, relPrevPos) = (relPrevPos, relVertPos);

                // Fully inside the neighbour: the edge is shared, draw nothing.
                if (relPrevPos >= 0 && relVertPos <= 1)
                    continue;

                // Disjoint: draw ourselves whole.
                if (relPrevPos >= 1 || relVertPos <= 0)
                {
                    edges.Add(new Segment(bl + wasPrev * tileSize, bl + vert * tileSize));
                    continue;
                }

                if (relPrevPos >= 0 || relVertPos <= 1)
                {
                    // We end somewhere inside it; draw only the part that sticks out.
                    if (relVertPos <= 1)
                        relVertPos = 0;
                    if (relPrevPos >= 0)
                        relPrevPos = 1;

                    var p1 = otherPrev + otherEdgeVec * relPrevPos;
                    var p2 = otherPrev + otherEdgeVec * relVertPos;
                    if (p2 - p1 != Vector2.Zero)
                        edges.Add(new Segment(bl + p1 * tileSize, bl + p2 * tileSize));
                }
                else
                {
                    // We fully encompass it, so draw the two overhangs.
                    var p1 = otherPrev + otherEdgeVec * relPrevPos;
                    var p4 = otherPrev + otherEdgeVec * relVertPos;
                    edges.Add(new Segment(bl + p1 * tileSize, bl + otherPrev * tileSize));
                    edges.Add(new Segment(bl + otherVert * tileSize, bl + p4 * tileSize));
                }
            }
        }
    }

    /// <summary>
    /// Joins collinear segments that meet end-to-start, so fewer vertices reach the GPU.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Indexes the edges by their start point and grows each chain in one pass. The previous form rescanned
    /// the whole list for every edge and repeated that until a pass changed nothing, which is quadratic on
    /// an outline with a lot of segments, and its scan only ever looked forward from the current index, so a
    /// run whose predecessor sat earlier in the list was never joined. A grid's outline is emitted tile by
    /// tile, so that happened constantly: two tiles side by side merged along the top and stayed split along
    /// the bottom purely because of emission order.
    /// </para>
    /// <para>
    /// The index chains through an array rather than holding a list per point, because a point usually has
    /// exactly one edge leaving it and an outline can carry six figures of them. Several edges CAN share a
    /// start: a fractal boundary pinches where two strands meet at one vertex, so the walk keeps looking
    /// past a non-collinear candidate instead of giving up on the first one.
    /// </para>
    /// </remarks>
    public static void MergeCollinear(List<Segment> edges)
    {
        if (edges.Count < 2)
            return;

        // head maps a point to the most recently seen edge starting there; next chains the rest.
        var head = new Dictionary<Vector2, int>(edges.Count);
        var next = new int[edges.Count];

        for (var i = 0; i < edges.Count; i++)
        {
            next[i] = head.TryGetValue(edges[i].Start, out var previous) ? previous : -1;
            head[edges[i].Start] = i;
        }

        var absorbed = new bool[edges.Count];

        for (var i = 0; i < edges.Count; i++)
        {
            if (absorbed[i])
                continue;

            var segment = edges[i];

            while (true)
            {
                if (!head.TryGetValue(segment.End, out var candidate))
                    break;

                var found = -1;
                for (var j = candidate; j >= 0; j = next[j])
                {
                    if (j == i || absorbed[j])
                        continue;

                    if (!CollinearSimplifier.IsCollinear(segment.Start, segment.End, edges[j].End, 10f * float.Epsilon))
                        continue;

                    found = j;
                    break;
                }

                if (found < 0)
                    break;

                // Every iteration absorbs an edge that can never be absorbed again, so this terminates.
                absorbed[found] = true;
                segment = new Segment(segment.Start, edges[found].End);
            }

            edges[i] = segment;
        }

        var write = 0;
        for (var i = 0; i < edges.Count; i++)
        {
            if (absorbed[i])
                continue;

            edges[write++] = edges[i];
        }

        edges.RemoveRange(write, edges.Count - write);
    }

    /// <summary>
    /// Records which of a tile's edges lie flat along each cardinal, indexed by <see cref="GetDirIndex"/>.
    /// </summary>
    private static Segment?[] BuildDirectionEdges(IReadOnlyList<Vector2> verts)
    {
        var result = new Segment?[4];
        var prev = verts[^1];

        for (var i = 0; i < verts.Count; i++)
        {
            var vert = verts[i];
            var wasPrev = prev;
            prev = vert;

            var dirFlag = ClassifyEdge(wasPrev, vert);
            if (dirFlag == DirectionFlag.None)
                continue;

            result[GetDirIndex(dirFlag.AsDir().ToIntVec())] = new Segment(wasPrev, vert);
        }

        return result;
    }

    /// <summary>
    /// Returns the cardinal an edge lies flat along, or <see cref="DirectionFlag.None"/> if it is diagonal.
    /// </summary>
    private static DirectionFlag ClassifyEdge(Vector2 wasPrev, Vector2 vert)
    {
        if (wasPrev.X == 0 && vert.X == 0)
            return DirectionFlag.West;
        if (wasPrev.X == 1 && vert.X == 1)
            return DirectionFlag.East;
        if (wasPrev.Y == 0 && vert.Y == 0)
            return DirectionFlag.South;
        if (wasPrev.Y == 1 && vert.Y == 1)
            return DirectionFlag.North;

        return DirectionFlag.None;
    }

    /// <summary>
    /// Packs a cardinal unit vector into 0-3.
    /// </summary>
    public static int GetDirIndex(Vector2i dir)
    {
        // 1,  0 -> 1
        // 0,  1 -> 3
        // -1, 0 -> 0
        // 0, -1 -> 2
        return Math.Abs(dir.Y) * 2 + (dir.X + dir.Y + 1) / 2;
    }
}
