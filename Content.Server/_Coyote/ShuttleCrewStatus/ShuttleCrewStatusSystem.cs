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

    // Delay window for visual IFF changes (base + random jitter).
    private TimeSpan _baseVisualDelay = TimeSpan.FromSeconds(20);
    private TimeSpan _randomVisualJitter = TimeSpan.FromSeconds(25);
    // Color used when no active crew is present.
    private Color _inactiveCrewColor = Color.Gray;
    // Verbose debug logging toggle.
    private bool _debug;

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

        _cfg.OnValueChanged(
            CoyoteCCVars.ShuttleCrewStatusDebug,
            UpdateDebug,
            invokeImmediately: true);

        SubscribeLocalEvent<IFFComponent, MapInitEvent>(OnIFFMapInit);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundCleanup);
        SubscribeLocalEvent<EntParentChangedMessage>(OnEntParentChanged);
        SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);

        // Handles disconnect/zombie transitions even when no movement event is fired.
        _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _cfg.UnsubValueChanged(CoyoteCCVars.ShuttleCrewUpdateBaseDelaySeconds, UpdateBaseVisualDelay);
        _cfg.UnsubValueChanged(CoyoteCCVars.ShuttleCrewUpdateRandomJitterSeconds, UpdateRandomVisualJitter);
        _cfg.UnsubValueChanged(CoyoteCCVars.ShuttleInactiveCrewColor, UpdateInactiveCrewColor);
        _cfg.UnsubValueChanged(CoyoteCCVars.ShuttleCrewStatusDebug, UpdateDebug);
        _playerManager.PlayerStatusChanged -= OnPlayerStatusChanged;
    }

    private void UpdateBaseVisualDelay(int seconds)
    {
        _baseVisualDelay = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (_debug)
            Log.Info($"[ShuttleCrewStatus] baseDelay={_baseVisualDelay}");
    }

    private void UpdateRandomVisualJitter(int seconds)
    {
        _randomVisualJitter = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (_debug)
            Log.Info($"[ShuttleCrewStatus] randomJitter={_randomVisualJitter}");
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
        if (_debug)
            Log.Info($"[ShuttleCrewStatus] inactiveColor={_inactiveCrewColor}");
    }

    private void UpdateDebug(bool value)
    {
        _debug = value;
        Log.Info($"[ShuttleCrewStatus] debug={_debug}");
    }

    private void OnIFFMapInit(EntityUid uid, IFFComponent component, MapInitEvent args)
    {
        if (!TryComp<ShuttleComponent>(uid, out _))
            return;

        if ((component.Flags & IFFFlags.IsPlayerShuttle) == 0)
            return;

        var crewStatus = EnsureComp<ShuttleCrewStatusComponent>(uid);
        crewStatus.OriginalColor = component.Color;
        crewStatus.HasActiveCrew = HasActivePlayersOnGrid(uid);
        crewStatus.PendingVisualUpdate = false;

        if (_debug)
        {
            Log.Info($"[ShuttleCrewStatus] MapInit grid={uid} " +
                     $"origColor={crewStatus.OriginalColor} hasActive={crewStatus.HasActiveCrew}");
        }
    }

    private void OnRoundCleanup(RoundRestartCleanupEvent ev)
    {
        var query = EntityQueryEnumerator<ShuttleCrewStatusComponent>();
        var removed = 0;
        while (query.MoveNext(out var uid, out _))
        {
            RemComp<ShuttleCrewStatusComponent>(uid);
            removed++;
        }

        if (_debug)
            Log.Info($"[ShuttleCrewStatus] RoundCleanup removed={removed}");
    }

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
            TryComp(oldParent, out TransformComponent? oldParentXform))
        {
            oldGrid = oldParentXform.GridUid;
        }

        if (_debug)
        {
            Log.Info($"[ShuttleCrewStatus] ParentChanged entity={uid} oldParent={ev.OldParent} oldMap={ev.OldMapId} " +
                     $"oldGrid={oldGrid} newGrid={newGrid} sessionStatus={session.Status}");
        }

        if (oldGrid is { } oldGridUid)
            QueueStatusRefresh(oldGridUid);

        if (newGrid is { } newGridUid && newGridUid != oldGrid)
            QueueStatusRefresh(newGridUid);
    }

    private void OnPlayerAttached(PlayerAttachedEvent ev)
    {
        if (!TryComp(ev.Entity, out TransformComponent? xform))
            return;

        if (xform.GridUid is not { } gridUid)
            return;

        if (_debug)
            Log.Info($"[ShuttleCrewStatus] PlayerAttached entity={ev.Entity} grid={gridUid}");

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
            if (_debug)
                Log.Info($"[ShuttleCrewStatus] StatusChanged with null AttachedEntity (old={e.OldStatus}, new={e.NewStatus})");

            // Optional safety net for disconnect/zombie transitions.
            if (becameInactive)
                RefreshTrackedPlayerShuttles();

            // Reconnect activation is handled by PlayerAttachedEvent.
            return;
        }

        if (!TryComp(ent, out TransformComponent? xform))
            return;

        if (xform.GridUid is not { } gridUid)
            return;

        if (_debug)
            Log.Info($"[ShuttleCrewStatus] PlayerStatusChanged entity={ent} old={e.OldStatus} new={e.NewStatus} grid={gridUid}");

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
        if (_debug)
            Log.Info($"[ShuttleCrewStatus] Refresh start grid={gridUid}");

        if (!TryComp<ShuttleComponent>(gridUid, out _))
        {
            if (_debug) Log.Info($"[ShuttleCrewStatus] Refresh skip grid={gridUid}: no ShuttleComponent");
            return;
        }

        if (!TryComp<IFFComponent>(gridUid, out var iff))
        {
            if (_debug) Log.Info($"[ShuttleCrewStatus] Refresh skip grid={gridUid}: no IFFComponent");
            return;
        }

        if ((iff.Flags & IFFFlags.IsPlayerShuttle) == 0)
        {
            if (_debug) Log.Info($"[ShuttleCrewStatus] Refresh skip grid={gridUid}: not IsPlayerShuttle");
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

            if (_debug)
            {
                Log.Info($"[ShuttleCrewStatus] Refresh canceled stale pending transition grid={gridUid} " +
                         $"applied={crewStatus.HasActiveCrew} pendingTarget={crewStatus.PendingHasActiveCrew} computed={computedActive}");
            }

            return;
        }

        // Compare against the currently represented state (applied or already pending),
        // Stops repeated transitions from causing flickering visual updates or redundant scheduling of visual changes.
        var effectiveActive = crewStatus.PendingVisualUpdate
            ? crewStatus.PendingHasActiveCrew
            : crewStatus.HasActiveCrew;

        if (_debug)
        {
            Log.Info($"[ShuttleCrewStatus] Refresh grid={gridUid} computed={computedActive} " +
                     $"applied={crewStatus.HasActiveCrew} pending={crewStatus.PendingVisualUpdate} " +
                     $"pendingTarget={crewStatus.PendingHasActiveCrew} effective={effectiveActive}");
        }

        // If desired state already represented (or already queued), do nothing.
        if (computedActive == effectiveActive)
        {
            if (_debug) Log.Info($"[ShuttleCrewStatus] Refresh no-op grid={gridUid}: computed matches effective");
            return;
        }

        crewStatus.PendingHasActiveCrew = computedActive;
        crewStatus.PendingVisualUpdate = true;
        crewStatus.ApplyVisualAt = _timing.CurTime + GetVisualDelay();

        if (_debug)
        {
            var delay = crewStatus.ApplyVisualAt - _timing.CurTime;
            Log.Info($"[ShuttleCrewStatus] Refresh scheduled grid={gridUid} targetActive={computedActive} " +
                     $"applyAt={crewStatus.ApplyVisualAt} delay={delay}");
        }
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
            {
                if (_debug)
                    Log.Info($"[ShuttleCrewStatus] Apply no-op grid={uid}: pending matches applied");
                continue;
            }

            crewStatus.HasActiveCrew = crewStatus.PendingHasActiveCrew;

            if (crewStatus.HasActiveCrew)
            {
                if (crewStatus.OriginalColor.HasValue)
                    _shuttle.SetIFFColor(uid, crewStatus.OriginalColor.Value, iff);

                if (_debug)
                    Log.Info($"[ShuttleCrewStatus] Apply grid={uid}: ACTIVE restoreColor={crewStatus.OriginalColor}");
            }
            else
            {
                _shuttle.SetIFFColor(uid, _inactiveCrewColor, iff);

                if (_debug)
                    Log.Info($"[ShuttleCrewStatus] Apply grid={uid}: INACTIVE setColor={_inactiveCrewColor}");
            }
        }
    }

    private bool HasActivePlayersOnGrid(EntityUid gridUid)
    {
        foreach (var session in _playerManager.Sessions)
        {
            if (session.Status is SessionStatus.Disconnected or SessionStatus.Zombie)
                continue;

            if (session.AttachedEntity is not { } playerEntity)
                continue;

            // Ignore ghosted/observer players for shuttle crew activity.
            if (HasComp<GhostComponent>(playerEntity))
                continue;

            if (!TryComp(playerEntity, out TransformComponent? playerXform))
                continue;

            if (playerXform.GridUid == gridUid)
            {
                if (_debug)
                    Log.Info($"[ShuttleCrewStatus] ActiveCheck grid={gridUid}: TRUE via playerEntity={playerEntity}");
                return true;
            }
        }

        if (_debug)
            Log.Info($"[ShuttleCrewStatus] ActiveCheck grid={gridUid}: FALSE");

        return false;
    }
}
