using Content.Shared._NF.Clothing.Components; // Triad
using Content.Shared.Alert;
using Content.Shared.Clothing; // Triad
using Content.Shared.Inventory;
using Content.Shared.Movement.Components;
using Robust.Shared.GameStates;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;

namespace Content.Shared.Gravity
{
    public abstract partial class SharedGravitySystem : EntitySystem
    {
        [Dependency] protected readonly IGameTiming Timing = default!;
        [Dependency] private readonly AlertsSystem _alerts = default!;

        [ValidatePrototypeId<AlertPrototype>]
        public const string WeightlessAlert = "Weightless";

        private EntityQuery<GravityComponent> _gravityQuery;

        // Triad: queries for the IsWeightless raw-event subscriber set (see IsWeightless guard)
        private EntityQuery<InventoryComponent> _inventoryQuery;
        private EntityQuery<MagbootsComponent> _magbootsQuery;
        private EntityQuery<NFMoonBootsComponent> _moonBootsQuery;
        // End Triad

        public bool IsWeightless(EntityUid uid, PhysicsComponent? body = null, TransformComponent? xform = null)
        {
            Resolve(uid, ref body, false);

            if ((body?.BodyType & (BodyType.Static | BodyType.Kinematic)) != 0)
                return false;

            if (TryComp<MovementIgnoreGravityComponent>(uid, out var ignoreGravityComponent))
                return ignoreGravityComponent.Weightless;

            // Triad: skip the IInventoryRelayEvent raise when no subscriber exists.
            // The raw IsWeightlessEvent has exactly three direct subscribers: InventoryComponent
            // (the relay), MagbootsComponent, and NFMoonBootsComponent (verified via the event
            // index). For inventory-less mobs (carp, wildlife, projectile debris) the raise
            // dispatches to zero handlers but still pays the directed event-bus lookup per moving
            // body per tick (~9% of PhysicsSystem.Update under mob-heavy load). Skipping the raise
            // here is bit-equivalent to raising it with no handler (the struct stays Handled=false,
            // so the method falls through to the grid/map gravity check below regardless).
            // If a future component subscribes to the RAW IsWeightlessEvent it MUST be added here,
            // or weightlessness silently breaks for entities carrying only that component.
            if (!_inventoryQuery.HasComp(uid)
                && !_magbootsQuery.HasComp(uid)
                && !_moonBootsQuery.HasComp(uid))
            {
                if (!Resolve(uid, ref xform))
                    return true;

                return !EntityGridOrMapHaveGravity((uid, xform));
            }
            // End Triad

            var ev = new IsWeightlessEvent(uid);
            RaiseLocalEvent(uid, ref ev);
            if (ev.Handled)
                return ev.IsWeightless;

            if (!Resolve(uid, ref xform))
                return true;

            // If grid / map has gravity
            if (EntityGridOrMapHaveGravity((uid, xform)))
                return false;

            return true;
        }

        /// <summary>
        /// Checks if a given entity is currently standing on a grid or map that supports having gravity at all.
        /// </summary>
        public bool EntityOnGravitySupportingGridOrMap(Entity<TransformComponent?> entity)
        {
            entity.Comp ??= Transform(entity);

            return _gravityQuery.HasComp(entity.Comp.GridUid) ||
                   _gravityQuery.HasComp(entity.Comp.MapUid);
        }


        /// <summary>
        /// Checks if a given entity is currently standing on a grid or map that has gravity of some kind.
        /// </summary>
        public bool EntityGridOrMapHaveGravity(Entity<TransformComponent?> entity)
        {
            entity.Comp ??= Transform(entity);

            return _gravityQuery.TryComp(entity.Comp.GridUid, out var gravity) && gravity.Enabled ||
                   _gravityQuery.TryComp(entity.Comp.MapUid, out var mapGravity) && mapGravity.Enabled;
        }

        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<GridInitializeEvent>(OnGridInit);
            SubscribeLocalEvent<AlertSyncEvent>(OnAlertsSync);
            SubscribeLocalEvent<AlertsComponent, EntParentChangedMessage>(OnAlertsParentChange);
            SubscribeLocalEvent<GravityChangedEvent>(OnGravityChange);
            SubscribeLocalEvent<GravityComponent, ComponentGetState>(OnGetState);
            SubscribeLocalEvent<GravityComponent, ComponentHandleState>(OnHandleState);

            _gravityQuery = GetEntityQuery<GravityComponent>();

            // Triad: populate IsWeightless guard queries
            _inventoryQuery = GetEntityQuery<InventoryComponent>();
            _magbootsQuery = GetEntityQuery<MagbootsComponent>();
            _moonBootsQuery = GetEntityQuery<NFMoonBootsComponent>();
            // End Triad
        }

        public override void Update(float frameTime)
        {
            base.Update(frameTime);
            UpdateShake();
        }

        private void OnHandleState(EntityUid uid, GravityComponent component, ref ComponentHandleState args)
        {
            if (args.Current is not GravityComponentState state)
                return;

            if (component.EnabledVV == state.Enabled)
                return;
            component.EnabledVV = state.Enabled;
            var ev = new GravityChangedEvent(uid, component.EnabledVV);
            RaiseLocalEvent(uid, ref ev, true);
        }

        private void OnGetState(EntityUid uid, GravityComponent component, ref ComponentGetState args)
        {
            args.State = new GravityComponentState(component.EnabledVV);
        }

        private void OnGravityChange(ref GravityChangedEvent ev)
        {
            var alerts = AllEntityQuery<AlertsComponent, TransformComponent>();
            while(alerts.MoveNext(out var uid, out _, out var xform))
            {
                if (xform.GridUid != ev.ChangedGridIndex)
                    continue;

                if (!ev.HasGravity)
                {
                    _alerts.ShowAlert(uid, WeightlessAlert);
                }
                else
                {
                    _alerts.ClearAlert(uid, WeightlessAlert);
                }
            }
        }

        private void OnAlertsSync(AlertSyncEvent ev)
        {
            if (IsWeightless(ev.Euid))
            {
                _alerts.ShowAlert(ev.Euid, WeightlessAlert);
            }
            else
            {
                _alerts.ClearAlert(ev.Euid, WeightlessAlert);
            }
        }

        private void OnAlertsParentChange(EntityUid uid, AlertsComponent component, ref EntParentChangedMessage args)
        {
            if (IsWeightless(uid))
            {
                _alerts.ShowAlert(uid, WeightlessAlert);
            }
            else
            {
                _alerts.ClearAlert(uid, WeightlessAlert);
            }
        }

        private void OnGridInit(GridInitializeEvent ev)
        {
            EntityManager.EnsureComponent<GravityComponent>(ev.EntityUid);
        }

        [Serializable, NetSerializable]
        private sealed class GravityComponentState : ComponentState
        {
            public bool Enabled { get; }

            public GravityComponentState(bool enabled)
            {
                Enabled = enabled;
            }
        }
    }

    [ByRefEvent]
    public record struct IsWeightlessEvent(EntityUid Entity, bool IsWeightless = false, bool Handled = false) : IInventoryRelayEvent
    {
        SlotFlags IInventoryRelayEvent.TargetSlots => ~SlotFlags.POCKET;
    }
}
