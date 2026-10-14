using System.Numerics;
using Robust.Client.Graphics;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Map;
using Robust.Shared.Timing;
using Content.Shared.Examine;
using Content.Shared._Triad.Shuttles;
using System.Linq;

namespace Content.Client._Triad.Shuttles;

public sealed partial class TriadThrusterSystem : SharedTriadThrusterSystem
{
    [Dependency] private IOverlayManager _overlayManager = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedTriadThrusterSystem _sharedTriadThruster = default!;

    private static readonly TimeSpan RayLifeTime = TimeSpan.FromSeconds(5);

    public struct ActiveThrusterRay
    {
        public Vector2 RayOrigin;
        public Vector2 RayHit;
        public TimeSpan LifeTime;
        public bool IsBlocked;
        public MapId Map;
    }

    public readonly List<ActiveThrusterRay> Rays = new();

    public override void Initialize()
    {
        base.Initialize();

        _overlayManager.AddOverlay(new ThrusterRayOverlay(this));
    }

    public override void Shutdown()
    {
        base.Shutdown();

        _overlayManager.RemoveOverlay<ThrusterRayOverlay>();
    }

    [SubscribeLocalEvent]
    private void OnExaminedEvent(Entity<ThrusterProfileComponent> ent, ref ExaminedEvent args)
    {
        var xform = Transform(ent.Owner);

        if (xform.GridUid == null)
            return;

        var worldRot = _transform.GetWorldRotation(xform);
        var localRot = xform.LocalRotation.ToVec();
        var localPos = xform.LocalPosition;

        var gridUID = xform.GridUid.Value;

        var clearQuality = 0d;

        var rayProfileTable = ThrusterBlockingProfileTable[(int)ent.Comp.Profile];
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
            var rayResults = _physics.IntersectRay(xform.MapID, ray, ignoredEnt: ent.Owner, returnOnFirstHit: false);

            var blocked = false;
            var grilleCount = 0;
            foreach (var hit in rayResults.ToList())
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

            var newRayListing = new ActiveThrusterRay
            {
                IsBlocked = blocked,
                RayOrigin = rayWorldPos,
                RayHit = rayDirection.ToWorldVec() * 35 + rayWorldPos,
                LifeTime = _timing.RealTime + RayLifeTime,
                Map = xform.MapID
            };

            Rays.Add(newRayListing);
        }
    }
}
