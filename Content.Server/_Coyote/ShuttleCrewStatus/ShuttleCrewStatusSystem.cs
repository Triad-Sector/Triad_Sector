using Content.Server.Shuttles.Components;
using Content.Shared._Coyote.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Coyote.ShuttleCrewStatus;

/// <summary>
/// Tracks whether player shuttles currently have active crew and updates IFF color with a delayed reveal.
/// </summary>
public sealed partial class ShuttleCrewStatusSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ISharedPlayerManager _playerManager = default!;
    [Dependency] private SharedShuttleSystem _shuttle = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IRobustRandom _random = default!;

    [Dependency] private EntityQuery<TransformComponent> _transformQuery = default!;
    [Dependency] private EntityQuery<ShuttleComponent> _shuttleQuery = default!;
    [Dependency] private EntityQuery<IFFComponent> _iffQuery = default!;

    // Delay window for visual IFF changes (base + random jitter).
    private TimeSpan _baseVisualDelay = TimeSpan.FromSeconds(20);
    private TimeSpan _randomVisualJitter = TimeSpan.FromSeconds(25);
    // Color used when no active crew is present.
    private Color _inactiveCrewColor = Color.Gray;

    public override void Initialize()
    {
        base.Initialize();

        // Cvars located in CoyoteCCVars.cs
        _cfg.OnValueChanged(
            CoyoteCCVars.ShuttleCrewUpdateBaseDelaySeconds,
            UpdateBaseVisualDelay,
            invokeImmediately: true);

        _cfg.OnValueChanged(
            CoyoteCCVars.ShuttleCrewUpdateRandomJitterSeconds,
            UpdateRandomVisualJitter,
            invokeImmediately: true);

        _cfg.OnValueChanged(
            CoyoteCCVars.ShuttleInactiveCrewColor,
            UpdateInactiveCrewColor,
            invokeImmediately: true);

        // Handles disconnect/zombie transitions even when no movement event is fired.
        _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _cfg.UnsubValueChanged(CoyoteCCVars.ShuttleCrewUpdateBaseDelaySeconds, UpdateBaseVisualDelay);
        _cfg.UnsubValueChanged(CoyoteCCVars.ShuttleCrewUpdateRandomJitterSeconds, UpdateRandomVisualJitter);
        _cfg.UnsubValueChanged(CoyoteCCVars.ShuttleInactiveCrewColor, UpdateInactiveCrewColor);
        _playerManager.PlayerStatusChanged -= OnPlayerStatusChanged;
    }

    private void UpdateBaseVisualDelay(int seconds)
    {
        _baseVisualDelay = TimeSpan.FromSeconds(Math.Max(0, seconds));
    }

    private void UpdateRandomVisualJitter(int seconds)
    {
        _randomVisualJitter = TimeSpan.FromSeconds(Math.Max(0, seconds));
    }

    private void UpdateInactiveCrewColor(string colorText)
    {
        if (!Color.TryFromHex(colorText, out var parsed))
        {
            Log.Warning($"Invalid cvar value for {CoyoteCCVars.ShuttleInactiveCrewColor.Name}: '{colorText}'. Falling back to #808080.");
            _inactiveCrewColor = Color.Gray;
            return;
        }

        _inactiveCrewColor = parsed;
    }

    [SubscribeLocalEvent]
    private void OnIFFMapInit(EntityUid uid, IFFComponent component, MapInitEvent args)
    {
        if (!TryComp<ShuttleComponent>(uid, out _) ||
            (component.Flags & IFFFlags.IsPlayerShuttle) == 0)
        {
            return;
        }
        var crewStatus = EnsureComp<ShuttleCrewStatusComponent>(uid);
        crewStatus.OriginalColor = component.Color;
        crewStatus.HasActiveCrew = HasActivePlayersOnGrid(uid);
        crewStatus.PendingVisualUpdate = false;
    }

    [SubscribeLocalEvent]
    private void OnEntParentChanged(ref EntParentChangedMessage ev)
    {
        var uid = ev.Entity;

        // Ignore non-session entities.
        if (!_playerManager.TryGetSessionByEntity(uid, out var session))
            return;

        // Ignore already inactive sessions.
        if (session.Status is SessionStatus.Disconnected or SessionStatus.Zombie)
            return;

        EntityUid? oldGrid = null;
        var newGrid = ev.Transform.GridUid;

        if (ev.OldParent is { } oldParent &&
            _transformQuery.TryGetComponent(oldParent, out var oldParentXform))
        {
            oldGrid = oldParentXform.GridUid;
        }

        if (oldGrid is { } oldGridUid)
            QueueStatusRefresh(oldGridUid);

        if (newGrid is { } newGridUid && newGridUid != oldGrid)
            QueueStatusRefresh(newGridUid);
    }

    [SubscribeLocalEvent]
    private void OnPlayerAttached(PlayerAttachedEvent ev)
    {
        if (!_transformQuery.TryGetComponent(ev.Entity, out var xform))
            return;

        if (xform.GridUid is not { } gridUid)
            return;

        QueueStatusRefresh(gridUid);
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs e)
    {
        var becameInactive = e.NewStatus is SessionStatus.Disconnected or SessionStatus.Zombie;
        var becameActive = e.NewStatus == SessionStatus.InGame;

        if (!becameInactive && !becameActive)
            return;

        if (e.Session.AttachedEntity is not { } ent)
        {
            // Optional safety net for disconnect/zombie transitions.
            if (becameInactive)
                RefreshTrackedPlayerShuttles();

            // Reconnect activation is handled by PlayerAttachedEvent.
            return;
        }

        if (!_transformQuery.TryGetComponent(ent, out var xform))
            return;

        if (xform.GridUid is not { } gridUid)
            return;

        QueueStatusRefresh(gridUid);
    }

    private void RefreshTrackedPlayerShuttles()
    {
        var shuttleQuery = EntityQueryEnumerator<ShuttleCrewStatusComponent, IFFComponent>();
        while (shuttleQuery.MoveNext(out var shuttleUid, out _, out var iff))
        {
            if ((iff.Flags & IFFFlags.IsPlayerShuttle) == 0)
                continue;

            QueueStatusRefresh(shuttleUid);
        }
    }

    private void QueueStatusRefresh(EntityUid gridUid)
    {
        if (!_shuttleQuery.HasComp(gridUid) ||
            !_iffQuery.TryGetComponent(gridUid, out var iff) ||
            (iff.Flags & IFFFlags.IsPlayerShuttle) == 0)
        {
            return;
        }
        var crewStatus = EnsureComp<ShuttleCrewStatusComponent>(gridUid);

        if (!crewStatus.OriginalColor.HasValue)
            crewStatus.OriginalColor = iff.Color;

        var computedActive = HasActivePlayersOnGrid(gridUid);

        // If applied state is already correct but a stale pending opposite transition exists,
        // cancel the pending transition immediately.
        if (computedActive == crewStatus.HasActiveCrew &&
            crewStatus.PendingVisualUpdate &&
            crewStatus.PendingHasActiveCrew != computedActive)
        {
            crewStatus.PendingVisualUpdate = false;
            return;
        }

        // Compare against the currently represented state (applied or already pending),
        // Stops repeated transitions from causing flickering visual updates or redundant scheduling of visual changes.
        var effectiveActive = crewStatus.PendingVisualUpdate
            ? crewStatus.PendingHasActiveCrew
            : crewStatus.HasActiveCrew;

        // If desired state already represented (or already queued), do nothing.
        if (computedActive == effectiveActive)
            return;

        crewStatus.PendingHasActiveCrew = computedActive;
        crewStatus.PendingVisualUpdate = true;
        crewStatus.ApplyVisualAt = _timing.CurTime + GetVisualDelay();
    }

    private TimeSpan GetVisualDelay()
    {
        if (_randomVisualJitter <= TimeSpan.Zero)
            return _baseVisualDelay;

        var jitterSeconds = _random.NextFloat((float) _randomVisualJitter.TotalSeconds);
        return _baseVisualDelay + TimeSpan.FromSeconds(jitterSeconds);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<ShuttleCrewStatusComponent, IFFComponent>();

        while (query.MoveNext(out var uid, out var crewStatus, out var iff))
        {
            if (!crewStatus.PendingVisualUpdate)
                continue;

            if (now < crewStatus.ApplyVisualAt)
                continue;

            crewStatus.PendingVisualUpdate = false;

            if (crewStatus.PendingHasActiveCrew == crewStatus.HasActiveCrew)
                continue;

            crewStatus.HasActiveCrew = crewStatus.PendingHasActiveCrew;

            if (crewStatus.HasActiveCrew)
            {
                if (crewStatus.OriginalColor.HasValue)
                    _shuttle.SetIFFColor(uid, crewStatus.OriginalColor.Value, iff);
            }
            else
            {
                _shuttle.SetIFFColor(uid, _inactiveCrewColor, iff);
            }
        }
    }

    private bool HasActivePlayersOnGrid(EntityUid gridUid)
    {
        foreach (var session in _playerManager.Sessions)
        {
            // Ignore ghosted/observer players for shuttle crew activity.
            if (session.Status is SessionStatus.Disconnected or SessionStatus.Zombie ||
                session.AttachedEntity is not { } playerEntity ||
                HasComp<GhostComponent>(playerEntity) ||
                !_transformQuery.TryGetComponent(playerEntity, out var playerXform))
            {
                continue;
            }

            if (playerXform.GridUid == gridUid)
                return true;
        }

        return false;
    }
}
