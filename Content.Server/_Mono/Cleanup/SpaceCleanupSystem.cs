using Content.Server.Cargo.Systems;
using Content.Server.NPC.HTN;
using Content.Server.Shuttles.Components;
using Content.Shared._Mono.CCVar;
using Content.Shared.Mind.Components;
using Content.Shared.Physics;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;
using System.Reflection;

namespace Content.Server._Mono.Cleanup;

/// <summary>
///     Deletes entities eligible for deletion.
/// </summary>
public sealed partial class SpaceCleanupSystem : BaseCleanupSystem<PhysicsComponent>
{
    [Dependency] private CleanupHelperSystem _cleanup = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IGameTiming _timing = default!;
    private object _manifold = default!;
    private MethodInfo _testOverlap = default!;

    /// <summary>
    ///     How many arguments <see cref="GetWallStuck"/> supplies to TestOverlap itself.
    /// </summary>
    private const int OverlapFixedArgs = 6;

    private object?[] _overlapArgs = [];
    [Dependency] private PricingSystem _pricing = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private float _maxDistance;
    private float _maxGridDistance;
    private float _maxPrice;

    private EntityQuery<CleanupImmuneComponent> _immuneQuery;
    private EntityQuery<FixturesComponent> _fixQuery;
    private EntityQuery<HTNComponent> _htnQuery;
    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<MindContainerComponent> _mindQuery;
    private EntityQuery<PhysicsComponent> _physQuery;

    private List<(EntityCoordinates Coord, TimeSpan Time, float Radius, float Aggression)> _sweepQueue = new();
    private HashSet<Entity<PhysicsComponent>> _sweepEnts = new();

    public override void Initialize()
    {
        base.Initialize();

        // this queries over literally everything with PhysicsComponent so has to have big interval
        _cleanupInterval = TimeSpan.FromSeconds(600);

        _immuneQuery = GetEntityQuery<CleanupImmuneComponent>();
        _fixQuery = GetEntityQuery<FixturesComponent>();
        _htnQuery = GetEntityQuery<HTNComponent>();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        _mindQuery = GetEntityQuery<MindContainerComponent>();
        _physQuery = GetEntityQuery<PhysicsComponent>();

        Subs.CVar(_cfg, MonoCVars.CleanupMaxGridDistance, val => _maxGridDistance = val, true);
        Subs.CVar(_cfg, MonoCVars.SpaceCleanupDistance, val => _maxDistance = val, true);
        Subs.CVar(_cfg, MonoCVars.SpaceCleanupMaxValue, val => _maxPrice = val, true);

        var manifoldType = typeof(SharedMapSystem).Assembly.GetType("Robust.Shared.Physics.Collision.IManifoldManager");
        if (manifoldType != null)
        {
            _manifold = IoCManager.ResolveType(manifoldType);
            var testOverlapMethod = manifoldType.GetMethod("TestOverlap");
            if (testOverlapMethod != null)
                _testOverlap = testOverlapMethod.MakeGenericMethod(typeof(IPhysShape), typeof(PhysShapeCircle));
        }

        // Triad: IManifoldManager is internal, so TestOverlap is bound by name and no compiler checks
        // its arity. Invoke supplies no optional defaults, so the argument array must be exactly as
        // long as the live signature with every trailing optional filled here; engine 289.0.2 takes
        // seven (RobustToolbox/Robust.Shared/Physics/Collision/IManifoldManager.cs:15). A mismatch
        // throws TargetParameterCountException out of Update on every tick, so a signature we cannot
        // call leaves _overlapArgs empty and GetWallStuck declines instead.
        _overlapArgs = TryBuildOverlapArgs(_testOverlap);
        if (_overlapArgs.Length == 0)
            Log.Error("IManifoldManager.TestOverlap did not bind; wall-stuck entities will not be cleaned up.");
    }

    /// <summary>
    ///     Sizes the reflection argument array against TestOverlap's live signature, filling every
    ///     parameter past the <see cref="OverlapFixedArgs"/> this system supplies with its default.
    ///     Empty when the signature is one we cannot call.
    /// </summary>
    private static object?[] TryBuildOverlapArgs(MethodInfo? method)
    {
        if (method == null)
            return [];

        var parameters = method.GetParameters();
        if (parameters.Length < OverlapFixedArgs)
            return [];

        var args = new object?[parameters.Length];
        for (var i = OverlapFixedArgs; i < parameters.Length; i++)
        {
            if (!parameters[i].HasDefaultValue)
                return [];

            args[i] = parameters[i].DefaultValue;
        }

        return args;
    }

    protected override bool ShouldEntityCleanup(EntityUid uid)
    {
        return ShouldEntityCleanup(uid, 1f);
    }

    private bool ShouldEntityCleanup(EntityUid uid, float aggression)
    {
        var xform = Transform(uid);

        var isStuck = false;

        var price = 0f;

        return !_gridQuery.HasComp(uid)
            && (xform.ParentUid == xform.MapUid // don't delete if on grid
                || (isStuck |= GetWallStuck((uid, xform)))) // or wall-stuck
            && !_htnQuery.HasComp(uid) // handled by MobCleanupSystem
            && !_immuneQuery.HasComp(uid) // handled by GridCleanupSystem
            && !_mindQuery.HasComp(uid) // no deleting anything that can have a mind - should be handled by MobCleanupSystem anyway
            && (price = (float)_pricing.GetPrice(uid)) <= _maxPrice
            && (isStuck
                || !_cleanup.HasNearbyGrids(xform.Coordinates, _maxGridDistance * aggression * MathF.Sqrt(price / _maxPrice))
                    && !_cleanup.HasNearbyPlayers(xform.Coordinates, _maxDistance * aggression * MathF.Sqrt(price / _maxPrice)));
    }

    private bool GetWallStuck(Entity<TransformComponent> ent)
    {
        if (_overlapArgs.Length == 0) // Triad: TestOverlap is unusable, see Initialize
            return false;

        if (ent.Comp.GridUid is not { } gridUid
            || ent.Comp.Anchored
            || ent.Comp.ParentUid != gridUid // ignore if not directly parented to grid
        )
            return false;

        var xfB = new Transform(ent.Comp.LocalPosition, 0);
        var shapeB = new PhysShapeCircle(0.001f);

        var contacts = _physics.GetContacts(ent.Owner);
        // it dies without this for some reason
        if (contacts == ContactEnumerator.Empty)
            return false;

        while (contacts.MoveNext(out var contact))
        {
            if (contact.FixtureA == null
                || contact.FixtureB == null
                || contact.BodyA == null
                || contact.BodyB == null
                || !contact.FixtureA.Hard
                || !contact.FixtureB.Hard
                || !contact.IsTouching
            )
                continue;

            var isA = contact.EntityB == ent.Owner;

            var body = isA ? contact.BodyA : contact.BodyB;
            // only trigger when the other entity is static
            if ((body.BodyType & BodyType.Static) == 0)
                continue;

            var fix = isA ? contact.FixtureA : contact.FixtureB;
            var xform = isA ? contact.XformA : contact.XformB;
            var anch = isA ? contact.EntityA : contact.EntityB;

            var xf = _physics.GetLocalPhysicsTransform(anch, xform);
            var shape = fix.Shape;

            _overlapArgs[0] = shape;
            _overlapArgs[1] = 0;
            _overlapArgs[2] = shapeB;
            _overlapArgs[3] = 0;
            _overlapArgs[4] = xf;
            _overlapArgs[5] = xfB;

            // Triad: the array is sized to the live signature in Initialize, trailing optionals included.
            // if ((bool?)_testOverlap.Invoke(_manifold, [shape, 0, shapeB, 0, xf, xfB]) ?? false)
            if ((bool?)_testOverlap.Invoke(_manifold, _overlapArgs) ?? false)
                return true;
        }

        return false;
    }

    public void QueueSweep(EntityCoordinates coordinates, TimeSpan time, float radius, float aggression)
    {
        _sweepQueue.Add((coordinates, time, radius, aggression));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        for (int i = _sweepQueue.Count - 1; i >= 0; i--)
        {
            var (coord, time, radius, aggression) = _sweepQueue[i];

            if (_timing.CurTime < time)
                continue;

            _sweepQueue.RemoveAt(i);
            if (!coord.IsValid(EntityManager)
                || radius <= 0f) // Triad: v277 lookup asserts on non-positive range
                continue;

            _sweepEnts.Clear();
            _lookup.GetEntitiesInRange(_transform.ToMapCoordinates(coord), radius, _sweepEnts, LookupFlags.Dynamic | LookupFlags.Approximate | LookupFlags.Sundries);

            foreach (var (uid, body) in _sweepEnts)
            {
                if (ShouldEntityCleanup(uid, aggression))
                    CleanupEnt(uid);
            }
        }
    }
}
