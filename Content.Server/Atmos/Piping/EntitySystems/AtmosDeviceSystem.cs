using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Components;
using Content.Shared.Atmos.Piping.Components;
using JetBrains.Annotations;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server.Atmos.Piping.EntitySystems
{
    [UsedImplicitly]
    public sealed partial class AtmosDeviceSystem : EntitySystem
    {
        [Dependency] private IGameTiming _gameTiming = default!;
        [Dependency] private AtmosphereSystem _atmosphereSystem = default!;

        private float _timer;

        // Set of atmos devices that are off-grid but have JoinSystem set.
        private readonly HashSet<Entity<AtmosDeviceComponent>> _joinedDevices = new();

        private static AtmosDeviceDisabledEvent _disabledEv = new();
        private static AtmosDeviceEnabledEvent _enabledEv = new();

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<AtmosDeviceComponent, ComponentInit>(OnDeviceInitialize);
            SubscribeLocalEvent<AtmosDeviceComponent, ComponentShutdown>(OnDeviceShutdown);
            // Re-anchoring should be handled by the parent change.
            SubscribeLocalEvent<AtmosDeviceComponent, EntParentChangedMessage>(OnDeviceParentChanged);
            SubscribeLocalEvent<AtmosDeviceComponent, AnchorStateChangedEvent>(OnDeviceAnchorChanged);
        }

        public void JoinAtmosphere(Entity<AtmosDeviceComponent> ent)
        {
            if (ent.Comp.JoinedGrid != null)
            {
                DebugTools.Assert(HasComp<GridAtmosphereComponent>(ent.Comp.JoinedGrid));
                DebugTools.Assert(Transform(ent).GridUid == ent.Comp.JoinedGrid);
                DebugTools.Assert(ent.Comp.RequireAnchored == Transform(ent).Anchored);
                return;
            }

            var component = ent.Comp;
            var transform = Transform(ent);

            if (component.RequireAnchored && !transform.Anchored)
                return;

            // Attempt to add device to a grid atmosphere.
            bool onGrid = (transform.GridUid != null) && _atmosphereSystem.AddAtmosDevice(transform.GridUid!.Value, ent);

            if (!onGrid && component.JoinSystem)
            {
                _joinedDevices.Add(ent);
                component.JoinedSystem = true;
            }

            component.LastProcess = _gameTiming.CurTime;
            RaiseLocalEvent(ent, ref _enabledEv);
        }

        public void LeaveAtmosphere(Entity<AtmosDeviceComponent> ent)
        {
            var component = ent.Comp;
            // Try to remove the component from an atmosphere, and if not
            if (component.JoinedGrid != null && !_atmosphereSystem.RemoveAtmosDevice(component.JoinedGrid.Value, ent))
            {
                // The grid might have been removed but not us... This usually shouldn't happen.
                component.JoinedGrid = null;
                return;
            }

            if (component.JoinedSystem)
            {
                _joinedDevices.Remove(ent);
                component.JoinedSystem = false;
            }

            component.LastProcess = TimeSpan.Zero;
            RaiseLocalEvent(ent, ref _disabledEv);
        }

        public void RejoinAtmosphere(Entity<AtmosDeviceComponent> component)
        {
            LeaveAtmosphere(component);
            JoinAtmosphere(component);
        }

        private void OnDeviceInitialize(Entity<AtmosDeviceComponent> ent, ref ComponentInit args)
        {
            JoinAtmosphere(ent);
        }

        private void OnDeviceShutdown(Entity<AtmosDeviceComponent> ent, ref ComponentShutdown args)
        {
            LeaveAtmosphere(ent);
        }

        private void OnDeviceAnchorChanged(Entity<AtmosDeviceComponent> ent, ref AnchorStateChangedEvent args)
        {
            // Do nothing if the component doesn't require being anchored to function.
            if (!ent.Comp.RequireAnchored)
                return;

            if (args.Anchored)
                JoinAtmosphere(ent);
            else
                LeaveAtmosphere(ent);
        }

        private void OnDeviceParentChanged(Entity<AtmosDeviceComponent> ent, ref EntParentChangedMessage args)
        {
            // Triad: the transform raises this message on every entity at startup, with no old
            // parent, and a device that joined its grid on init then left and rejoined it here.
            // Leaving raises the disabled event, and every pump, filter, mixer and valve answers
            // that by switching itself off, so any loaded grid came up with its distro dead: a
            // stored ship on retrieve, a saved ship on load, a mapped pump without startOnMapInit
            // (reported: "turned on pump became off", "filters and pumps turn off").
            // A device already in the atmosphere of the grid it sits on has nothing to rejoin, and
            // one that is in no atmosphere at all has nothing to leave. A real move between grids
            // still goes through the full leave-and-join below.
            var gridUid = Transform(ent).GridUid;
            if (ent.Comp.JoinedGrid != null && ent.Comp.JoinedGrid == gridUid)
                return;

            if (ent.Comp.JoinedGrid == null && !ent.Comp.JoinedSystem)
            {
                JoinAtmosphere(ent);
                return;
            }

            RejoinAtmosphere(ent);
        }

        /// <summary>
        /// Update atmos devices that are off-grid but have JoinSystem set. For devices updates when
        /// a device is on a grid, see AtmosphereSystem:UpdateProcessing().
        /// </summary>
        public override void Update(float frameTime)
        {
            _timer += frameTime;

            if (_timer < _atmosphereSystem.AtmosTime)
                return;

            _timer -= _atmosphereSystem.AtmosTime;

            var time = _gameTiming.CurTime;
            var ev = new AtmosDeviceUpdateEvent(_atmosphereSystem.AtmosTime, null, null);
            foreach (var device in _joinedDevices)
            {
                var deviceGrid = Transform(device).GridUid;
                if (HasComp<GridAtmosphereComponent>(deviceGrid))
                {
                    RejoinAtmosphere(device);
                }
                RaiseLocalEvent(device, ref ev);
                device.Comp.LastProcess = time;
            }
        }

        public bool IsJoinedOffGrid(Entity<AtmosDeviceComponent> device)
        {
            return _joinedDevices.Contains(device);
        }
    }
}
