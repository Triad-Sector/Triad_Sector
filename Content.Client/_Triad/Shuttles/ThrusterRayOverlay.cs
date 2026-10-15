using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Timing;

namespace Content.Client._Triad.Shuttles;

public sealed partial class ThrusterRayOverlay : Overlay
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IEntityManager _entityManager = default!;

    private readonly TriadThrusterSystem _thrusterSystem;

    public ThrusterRayOverlay()
    {
        IoCManager.InjectDependencies(this);
        _thrusterSystem = _entityManager.System<TriadThrusterSystem>();
    }

    private readonly List<TriadThrusterSystem.ActiveThrusterRay> _toRemove = new();

    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    protected override void Draw(in OverlayDrawArgs args)
    {
        var handle = args.WorldHandle;
        foreach (var ray in _thrusterSystem.Rays)
        {
            if (args.MapId != ray.Map)
                continue;

            var color = ray.IsBlocked ? Color.Red : Color.Cyan;

            handle.DrawLine(ray.RayOrigin, ray.RayHit, color);
        }
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        foreach (var rayListing in _thrusterSystem.Rays)
        {
            if (rayListing.LifeTime < _timing.RealTime)
                _toRemove.Add(rayListing);
        }

        foreach (var r in _toRemove)
        {
            _thrusterSystem.Rays.Remove(r);
        }
    }
}
