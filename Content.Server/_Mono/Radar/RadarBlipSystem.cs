
using System.Numerics;
using Content.Shared._Mono.Radar;
using Content.Shared.Projectiles;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Server._Mono.Radar;

public sealed partial class RadarBlipSystem : EntitySystem
{
    [Dependency] private SharedTransformSystem _xform = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private IGameTiming _timing = default!;

    // Per-request report collections.
    private readonly List<BlipNetData> _tempBlipsCache = new();
    private readonly List<HitscanNetData> _tempHitscansCache = new();
    private readonly List<EntityUid> _tempSourcesCache = new();
    private readonly List<Vector2> _tempSourcePositions = new();
    private readonly List<BlipConfig> _tempPaletteCache = new();
    private readonly Dictionary<BlipConfig, ushort> _paletteIndex = new();

    // Shared server-wide snapshot.
    private static readonly TimeSpan SnapshotRefreshInterval =
        TimeSpan.FromMilliseconds(500);

    private readonly List<CachedRadarBlip> _blipSnapshot = new();
    private readonly List<CachedHitscan> _hitscanSnapshot = new();

    private TimeSpan _lastSnapshotRefresh;
    private bool _hasSnapshot;

    // Profiling.
    private const double RadarProfileInterval = 5.0;
    private double _radarProfileAccumulator;

    private TimeSpan _snapshotBlipBuildTime;
    private TimeSpan _snapshotHitscanBuildTime;
    private TimeSpan _blipReportTime;
    private TimeSpan _hitscanReportTime;

    private int _snapshotRefreshes;
    private int _radarRequests;
    private int _blipReports;
    private int _hitscanReports;

    private long _blipsExamined;
    private long _hitscansExamined;
    private long _blipsIncluded;
    private long _hitscansIncluded;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<RequestBlipsEvent>(OnBlipsRequested);
        SubscribeLocalEvent<RadarBlipComponent, ComponentShutdown>(OnBlipShutdown);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _radarProfileAccumulator += frameTime;

        if (_radarProfileAccumulator < RadarProfileInterval)
            return;

        PrintRadarProfile();
        _radarProfileAccumulator = 0;
    }

    private void PrintRadarProfile()
    {
        var snapshotBlipMs = _snapshotBlipBuildTime.TotalMilliseconds;
        var snapshotHitscanMs = _snapshotHitscanBuildTime.TotalMilliseconds;
        var blipReportMs = _blipReportTime.TotalMilliseconds;
        var hitscanReportMs = _hitscanReportTime.TotalMilliseconds;

        var snapshotBlipAvg = _snapshotRefreshes > 0
            ? snapshotBlipMs / _snapshotRefreshes
            : 0;

        var snapshotHitscanAvg = _snapshotRefreshes > 0
            ? snapshotHitscanMs / _snapshotRefreshes
            : 0;

        var blipReportAvg = _blipReports > 0
            ? blipReportMs / _blipReports
            : 0;

        var hitscanReportAvg = _hitscanReports > 0
            ? hitscanReportMs / _hitscanReports
            : 0;

        Log.Info(
            $"[RadarProfile] Requests={_radarRequests} | " +
            $"SnapshotRefreshes={_snapshotRefreshes} | " +
            $"BlipsExamined={_blipsExamined}, " +
            $"SnapshotBlipTotal={snapshotBlipMs:F3}ms, " +
            $"SnapshotBlipAvg={snapshotBlipAvg:F3}ms | " +
            $"HitscansExamined={_hitscansExamined}, " +
            $"SnapshotHitscanTotal={snapshotHitscanMs:F3}ms, " +
            $"SnapshotHitscanAvg={snapshotHitscanAvg:F3}ms | " +
            $"BlipReports={_blipReports}, " +
            $"BlipsIncluded={_blipsIncluded}, " +
            $"BlipReportTotal={blipReportMs:F3}ms, " +
            $"BlipReportAvg={blipReportAvg:F3}ms | " +
            $"HitscanReports={_hitscanReports}, " +
            $"HitscansIncluded={_hitscansIncluded}, " +
            $"HitscanReportTotal={hitscanReportMs:F3}ms, " +
            $"HitscanReportAvg={hitscanReportAvg:F3}ms"
        );

        // Reset profiling counters.
        _snapshotBlipBuildTime = TimeSpan.Zero;
        _snapshotHitscanBuildTime = TimeSpan.Zero;
        _blipReportTime = TimeSpan.Zero;
        _hitscanReportTime = TimeSpan.Zero;

        _snapshotRefreshes = 0;
        _radarRequests = 0;
        _blipReports = 0;
        _hitscanReports = 0;

        _blipsExamined = 0;
        _hitscansExamined = 0;
        _blipsIncluded = 0;
        _hitscansIncluded = 0;
    }

    private void OnBlipsRequested(RequestBlipsEvent ev, EntitySessionEventArgs args)
    {
        if (!TryGetEntity(ev.Radar, out var radarUid)
            || !TryComp<RadarConsoleComponent>(radarUid, out var radar))
            return;

        _radarRequests++;

        // Refresh the shared snapshot only when it has expired.
        RefreshSnapshotIfNeeded();

        // Get this console's radar sources.
        var sourcesEv = new GetRadarSourcesEvent();
        RaiseLocalEvent(radarUid.Value, ref sourcesEv);

        _tempSourcesCache.Clear();
        _tempSourcePositions.Clear();

        if (sourcesEv.Sources != null)
            _tempSourcesCache.AddRange(sourcesEv.Sources);
        else
            _tempSourcesCache.Add(radarUid.Value);

        // Calculate source positions once per request rather than
        // repeatedly calculating them for every cached blip.
        foreach (var source in _tempSourcesCache)
        {
            _tempSourcePositions.Add(_xform.GetWorldPosition(source));
        }

        AssembleBlipsReport(
            (EntityUid)radarUid,
            _tempSourcePositions,
            radar);

        AssembleHitscanReport(
            (EntityUid)radarUid,
            _tempSourcePositions,
            radar);

        var giveEv = new GiveBlipsEvent(
            _tempPaletteCache,
            _tempBlipsCache,
            _tempHitscansCache);

        RaiseNetworkEvent(giveEv, args.SenderSession);

        _tempBlipsCache.Clear();
        _tempHitscansCache.Clear();
        _tempSourcesCache.Clear();
        _tempSourcePositions.Clear();
        _tempPaletteCache.Clear();
        _paletteIndex.Clear();
    }

    // Build one shared snapshot for all radar consoles.
    private void RefreshSnapshotIfNeeded()
    {
        var now = _timing.CurTime;

        if (_hasSnapshot && now - _lastSnapshotRefresh < SnapshotRefreshInterval)
            return;

        BuildBlipSnapshot();
        BuildHitscanSnapshot();

        _lastSnapshotRefresh = now;
        _hasSnapshot = true;
        _snapshotRefreshes++;
    }

    private void BuildBlipSnapshot()
    {
        _blipSnapshot.Clear();

        var sw = new Stopwatch();
        sw.Start();

        var examined = 0;

        var blipQuery = EntityQueryEnumerator<
            RadarBlipComponent,
            TransformComponent,
            PhysicsComponent>();

        while (blipQuery.MoveNext(
                   out var blipUid,
                   out var blip,
                   out var blipXform,
                   out var blipPhysics))
        {
            examined++;

            var blipGrid = blipXform.GridUid;

            var coord = blipXform.Coordinates;

            if (blipXform.ParentUid != blipXform.MapUid
                && blipXform.ParentUid != blipGrid)
            {
                coord = _xform.WithEntityId(
                    coord,
                    blipGrid ?? blipXform.MapUid!.Value);
            }

            var blipVelocity = _physics.GetMapLinearVelocity(
                blipUid,
                blipPhysics,
                blipXform);

            var rotation = _xform.GetWorldRotation(blipXform);

            BlipConfig? gridConfig = null;

            if (blipGrid != null)
            {
                var gridXform = Transform(blipGrid.Value);

                if (TryComp<PhysicsComponent>(
                        blipGrid.Value,
                        out var gridBody))
                {
                    blipVelocity -= _physics.GetLinearVelocity(
                        blipGrid.Value,
                        coord.Position,
                        gridBody);
                }

                blipVelocity = (-gridXform.LocalRotation).RotateVec(blipVelocity);
                rotation -= gridXform.LocalRotation;

                gridConfig = blip.GridConfig;
            }

            _blipSnapshot.Add(new CachedRadarBlip(
                GetNetEntity(blipUid),
                blipXform.MapID,
                blipGrid,
                _xform.GetWorldPosition(blipXform),
                coord,
                blipVelocity,
                rotation,
                blip.Enabled,
                blip.RequireNoGrid,
                blip.VisibleFromOtherGrids,
                blip.MaxDistance,
                blip.Config,
                gridConfig));
        }

        _snapshotBlipBuildTime += sw.Elapsed;
        _blipsExamined += examined;
    }

    private void BuildHitscanSnapshot()
    {
        _hitscanSnapshot.Clear();

        var sw = new Stopwatch();
        sw.Start();

        var examined = 0;

        var hitscanQuery = EntityQueryEnumerator<HitscanRadarComponent>();

        while (hitscanQuery.MoveNext(out var hitscanUid, out var hitscan))
        {
            examined++;

            _hitscanSnapshot.Add(new CachedHitscan(
                hitscan.Enabled,
                hitscan.StartPosition,
                hitscan.EndPosition,
                hitscan.LineThickness,
                hitscan.RadarColor));
        }

        _snapshotHitscanBuildTime += sw.Elapsed;
        _hitscansExamined += examined;
    }

    private void OnBlipShutdown(
        EntityUid blipUid,
        RadarBlipComponent component,
        ComponentShutdown args)
    {
        // Remove destroyed blips immediately instead of waiting
        // for the next 500 ms snapshot refresh.
        var netBlipUid = GetNetEntity(blipUid);

        for (var i = _blipSnapshot.Count - 1; i >= 0; i--)
        {
            if (_blipSnapshot[i].Uid != netBlipUid)
                continue;

            _blipSnapshot.RemoveAt(i);
        }

        var removalEv = new BlipRemovalEvent(netBlipUid);
        RaiseNetworkEvent(removalEv);
    }

    private void AssembleBlipsReport(
        EntityUid uid,
        List<Vector2> sources,
        RadarConsoleComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        var sw = new Stopwatch();
        sw.Start();

        var radarXform = Transform(uid);
        var radarGrid = radarXform.GridUid;
        var radarMapId = radarXform.MapID;

        foreach (var blip in _blipSnapshot)
        {
            if (!blip.Enabled
                || blip.MapId != radarMapId
                || !NearAnySources(
                    blip.WorldPosition,
                    sources,
                    blip.MaxDistance))
                continue;

            if (blip.RequireNoGrid && blip.GridUid != null
                || !blip.VisibleFromOtherGrids && blip.GridUid != radarGrid)
                continue;

            var configIdx = GetOrAddConfig(blip.Config);

            ushort? gridConfigIdx = blip.GridConfig is { } gridConfig
                ? GetOrAddConfig(gridConfig)
                : null;

            _tempBlipsCache.Add(new BlipNetData(
                blip.Uid,
                GetNetCoordinates(blip.Coordinates),
                blip.Velocity,
                blip.Rotation,
                configIdx,
                gridConfigIdx));
        }

        _blipReportTime += sw.Elapsed;
        _blipReports++;
        _blipsIncluded += _tempBlipsCache.Count;
    }

    private ushort GetOrAddConfig(BlipConfig config)
    {
        if (_paletteIndex.TryGetValue(config, out var index))
            return index;

        if (_tempPaletteCache.Count >= ushort.MaxValue)
        {
            Log.Error(
                $"Blip config count overflow! Reached max {ushort.MaxValue}, but trying to add more.");

            return 0;
        }

        index = (ushort)_tempPaletteCache.Count;
        _tempPaletteCache.Add(config);
        _paletteIndex[config] = index;

        return index;
    }

    private void AssembleHitscanReport(
        EntityUid uid,
        List<Vector2> sources,
        RadarConsoleComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        var sw = new Stopwatch();
        sw.Start();

        foreach (var hitscan in _hitscanSnapshot)
        {
            if (!hitscan.Enabled)
                continue;

            // Preserved from the original implementation.
            if (!NearAnySources(
                    hitscan.StartPosition,
                    sources,
                    component.MaxRange)
                && NearAnySources(
                    hitscan.EndPosition,
                    sources,
                    component.MaxRange))
                continue;

            _tempHitscansCache.Add(new HitscanNetData(
                hitscan.StartPosition,
                hitscan.EndPosition,
                hitscan.LineThickness,
                hitscan.RadarColor));
        }

        _hitscanReportTime += sw.Elapsed;
        _hitscanReports++;
        _hitscansIncluded += _tempHitscansCache.Count;
    }

    private bool NearAnySources(
        Vector2 coord,
        List<Vector2> sources,
        float range)
    {
        var rsqr = range * range;

        foreach (var pos in sources)
        {
            if ((pos - coord).LengthSquared() < rsqr)
                return true;
        }

        return false;
    }

    private readonly record struct CachedRadarBlip(
        NetEntity Uid,
        MapId MapId,
        EntityUid? GridUid,
        Vector2 WorldPosition,
        EntityCoordinates Coordinates,
        Vector2 Velocity,
        Angle Rotation,
        bool Enabled,
        bool RequireNoGrid,
        bool VisibleFromOtherGrids,
        float MaxDistance,
        BlipConfig Config,
        BlipConfig? GridConfig);

    private readonly record struct CachedHitscan(
        bool Enabled,
        Vector2 StartPosition,
        Vector2 EndPosition,
        float LineThickness,
        Color RadarColor);
}
