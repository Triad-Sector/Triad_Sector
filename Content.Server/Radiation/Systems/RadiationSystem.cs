using Content.Server.Radiation.Components;
using Content.Shared.Radiation.Components;
using Content.Shared.Radiation.Events;
using Content.Shared.Stacks;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server.Radiation.Systems;

public sealed partial class RadiationSystem : EntitySystem
{
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedStackSystem _stack = default!;
    [Dependency] private readonly SharedMapSystem _maps = default!;

    private EntityQuery<RadiationBlockingContainerComponent> _blockerQuery;
    private EntityQuery<RadiationGridResistanceComponent> _resistanceQuery;
    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<StackComponent> _stackQuery;

    private float _accumulator;
    private List<SourceData> _sources = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeCvars();
        InitRadBlocking();

        _blockerQuery = GetEntityQuery<RadiationBlockingContainerComponent>();
        _resistanceQuery = GetEntityQuery<RadiationGridResistanceComponent>();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        _stackQuery = GetEntityQuery<StackComponent>();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _accumulator += frameTime;
        if (_accumulator < GridcastUpdateRate)
            return;

        UpdateGridcast();
        UpdateResistanceDebugOverlay();
        _accumulator = 0f;
    }

    public void IrradiateEntity(EntityUid uid, float radsPerSecond, float time) // Triad: no origin arg - our OnIrradiatedEvent predates wizden's origin field
    {
        var msg = new OnIrradiatedEvent(time, radsPerSecond);
        RaiseLocalEvent(uid, msg);
    }

    public void SetSourceEnabled(Entity<RadiationSourceComponent?> entity, bool val)
    {
        if (!Resolve(entity, ref entity.Comp, false))
            return;

        entity.Comp.Enabled = val;
    }

    // Triad: cache-invalidating intensity setter. The gridcast reads only from the cached
    // _sourceTree/_sourceDataMap, so callers must never write Intensity/Slope directly -
    // they have to route through here (or SetSourceEnabled) so UpdateSource refreshes the cache.
    /// <summary>
    ///     Sets the source's radiation intensity (and optionally its falloff slope), refreshing the source cache.
    /// </summary>
    public void SetSourceIntensity(Entity<RadiationSourceComponent?> entity, float intensity, float? slope = null)
    {
        if (!Resolve(entity, ref entity.Comp, false))
            return;

        entity.Comp.Intensity = intensity;
        if (slope is { } s)
            entity.Comp.Slope = s;
    }

    /// <summary>
    ///     Marks entity to receive/ignore radiation rays.
    /// </summary>
    public void SetCanReceive(EntityUid uid, bool canReceive)
    {
        if (canReceive)
        {
            EnsureComp<RadiationReceiverComponent>(uid);
        }
        else
        {
            RemComp<RadiationReceiverComponent>(uid);
        }
    }
}
