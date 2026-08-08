using System.Linq;
using System.Numerics;
using Content.Server.Shuttles.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Content.Shared.Shuttles.Components; // Frontier

namespace Content.Server.Shuttles.Systems;

public sealed partial class DockingSystem
{
    /*
     * Handles the shuttle side of FTL docking.
     */

    private const int DockRoundingDigits = 2;

    public Angle GetAngle(EntityUid uid, TransformComponent xform, EntityUid targetUid, TransformComponent targetXform)
    {
        var (shuttlePos, shuttleRot) = _transform.GetWorldPositionRotation(xform);
        var (targetPos, targetRot) = _transform.GetWorldPositionRotation(targetXform);

        var shuttleCOM = Robust.Shared.Physics.Transform.Mul(new Transform(shuttlePos, shuttleRot),
            _physicsQuery.GetComponent(uid).LocalCenter);
        var targetCOM = Robust.Shared.Physics.Transform.Mul(new Transform(targetPos, targetRot),
            _physicsQuery.GetComponent(targetUid).LocalCenter);

        var mapDiff = shuttleCOM - targetCOM;
        var angle = mapDiff.ToWorldAngle();
        angle -= targetRot;
        return angle;
    }

    /// <summary>
    /// Checks if 2 docks can be connected by moving the shuttle directly onto docks.
    /// </summary>
    private bool CanDock(
        DockingComponent shuttleDock,
        TransformComponent shuttleDockXform,
        DockingComponent gridDock,
        TransformComponent gridDockXform,
        Box2 shuttleAABB,
        Angle targetGridRotation,
        FixturesComponent shuttleFixtures,
        Entity<MapGridComponent> gridEntity,
        bool isMap,
        out Matrix3x2 matty,
        out Box2 shuttleDockedAABB,
        out Angle gridRotation)
    {
        shuttleDockedAABB = Box2.UnitCentered;
        gridRotation = Angle.Zero;
        matty = Matrix3x2.Identity;

        if (shuttleDock.Docked ||
            gridDock.Docked ||
            !shuttleDockXform.Anchored ||
            !gridDockXform.Anchored)
        {
            return false;
        }

        // Frontier: check dock types
        if ((shuttleDock.DockType & gridDock.DockType) == DockType.None)
            return false;
        // End Frontier

        // First, get the station dock's position relative to the shuttle, this is where we rotate it around
        var stationDockPos = shuttleDockXform.LocalPosition +
                             shuttleDockXform.LocalRotation.RotateVec(new Vector2(0f, -1f));

        // Need to invert the grid's angle.
        var shuttleDockAngle = shuttleDockXform.LocalRotation;
        var gridDockAngle = gridDockXform.LocalRotation.Opposite();
        var offsetAngle = gridDockAngle - shuttleDockAngle;

        var stationDockMatrix = Matrix3Helpers.CreateInverseTransform(stationDockPos, shuttleDockAngle);
        var gridXformMatrix = Matrix3Helpers.CreateTransform(gridDockXform.LocalPosition, gridDockAngle);
        matty = Matrix3x2.Multiply(stationDockMatrix, gridXformMatrix);

        if (!ValidSpawn(gridEntity, matty, offsetAngle, shuttleFixtures, isMap))
            return false;

        shuttleDockedAABB = matty.TransformBox(shuttleAABB);
        gridRotation = offsetAngle.Reduced();
        return true;
    }

    /// <summary>
    /// Gets docking config between 2 specific docks.
    /// </summary>
    public DockingConfig? GetDockingConfig(
        EntityUid shuttleUid,
        EntityUid targetGrid,
        EntityUid shuttleDockUid,
        DockingComponent shuttleDock,
        EntityUid gridDockUid,
        DockingComponent gridDock)
    {
        var shuttleDocks = new List<Entity<DockingComponent>>(1)
       {
           (shuttleDockUid, shuttleDock)
       };

        var gridDocks = new List<Entity<DockingComponent>>(1)
       {
           (gridDockUid, gridDock)
       };

        return GetDockingConfigPrivate(shuttleUid, targetGrid, shuttleDocks, gridDocks);
    }

    /// <summary>
    /// Tries to get a valid docking configuration for the shuttle to the target grid.
    /// </summary>
    /// <param name="priorityTag">Priority docking tag to prefer, e.g. for emergency shuttle</param>
    public DockingConfig? GetDockingConfig(EntityUid shuttleUid, EntityUid targetGrid, string? priorityTag = null, DockType dockType = DockType.Airlock) // Frontier: add dockType
    {
        var gridDocks = GetDocks(targetGrid);
        var shuttleDocks = GetDocks(shuttleUid);

        return GetDockingConfigPrivate(shuttleUid, targetGrid, shuttleDocks, gridDocks, priorityTag, dockType); // Frontier: add dockType
    }

    /// <summary>
    /// Tries to get a docking config at the specified coordinates and angle.
    /// </summary>
    public DockingConfig? GetDockingConfigAt(EntityUid shuttleUid,
        EntityUid targetGrid,
        EntityCoordinates coordinates,
        Angle angle,
        bool fallback = true,
        DockType dockType = DockType.Airlock, // Frontier
        string? priorityTag = null) // Triad: prefer a berth flagged for this traffic, see the fallback
    {
        var gridDocks = GetDocks(targetGrid);
        var shuttleDocks = GetDocks(shuttleUid);

        var configs = GetDockingConfigs(shuttleUid, targetGrid, shuttleDocks, gridDocks, dockType); // Frontier: add dockType

        foreach (var config in configs)
        {
            if (config.Coordinates.Equals(coordinates) && config.Angle.EqualsApprox(angle, 0.15))
            {
                return config;
            }
        }

        if (fallback && configs.Count > 0)
        {
            // Triad: the exact requested berth was not free, so rank the rest rather than grabbing
            // whatever enumerated first out of a HashSet. Same comparison departure used, so the
            // fallback lands on the berth and the approach side that were planned for. With no tag this
            // is the old behaviour, since neither tag predicate matches null.
            return SortDockingConfigs(configs, targetGrid, priorityTag).First();
        }

        return null;
    }

    /// <summary>
    /// Gets all docking configs between the 2 grids.
    /// </summary>
    private List<DockingConfig> GetDockingConfigs(
        EntityUid shuttleUid,
        EntityUid targetGrid,
        List<Entity<DockingComponent>> shuttleDocks,
        List<Entity<DockingComponent>> gridDocks,
        DockType dockType) // Frontier: add dockType
    {
        var validDockConfigs = new List<DockingConfig>();

        if (gridDocks.Count <= 0)
            return validDockConfigs;

        var targetGridGrid = _gridQuery.GetComponent(targetGrid);
        var targetGridXform = _xformQuery.GetComponent(targetGrid);
        var targetGridAngle = _transform.GetWorldRotation(targetGridXform).Reduced();
        var shuttleFixturesComp = Comp<FixturesComponent>(shuttleUid);
        var shuttleAABB = _gridQuery.GetComponent(shuttleUid).LocalAABB;

        var isMap = HasComp<MapComponent>(targetGrid);

        var grids = new List<Entity<MapGridComponent>>();
        if (shuttleDocks.Count > 0)
        {
            // We'll try all combinations of shuttle docks and see which one is most suitable
            foreach (var (dockUid, shuttleDock) in shuttleDocks)
            {
                var shuttleDockXform = _xformQuery.GetComponent(dockUid);

                // Frontier: skip docks that don't match type
                if ((shuttleDock.DockType & dockType) == DockType.None)
                    continue;
                // End Frontier

                foreach (var (gridDockUid, gridDock) in gridDocks)
                {
                    var gridXform = _xformQuery.GetComponent(gridDockUid);

                    // Frontier: skip docks that don't match type
                    if ((gridDock.DockType & dockType) == DockType.None)
                        continue;
                    // End Frontier

                    // Triad: a berth may demand a specific shuttle side; see RequiredShuttleTag.
                    if (!ShuttleSideAllowed(dockUid, gridDockUid))
                        continue;

                    if (!CanDock(
                            shuttleDock, shuttleDockXform,
                            gridDock, gridXform,
                            shuttleAABB,
                            targetGridAngle,
                            shuttleFixturesComp,
                            (targetGrid, targetGridGrid),
                            isMap,
                            out var matty,
                            out var dockedAABB,
                            out var targetAngle))
                    {
                        continue;
                    }

                    // Can't just use the AABB as we want to get bounds as tight as possible.
                    var gridPosition = new EntityCoordinates(targetGrid, Vector2.Transform(Vector2.Zero, matty));
                    var spawnPosition = new EntityCoordinates(targetGridXform.MapUid!.Value, _transform.ToMapCoordinates(gridPosition).Position);

                    // TODO: use tight bounds
                    var targetWorldAngle = (targetGridAngle + targetAngle).Reduced(); // Frontier
                    var dockedBounds = new Box2Rotated(shuttleAABB.Translated(spawnPosition.Position), targetWorldAngle, spawnPosition.Position); // Frontier: targetAngle<targetWorldAngle

                    // Check if there's no intersecting grids (AKA oh god it's docking at cargo).
                    grids.Clear();
                    _mapSystem.FindGridsIntersecting(targetGridXform.MapID, dockedBounds, ref grids, includeMap: false);
                    if (grids.Any(o => o.Owner != targetGrid && o.Owner != targetGridXform.MapUid))
                    {
                        continue;
                    }

                    // Alright well the spawn is valid now to check how many we can connect
                    // Get the matrix for each shuttle dock and test it against the grid docks to see
                    // if the connected position / direction matches.

                    var dockedPorts = new List<(EntityUid DockAUid, EntityUid DockBUid, DockingComponent DockA, DockingComponent DockB)>()
                   {
                       (dockUid, gridDockUid, shuttleDock, gridDock),
                   };

                    dockedAABB = dockedAABB.Rounded(DockRoundingDigits);

                    foreach (var (otherUid, other) in shuttleDocks)
                    {
                        if (other == shuttleDock)
                            continue;

                        // Frontier: skip docks that don't match type
                        if ((other.DockType & dockType) == DockType.None)
                            continue;
                        // End Frontier

                        foreach (var (otherGridUid, otherGrid) in gridDocks)
                        {
                            if (otherGrid == gridDock)
                                continue;

                            // Frontier: skip docks that don't match type
                            if ((otherGrid.DockType & dockType) == DockType.None)
                                continue;
                            // End Frontier

                            // Triad: side requirement applies to aggregated pairs too.
                            if (!ShuttleSideAllowed(otherUid, otherGridUid))
                                continue;

                            if (!CanDock(
                                    other,
                                    _xformQuery.GetComponent(otherUid),
                                    otherGrid,
                                    _xformQuery.GetComponent(otherGridUid),
                                    shuttleAABB,
                                    targetGridAngle,
                                    shuttleFixturesComp,
                                    (targetGrid, targetGridGrid),
                                    isMap,
                                    out _,
                                    out var otherdockedAABB,
                                    out var otherTargetAngle))
                            {
                                continue;
                            }

                            otherdockedAABB = otherdockedAABB.Rounded(DockRoundingDigits);

                            // Different setup: allow small tolerance on angle / AABB to aggregate more ports.
                            if (!targetAngle.EqualsApprox(otherTargetAngle, 0.05) ||
                                !Box2ApproximatelyEquals(dockedAABB, otherdockedAABB, 0.05f))
                            {
                                continue;
                            }

                            dockedPorts.Add((otherUid, otherGridUid, other, otherGrid));
                        }
                    }

                    validDockConfigs.Add(new DockingConfig()
                    {
                        Docks = dockedPorts,
                        Coordinates = gridPosition,
                        Area = dockedAABB,
                        Angle = targetAngle,
                    });
                }
            }
        }

        return validDockConfigs;
    }

    private static bool Box2ApproximatelyEquals(Box2 a, Box2 b, float epsilon)
    {
        return MathF.Abs(a.Left - b.Left) <= epsilon &&
               MathF.Abs(a.Right - b.Right) <= epsilon &&
               MathF.Abs(a.Top - b.Top) <= epsilon &&
               MathF.Abs(a.Bottom - b.Bottom) <= epsilon;
    }

    private DockingConfig? GetDockingConfigPrivate(
        EntityUid shuttleUid,
        EntityUid targetGrid,
        List<Entity<DockingComponent>> shuttleDocks,
        List<Entity<DockingComponent>> gridDocks,
        string? priorityTag = null,
        DockType dockType = DockType.Airlock) // Frontier
    {
        var validDockConfigs = GetDockingConfigs(shuttleUid, targetGrid, shuttleDocks, gridDocks, dockType); // Frontier: add dockType

        if (validDockConfigs.Count <= 0)
            return null;

        validDockConfigs = SortDockingConfigs(validDockConfigs, targetGrid, priorityTag);

        var location = validDockConfigs.First();
        location.TargetGrid = targetGrid;
        // TODO: Ideally do a hyperspace warpin, just have it run on like a 10 second timer.

        return location;
    }

    public bool IsConfigPriority(DockingConfig config, string? priorityTag)
    {
        return config.Docks.Any(docks =>
            TryComp<PriorityDockComponent>(docks.DockBUid, out var priority)
            && priority.Tag?.Equals(priorityTag) == true);
    }

    /// <summary>
    /// Whether this shuttle port satisfies the station dock's side requirement, if it declares one.
    /// </summary>
    // Triad: hard filter, not a preference. A berth with RequiredShuttleTag set never pairs with an
    // untagged or wrong-side port, so the shuttle presents the declared side or the config never forms
    // and the trip is refused upstream. Berths with no requirement behave exactly as before.
    private bool ShuttleSideAllowed(EntityUid shuttleDockUid, EntityUid gridDockUid)
    {
        if (!TryComp<PriorityDockComponent>(gridDockUid, out var gridPriority)
            || gridPriority.RequiredShuttleTag is not { } required)
        {
            return true;
        }

        return TryComp<PriorityDockComponent>(shuttleDockUid, out var shuttlePriority)
               && shuttlePriority.Tag?.Equals(required) == true;
    }

    /// <summary>
    /// Ranks candidate configs best-first.
    /// </summary>
    // Triad: departure and arrival used to rank differently, so a shuttle could plan one berth and take
    // another. One comparison, used by both. Which face the shuttle presents is not a criterion on
    // purpose: where a berth is tight only one orientation survives CanDock/ValidSpawn at all, and where
    // it is open either face docks port-to-port equally well.
    private List<DockingConfig> SortDockingConfigs(List<DockingConfig> configs, EntityUid targetGrid, string? priorityTag)
    {
        var targetGridAngle = _transform.GetWorldRotation(targetGrid).Reduced();

        return configs
            .OrderByDescending(x => IsConfigPriority(x, priorityTag))
            .ThenByDescending(x => x.Docks.Count)
            .ThenBy(x => Math.Abs(Angle.ShortestDistance(x.Angle.Reduced(), targetGridAngle).Theta))
            .ToList();
    }

    /// <summary>
    /// Checks whether the shuttle can warp to the specified position.
    /// </summary>
    private bool ValidSpawn(Entity<MapGridComponent> gridEntity, Matrix3x2 matty, Angle angle, FixturesComponent shuttleFixturesComp, bool isMap)
    {
        var transform = new Transform(Vector2.Transform(Vector2.Zero, matty), angle);

        // Because some docking bounds are tight af need to check each chunk individually
        foreach (var fix in shuttleFixturesComp.Fixtures.Values)
        {
            var polyShape = (PolygonShape)fix.Shape;
            var aabb = polyShape.ComputeAABB(transform, 0);
            aabb = aabb.Enlarged(-0.01f);

            // If it's a map check no hard collidable anchored entities overlap
            if (isMap)
            {
                var localTiles = _mapSystem.GetLocalTilesEnumerator(gridEntity.Owner, gridEntity.Comp, aabb);

                while (localTiles.MoveNext(out var tile))
                {
                    var anchoredEnumerator = _mapSystem.GetAnchoredEntitiesEnumerator(gridEntity.Owner, gridEntity.Comp, tile.GridIndices);

                    while (anchoredEnumerator.MoveNext(out var anc))
                    {
                        if (!_physicsQuery.TryGetComponent(anc, out var physics) ||
                            !physics.CanCollide ||
                            !physics.Hard)
                        {
                            continue;
                        }

                        return false;
                    }
                }
            }
            // If it's not a map then we're docking onto another grid; ensure we don't overlap either tiles OR
            // anchored hard-collidable entities on that grid. Previously this only checked tiles which could
            // miss walls/fixtures and allow clipping.
            else
            {
                // First reject if any solid tiles intersect.
                if (_mapSystem.GetLocalTilesIntersecting(gridEntity.Owner, gridEntity.Comp, aabb).Any())
                    return false;

                // Additionally scan anchored entities on intersecting tiles and reject if any hard colliders are present.
                var localTiles = _mapSystem.GetLocalTilesEnumerator(gridEntity.Owner, gridEntity.Comp, aabb);
                while (localTiles.MoveNext(out var tile))
                {
                    var anchoredEnumerator = _mapSystem.GetAnchoredEntitiesEnumerator(gridEntity.Owner, gridEntity.Comp, tile.GridIndices);
                    while (anchoredEnumerator.MoveNext(out var anc))
                    {
                        if (!_physicsQuery.TryGetComponent(anc, out var physics) ||
                            !physics.CanCollide ||
                            !physics.Hard)
                        {
                            continue;
                        }

                        return false;
                    }
                }
            }
        }

        return true;
    }

    public List<Entity<DockingComponent>> GetDocks(EntityUid uid)
    {
        _dockingSet.Clear();
        _lookup.GetChildEntities(uid, _dockingSet);

        return _dockingSet.ToList();
    }

    /// <summary>
    /// Mono: Checks if two grids are docked together by examining if any docking port on gridA is connected to any docking port on gridB.
    /// </summary>
    public bool AreGridsDocked(EntityUid gridA, EntityUid gridB)
    {
        var docksA = GetDocks(gridA);

        foreach (var dockA in docksA)
        {
            if (!dockA.Comp.Docked || dockA.Comp.DockedWith == null)
                continue;

            // Get the grid that this dock is connected to
            var connectedDockGrid = Transform(dockA.Comp.DockedWith.Value).GridUid;
            if (connectedDockGrid == gridB)
                return true;
        }

        return false;
    }
}
