using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Timing;

namespace Content.Client._Triad.Shuttles;

public sealed partial class ThrusterRayOverlay(TriadThrusterSystem owner) : Overlay
{
    [Dependency] private IGameTiming _timing = default!;

    private readonly TriadThrusterSystem _owner = owner;
    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    protected override void Draw(in OverlayDrawArgs args)
    {
        var handle = args.WorldHandle;
        foreach (var ray in _owner.Rays)
        {
            if (args.MapId != ray.Map)
                continue;

            var color = ray.IsBlocked ? Color.Red : Color.Green;

            handle.DrawLine(ray.RayOrigin, ray.RayHit, color);
        }
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        _owner.Rays.RemoveAll(r => r.LifeTime < _timing.RealTime);
    }
}
