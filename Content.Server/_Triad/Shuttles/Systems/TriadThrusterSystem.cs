using System.Numerics;
using Content.Server.Shuttles.Components;
using Content.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Physics;
using Content.Shared.Tag;
using Content.Shared.Whitelist;
using System.Linq;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Shuttles.Systems;

public sealed partial class TriadThrusterSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private TagSystem _tag = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private EntityWhitelistSystem _whitelist = default!;

    private static readonly ProtoId<TagPrototype> GrilleTag = "Grille";
    private static readonly TimeSpan RayCheckCooldown = TimeSpan.FromSeconds(5);
    private const int MaxGrilles = 3;

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
                new ThrusterBlockingRayData(
                    angle: 180d,
                    offsetX: 0.0f,
                    offsetY: 0.0f
                ),

                new ThrusterBlockingRayData(
                    angle: 135d,
                    offsetX: 0.0f,
                    offsetY: 0.15f
                ),

                new ThrusterBlockingRayData(
                    angle: 103d,
                    offsetX: -0.45f,
                    offsetY: 0.05f
                ),

                new ThrusterBlockingRayData(
                    angle: 90d,
                    offsetX: 0.0f,
                    offsetY: 0.0f
                ),

                new ThrusterBlockingRayData(
                    angle: 77d,
                    offsetX: -0.45f,
                    offsetY: 0.05f
                ),

                new ThrusterBlockingRayData(
                    angle: 63d,
                    offsetX: 0.45f,
                    offsetY: 0.05f
                ),

                new ThrusterBlockingRayData(
                    angle: 33d,
                    offsetX: 0.0f,
                    offsetY: 0.25f
                ),

                new ThrusterBlockingRayData(
                    angle: 24.5d,
                    offsetX: 0.25f,
                    offsetY: 0.45f
                ),

                new ThrusterBlockingRayData(
                    angle: 17d,
                    offsetX: 0.25f,
                    offsetY: 0.45f
                ),

                new ThrusterBlockingRayData(
                    angle: 0d,
                    offsetX: 0.0f,
                    offsetY: 0.0f
                ),

                new ThrusterBlockingRayData(
                    angle: -17d,
                    offsetX: -0.25f,
                    offsetY: 0.45f
                ),

                new ThrusterBlockingRayData(
                    angle: -24.5d,
                    offsetX: -0.25f,
                    offsetY: 0.45f
                ),

                new ThrusterBlockingRayData(
                    angle: -33d,
                    offsetX: 0.0f,
                    offsetY: 0.25f
                ),

                new ThrusterBlockingRayData(
                    angle: -63d,
                    offsetX: -0.45f,
                    offsetY: 0.05f
                ),

                new ThrusterBlockingRayData(
                    angle: -77d,
                    offsetX: 0.45f,
                    offsetY: 0.05f
                ),

                new ThrusterBlockingRayData(
                    angle: -90d,
                    offsetX: 0.0f,
                    offsetY: 0.0f
                ),

                new ThrusterBlockingRayData(
                    angle: -103d,
                    offsetX: 0.45f,
                    offsetY: 0.05f
                ),

                new ThrusterBlockingRayData(
                    angle: -135d,
                    offsetX: 0.0f,
                    offsetY: 0.15f
                ),
            ],
            3 // 3 min quality
        ),

        // Large thruster
        new ThrusterBlockingProfileData(
            [
                new ThrusterBlockingRayData(
                    angle: 180d,
                    offsetX: 0.0f,
                    offsetY: 0.0f
                ),

                new ThrusterBlockingRayData(
                    angle: 180d,
                    offsetX: 1.0f,
                    offsetY: 0.0f
                ),

                new ThrusterBlockingRayData(
                    angle: 116.6d,
                    offsetX: 0.5f,
                    offsetY: -0.45f
                ),

                new ThrusterBlockingRayData(
                    angle: -116.6d,
                    offsetX: 0.5f,
                    offsetY: -0.45f
                ),

                new ThrusterBlockingRayData(
                    angle: 90d,
                    offsetX: 1.0f,
                    offsetY: 0.0f
                ),

                new ThrusterBlockingRayData(
                    angle: 90d,
                    offsetX: 1.0f,
                    offsetY: -1.0f
                ),

                new ThrusterBlockingRayData(
                    angle: -90d,
                    offsetX: 0.0f,
                    offsetY: 0.0f
                ),

                new ThrusterBlockingRayData(
                    angle: 62d,
                    offsetX: 0.5f,
                    offsetY: -0.55f
                ),

                new ThrusterBlockingRayData(
                    angle: -62d,
                    offsetX: 0.5f,
                    offsetY: -0.55f
                ),

                new ThrusterBlockingRayData(
                    angle: -53d,
                    offsetX: 0.5f,
                    offsetY: 0.0f
                ),

                new ThrusterBlockingRayData(
                    angle: 30.5d,
                    offsetX: 0.5f,
                    offsetY: 0.0f
                ),

                new ThrusterBlockingRayData(
                    angle: -30.5d,
                    offsetX: 0.5f,
                    offsetY: 0.0f
                ),

                new ThrusterBlockingRayData(
                    angle: 24.5d,
                    offsetX: 0.25f,
                    offsetY: 0.45f
                ),

                new ThrusterBlockingRayData(
                    angle: 17d,
                    offsetX: 0.45f,
                    offsetY: 0.44f
                ),

                new ThrusterBlockingRayData(
                    angle: -17d,
                    offsetX: 0.55f,
                    offsetY: 0.44f
                ),

                new ThrusterBlockingRayData(
                    angle: 0d,
                    offsetX: 0.0f,
                    offsetY: -1.0f
                ),

                new ThrusterBlockingRayData(
                    angle: 0d,
                    offsetX: 1.0f,
                    offsetY: -1.0f
                )
            ],
            3 // 3 min quality
        ),
    ];

    public bool NozzleExposedRaycast(Entity<TransformComponent, ThrusterComponent> ent, out float clearanceFraction)
    {
        clearanceFraction = 0.0f;

        var thruster = ent.Comp2;

        // Prevents horrible lag from spam
        if (thruster.NextRayCheck > _timing.CurTime)
            return thruster.LastRayCheckClear;

        thruster.NextRayCheck = _timing.CurTime + RayCheckCooldown;

        var xform = ent.Comp1;

        if (xform.GridUid == null)
            return true;

        var worldRot = _transform.GetWorldRotation(xform);
        var localRot = xform.LocalRotation.ToVec();
        var localPos = xform.LocalPosition;

        var gridUID = xform.GridUid.Value;

        var clearQuality = 0d;

        var rayProfileTable = ThrusterBlockingProfileTable[(int)thruster.ThrusterProfile];
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
            var grilleCount = 0;
            foreach (var hit in rayResults)
            {
                var hitEnt = hit.HitEntity;
                var hitxForm = Transform(hitEnt);

                // Needs to be on the same grid
                if (hitxForm.GridUid != xform.GridUid)
                    continue;

                if (_tag.HasTag(hitEnt, GrilleTag))
                    grilleCount++;

                // Entities that fit the block whitelist were in the thruster's path. This path is blocked.
                if (_whitelist.IsWhitelistPass(thruster.BlockThrusterWhitelist, hitEnt))
                {
                    blocked = true;
                    break;
                }
            }

            if (grilleCount > MaxGrilles)
                blocked = true;

            // Check if the ray was blocked. Any path that is blocked by 3 or more grilles is considered blocked.
            if (!blocked)
                clearQuality += rayPreset.Quality;
        }

        var cleared = clearQuality >= requiredQuality;
        thruster.LastRayCheckClear = cleared; // Store last value to prevent spam
        clearanceFraction = (float)clearQuality / requiredQuality;

        return clearQuality >= requiredQuality;
    }
}
