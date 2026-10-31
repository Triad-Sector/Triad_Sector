// New Frontiers - This file is licensed under AGPLv3
// Copyright (c) 2024 New Frontiers Contributors
// See AGPLv3.txt for details.
using Content.Server._Mono.Shuttles.Components;
using Content.Server._NF.Station.Components;
using Content.Server.Shuttles.Components;
using Content.Shared._NF.Shuttles.Events;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Physics; // Mono
using Robust.Shared.Physics.Components;

namespace Content.Server.Shuttles.Systems;

public sealed partial class ShuttleSystem
{
    private const float SpaceFrictionStrength = 0.15f; // Triad from 0.0075f to 0.15f, so shuttle won't drift away when unmmanned/broken
    private const float DampenDampingStrength = 0.50f; // Triad from 0.25f to 0.5
    private const float AnchorDampingStrength = 2.5f;

    // Triad
    public readonly float CruiseModeLinearAccelerationMultiplier = 4f;
    public readonly float DriveModeThrusterMultiplier = 2.5f;
    // Triad end
    private void NfInitialize()
    {
        SubscribeLocalEvent<ShuttleConsoleComponent, SetInertiaDampeningRequest>(OnSetInertiaDampening);
        SubscribeLocalEvent<ShuttleConsoleComponent, SetMaxShuttleSpeedRequest>(OnSetMaxShuttleSpeed);
        SubscribeLocalEvent<ShuttleConsoleComponent, SetServiceFlagsRequest>(NfSetServiceFlags); // Frontier
    }

    public bool SetInertiaDampening(EntityUid uid, PhysicsComponent physicsComponent, ShuttleComponent shuttleComponent, TransformComponent transform, InertiaDampeningMode mode) // Mono: public for ship AI (ported with #4064)
    {
        if (!transform.GridUid.HasValue)
        {
            return false;
        }

        if (mode == InertiaDampeningMode.Query)
        {
            _console.RefreshShuttleConsoles(transform.GridUid.Value);
            return false;
        }

        // Mono - remove shuttle deed requirement, kill StationDampening
        if ((physicsComponent.BodyType & BodyType.Static) != 0)
        {
            return false;
        }

        switch (mode)
        {
            case InertiaDampeningMode.Off:
                {
                    shuttleComponent.BaseThrusterMultiplier = 1f;
                    shuttleComponent.MaxLinearVelocityMultiplier = CruiseModeLinearAccelerationMultiplier;
                    shuttleComponent.BodyModifier = SpaceFrictionStrength;
                    break;
                }
            case InertiaDampeningMode.Dampen:
                {
                    shuttleComponent.BaseThrusterMultiplier = DriveModeThrusterMultiplier;
                    shuttleComponent.MaxLinearVelocityMultiplier = 1f;
                    shuttleComponent.BodyModifier = DampenDampingStrength;
                    break;
                }
            case InertiaDampeningMode.Anchor:
                {
                    shuttleComponent.BaseThrusterMultiplier = 1f;
                    shuttleComponent.MaxLinearVelocityMultiplier = 1f;
                    shuttleComponent.BodyModifier = AnchorDampingStrength;
                    break;
                }
        }

        if (shuttleComponent.DampingModifier != 0)
            shuttleComponent.DampingModifier = shuttleComponent.BodyModifier;
        _console.RefreshShuttleConsoles(transform.GridUid.Value);
        return true;
    }

    private void OnSetInertiaDampening(EntityUid uid, ShuttleConsoleComponent component, SetInertiaDampeningRequest args)
    {
        // Ensure that the entity requested is a valid shuttle (stations should not be togglable)
        if (!EntityManager.TryGetComponent(uid, out TransformComponent? transform) ||
            !transform.GridUid.HasValue ||
            !EntityManager.TryGetComponent(transform.GridUid, out PhysicsComponent? physicsComponent) ||
            !EntityManager.TryGetComponent(transform.GridUid, out ShuttleComponent? shuttleComponent))
        {
            return;
        }

        if (SetInertiaDampening(uid, physicsComponent, shuttleComponent, transform, args.Mode) && args.Mode != InertiaDampeningMode.Query)
            component.DampeningMode = args.Mode;
    }

    private void OnSetMaxShuttleSpeed(EntityUid uid, ShuttleConsoleComponent component, SetMaxShuttleSpeedRequest args)
    {
        // Ensure that the entity requested is a valid shuttle
        var xform = Transform(uid);
        if (!xform.GridUid.HasValue ||
            !TryComp<ShuttleComponent>(xform.GridUid, out var shuttleComponent) ||
            !TryComp<PilotComponent>(args.Actor, out var pilot))
        {
            return;
        }

        var maxSpeed = args.MaxSpeed;
        if (maxSpeed is { } speed)
            maxSpeed = Math.Max(speed, 0f);

        pilot.SetMaxVelocity = maxSpeed;

        // Refresh the shuttle consoles to update the UI
        _console.RefreshShuttleConsoles(xform.GridUid.Value);
    }

    public InertiaDampeningMode NfGetInertiaDampeningMode(EntityUid entity)
    {
        if (!EntityManager.TryGetComponent<TransformComponent>(entity, out var xform))
            return InertiaDampeningMode.Dampen;

        // Not a shuttle, shouldn't be togglable // Mono - remove shuttle deed requirement, kill StationDampening
        if (TryComp<PhysicsComponent>(xform.GridUid, out var body) && (body.BodyType & BodyType.Static) != 0)
            return InertiaDampeningMode.Station;

        if (!EntityManager.TryGetComponent(xform.GridUid, out ShuttleComponent? shuttle))
            return InertiaDampeningMode.Dampen;

        if (shuttle.BodyModifier >= AnchorDampingStrength)
            return InertiaDampeningMode.Anchor;
        else if (shuttle.BodyModifier <= SpaceFrictionStrength)
            return InertiaDampeningMode.Off;
        else
            return InertiaDampeningMode.Dampen;
    }

    public void NfSetPowered(EntityUid uid, ShuttleConsoleComponent component, bool powered)
    {
        // Ensure that the entity requested is a valid shuttle (stations should not be togglable)
        if (!EntityManager.TryGetComponent(uid, out TransformComponent? transform) ||
            !transform.GridUid.HasValue ||
            !EntityManager.TryGetComponent(transform.GridUid, out PhysicsComponent? physicsComponent) ||
            !EntityManager.TryGetComponent(transform.GridUid, out ShuttleComponent? shuttleComponent))
        {
            return;
        }

        // Update dampening physics without adjusting requested mode.
        if (!powered)
        {
            SetInertiaDampening(uid, physicsComponent, shuttleComponent, transform, InertiaDampeningMode.Anchor);
        }
        else
        {
            // Update our dampening mode if we need to, and if we aren't a station.
            var currentDampening = NfGetInertiaDampeningMode(uid);
            if (currentDampening != component.DampeningMode &&
                currentDampening != InertiaDampeningMode.Station &&
                component.DampeningMode != InertiaDampeningMode.Station)
            {
                SetInertiaDampening(uid, physicsComponent, shuttleComponent, transform, component.DampeningMode);
            }
        }
    }

    /// <summary>
    /// Get the current service flags for this grid.
    /// </summary>
    public ServiceFlags NfGetServiceFlags(EntityUid uid)
    {
        var transform = Transform(uid);
        // Get the grid entity from the console transform
        if (!transform.GridUid.HasValue)
            return ServiceFlags.None;

        var gridUid = transform.GridUid.Value;

        // Get the service flags from the IFFComponent.
        if (!EntityManager.TryGetComponent<IFFComponent>(gridUid, out var iffComponent))
            return ServiceFlags.None;

        return iffComponent.ServiceFlags;
    }

    /// <summary>
    /// Set the service flags for this grid.
    /// </summary>
    public void NfSetServiceFlags(EntityUid uid, ShuttleConsoleComponent component, SetServiceFlagsRequest args)
    {
        var transform = Transform(uid);
        // Get the grid entity from the console transform
        if (!transform.GridUid.HasValue)
            return;

        var gridUid = transform.GridUid.Value;

        // Set the service flags on the IFFComponent.
        if (!EntityManager.TryGetComponent<IFFComponent>(gridUid, out var iffComponent))
            return;

        SetServiceFlags(gridUid, args.ServiceFlags, iffComponent); // honor the ReadOnly POI IFF guard instead of writing the field directly
        _console.RefreshShuttleConsoles(gridUid); // push updated nav state to the shuttle consoles
    }

}
