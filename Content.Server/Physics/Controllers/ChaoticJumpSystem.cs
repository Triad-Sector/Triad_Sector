using Content.Server.Physics.Components;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Physics;
using System.Numerics;
using Robust.Shared.Physics.Controllers;
using Robust.Shared.Physics.Collision.Shapes;

namespace Content.Server.Physics.Controllers;

/// <summary>
/// A component which makes its entity periodically chaotic jumps arounds
/// </summary>
public sealed partial class ChaoticJumpSystem : VirtualController
{
    [Dependency] private IGameTiming _gameTiming = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private RayCastSystem _rayCast = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ChaoticJumpComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<ChaoticJumpComponent> chaotic, ref MapInitEvent args)
    {
        //So the entity doesn't teleport instantly. For tesla, for example, it's important for it to eat tesla's generator.
        chaotic.Comp.NextJumpTime = _gameTiming.CurTime + TimeSpan.FromSeconds(_random.NextFloat(chaotic.Comp.JumpMinInterval, chaotic.Comp.JumpMaxInterval));
    }

    public override void UpdateBeforeSolve(bool prediction, float frameTime)
    {
        base.UpdateBeforeSolve(prediction, frameTime);

        var query = EntityQueryEnumerator<ChaoticJumpComponent>();
        while (query.MoveNext(out var uid, out var chaotic))
        {
            //Jump
            if (chaotic.NextJumpTime <= _gameTiming.CurTime)
            {
                Jump(uid, chaotic);
                chaotic.NextJumpTime += TimeSpan.FromSeconds(_random.NextFloat(chaotic.JumpMinInterval, chaotic.JumpMaxInterval));
            }
        }
    }

    private void Jump(EntityUid uid, ChaoticJumpComponent component)
    {
        var xform = Transform(uid);
        var origin = _physics.GetPhysicsTransform(uid);
        var startPos = origin.Position;

        var direction = _random.NextAngle();
        var dir = direction.ToVec();
        var range = _random.NextFloat(component.RangeMin, component.RangeMax);
        var translation = dir * range;

        // Triad: replaced the zero-width raycast + 1-tile offset below with a footprint shape-sweep.
        // The zero-width ray could thread sub-tile gaps (containment-field corner slots) and the teleport
        // ignored physics entirely, so the tesla could escape a correctly-built cage. Sweeping the body's
        // own circle stops it on anything it could not physically pass through. A circle is rotation-
        // invariant, so the origin transform's rotation is irrelevant. Old logic preserved:
        // var ray = new CollisionRay(startPos, direction.ToVec(), component.CollisionMask);
        // var rayCastResults = _physics.IntersectRay(xform.MapID, ray, range, uid, returnOnFirstHit: false).FirstOrNull();
        // if (rayCastResults != null)
        // {
        //     targetPos = rayCastResults.Value.HitPos;
        //     targetPos = new Vector2(targetPos.X - (float) Math.Cos(direction), targetPos.Y - (float) Math.Sin(direction));
        // }
        // else
        // {
        //     targetPos = new Vector2(startPos.X + range * (float) Math.Cos(direction), startPos.Y + range * (float) Math.Sin(direction));
        // }
        var shape = new PhysShapeCircle(component.SweepRadius);
        var filter = new QueryFilter
        {
            MaskBits = component.CollisionMask,
            IsIgnored = entity => entity == uid,
        };

        var result = _rayCast.CastShape(xform.MapID, shape, origin, translation, filter, RayCastSystem.RayCastClosestCallback);

        Vector2 targetPos;
        if (result.Hit)
        {
            // Land just short of the first solid contact along the sweep.
            var stopDistance = MathF.Max(0f, range * result.Results[0].Fraction - component.SweepSkin);
            targetPos = startPos + dir * stopDistance;
        }
        else
        {
            targetPos = startPos + translation;
        }

        Spawn(component.Effect, xform.Coordinates);

        _transform.SetWorldPosition(uid, targetPos);
    }
}
