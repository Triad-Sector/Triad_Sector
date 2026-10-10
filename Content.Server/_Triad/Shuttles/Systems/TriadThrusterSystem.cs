using System.Numerics;
using Content.Server.Shuttles.Components;
using Content.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Physics;
using Content.Shared.Whitelist;
using System.Linq;
using Robust.Shared.Map;

namespace Content.Server._Triad.Shuttles.Systems;

public sealed partial class TriadThrusterSystem : EntitySystem
{
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private EntityWhitelistSystem _whitelist = default!;

    private const CollisionGroup StructureMask = CollisionGroup.FullTileMask;

    public static readonly List<ThrusterBlockingProfileData> ThrusterBlockingProfileTable =
    [
        // Always blocked
        new ThrusterBlockingProfileData(
            [],
            8
        ),

        // Never blocked
        new ThrusterBlockingProfileData(
            [],
            0
        ),

        // Small slash normal thruster
        new ThrusterBlockingProfileData(
            [
                // Thruster facing direction
                new ThrusterBlockingRayData(
                    angle: 0d,
                    offset: Vector2.Zero,
                    quality: 1
                ),
                // just far enough left or right to see past a 3 deep hole at an angle
                new ThrusterBlockingRayData(
                    angle: -16d,
                    offset: new(-0.45f, 0.45f),
                    quality: 1
                ),
                new ThrusterBlockingRayData(
                    angle: 16d,
                    offset: new(0.45f, 0.45f),
                    quality: 1
                ),
                // 45s left and right of center
                new ThrusterBlockingRayData(
                    angle: 45d,
                    offset: new(0.0f, 0.25f),
                    quality: 1
                ),
                new ThrusterBlockingRayData(
                    angle: -45d,
                    offset: new(0.0f, 0.25f),
                    quality: 1
                ),
                // directly left and right
                new ThrusterBlockingRayData(
                    angle: 90d,
                    offset: Vector2.Zero,
                    quality: 1
                ),
                new ThrusterBlockingRayData(
                    angle: -90d,
                    offset: Vector2.Zero,
                    quality: 1
                ),
                // 45 degrees left and right of reverse
                new ThrusterBlockingRayData(
                    angle: 135d,
                    offset: new(0.0f, 0.25f),
                    quality: 1
                ),
                new ThrusterBlockingRayData(
                    angle: -135d,
                    offset: new(0.0f, 0.25f),
                    quality: 1
                ),
                // directly reverse
                new ThrusterBlockingRayData(
                    angle: 180d,
                    offset: Vector2.Zero,
                    quality: 1
                ),
            ],
            4 // 4 min quality
        ),

        // Large thruster
        new ThrusterBlockingProfileData(
            [
                // Thruster facing direction
                new ThrusterBlockingRayData(
                    angle: 0d,
                    offset: new(0.0f, -1f),
                    quality: 1
                ),
                new ThrusterBlockingRayData(
                    angle: 0d,
                    offset: new(1f, -1f),
                    quality: 1
                ),
                // just far enough left or right to see past a 3 deep hole at an angle
                new ThrusterBlockingRayData(
                    angle: 16d,
                    offset: new(0.5f, 0.45f),
                    quality: 1
                ),
                new ThrusterBlockingRayData(
                    angle: -16d,
                    offset: new(0.5f, 0.45f),
                    quality: 1
                ),
                // just far enough left or right to see past a 2 deep hole at an angle
                new ThrusterBlockingRayData(
                    angle: 25d,
                    offset: new(0.5f, 0.45f),
                    quality: 1
                ),
                new ThrusterBlockingRayData(
                    angle: -25d,
                    offset: new(0.5f, 0.45f),
                    quality: 1
                ),
                // 45s left and right of center
                new ThrusterBlockingRayData(
                    angle: 45d,
                    offset: new(0.5f, 0.0f),
                    quality: 1
                ),
                new ThrusterBlockingRayData(
                    angle: -45d,
                    offset: new(0.5f, 0.0f),
                    quality: 1
                ),
                // directly left and right
                new ThrusterBlockingRayData(
                    angle: 90d,
                    offset: new(1.0f, -1.0f),
                    quality: 1
                ),
                new ThrusterBlockingRayData(
                    angle: 90d,
                    offset: new(1.0f, 0.0f),
                    quality: 1
                ),
                new ThrusterBlockingRayData(
                    angle: -90d,
                    offset: new(0.0f, -1.0f),
                    quality: 1
                ),
                new ThrusterBlockingRayData(
                    angle: -90d,
                    offset: new(1.0f, 0.0f),
                    quality: 1
                ),
                // 45 degrees left and right of reverse
                new ThrusterBlockingRayData(
                    angle: 135d,
                    offset: new(0.5f, 0.0f),
                    quality: 1
                ),
                new ThrusterBlockingRayData(
                    angle: -135d,
                    offset: new(0.5f, 0.0f),
                    quality: 1
                ),
                // directly reverse
                new ThrusterBlockingRayData(
                    angle: 180d,
                    offset: Vector2.Zero,
                    quality: 1
                ),
                // directly reverse
                new ThrusterBlockingRayData(
                    angle: 180d,
                    offset: new(1.0f, 0.0f),
                    quality: 1
                ),
            ],
            5 // 5 min quality
        ),
    ];

    public bool NozzleExposedRaycast(Entity<TransformComponent, ThrusterComponent> ent)
    {
        var xform = ent.Comp1;

        if (xform.GridUid == null)
            return true;

        var worldRot = _transform.GetWorldRotation(xform);
        var localRot = xform.LocalRotation.ToVec();
        var localPos = xform.LocalPosition;

        var gridUID = xform.GridUid.Value;

        var clearQuality = 0d;

        var rayProfileTable = ThrusterBlockingProfileTable[(int)ent.Comp2.ThrusterProfile];
        var requiredQuality = rayProfileTable.RequiredQuality;

        foreach (var rayPreset in rayProfileTable.Rays)
        {
            // Each ray is worth a certain amount of points, defined in the prototype.
            // You need a certain amounts of points for this thruster to be considered 'clear to space'.
            // Default minimum ray quality is 3.
            if (clearQuality >= requiredQuality)
                break;

            var direction = rayPreset.AngleInRadians();

            var offsetX = rayPreset.OffsetX;
            var offsetY = rayPreset.OffsetY;

            // rotate the offset into the correct space
            var rayOffset = new Vector2(
                offsetX * localRot.X - offsetY * localRot.Y,
                offsetX * localRot.Y + offsetY * localRot.X);

            // Offset local coords based on grid, then convert it to map coordinates
            var offsetCoords = new EntityCoordinates(gridUID, localPos + rayOffset);

            // World coords of the start of the ray
            var rayWorldPos = _transform.ToMapCoordinates(offsetCoords).Position;

            // World angle of the ray
            var rayDirection = direction + worldRot;

            var ray = new CollisionRay(rayWorldPos, rayDirection.ToWorldVec(), (int)StructureMask);
            var rayResults = _physics.IntersectRay(xform.MapID, ray, ignoredEnt: ent.Owner, returnOnFirstHit: false).ToList();

            //Log.Debug($"world pos of {ToPrettyString(ent.Owner)}: {rayWorldPos}");
            //Log.Debug($"raycast of {ToPrettyString(ent.Owner)}: {thrusterFacingDir}");
            //Log.Debug($"RAY ANGLE: {rayDirection.GetCardinalDir()}");

            var blocked = false;
            foreach (var hit in rayResults)
            {
                var hitEnt = hit.HitEntity;
                var hitxForm = Transform(hitEnt);

                // Needs to be on the same grid
                if (hitxForm.GridUid != xform.GridUid)
                    continue;

                // Entities that fit the block whitelist were in the thruster's path. This path is blocked.
                if (_whitelist.IsWhitelistPass(ent.Comp2.BlockThrusterWhitelist, hitEnt))
                {
                    blocked = true;
                    break;
                }
            }

            if (!blocked)
                clearQuality += rayPreset.Quality;
        }

        return clearQuality >= requiredQuality;
    }
}
