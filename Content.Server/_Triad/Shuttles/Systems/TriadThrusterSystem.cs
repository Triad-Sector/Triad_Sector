using System.Numerics;
using Content.Server.Shuttles.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Physics;
using Content.Shared._Triad.Shuttles;
using System.Linq;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Shuttles.Systems;

public sealed partial class TriadThrusterSystem : SharedTriadThrusterSystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    [Dependency] private EntityQuery<ThrusterProfileComponent> _profileQuery = default!;

    private static readonly TimeSpan RayCheckCooldown = TimeSpan.FromSeconds(5);

    public bool NozzleExposedRaycast(Entity<TransformComponent, ThrusterComponent> ent, bool playerTriggered, out float clearanceFraction)
    {
        clearanceFraction = 0.0f;

        if (!_profileQuery.TryComp(ent.Owner, out var profileComp))
            return true;

        var thruster = ent.Comp2;

        // Prevents horrible lag from spam
        // Manual triggers, such as examining or toggling, will ignore this cooldown.
        if (thruster.NextRayCheck > _timing.CurTime && !playerTriggered)
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

        var rayProfileTable = ThrusterBlockingProfileTable[(int)profileComp.Profile];
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
                if (_whitelist.IsWhitelistPass(BlockThrusterWhitelist, hitEnt))
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
