using Content.Server.Atmos.EntitySystems;
using Robust.Shared.Physics.Components;
using Robust.Shared.Random;
using Robust.Server.GameObjects;
using Content.Shared.Atmos;
using Content.Shared.Examine;
using Content.Shared.IdentityManagement;
using Robust.Shared.Network;
using Content.Shared.Inventory;
using Robust.Server.Player;
using Content.Shared._NF.AirFreshener.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Popups;
using Robust.Shared.Player;

namespace Content.Server._NF.Traits.Assorted;

/// <summary>
/// This handles stink, causing the affected to stink uncontrollably at a random interval.
/// </summary>
public sealed partial class StinkyTraitSystem : EntitySystem
{
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private INetManager _net = default!;
    [Dependency] private AtmosphereSystem _atmosphere = default!;
    [Dependency] private TransformSystem _transform = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    /// <inheritdoc/>
    public override void Initialize()
    {
        SubscribeLocalEvent<StinkyTraitComponent, ComponentStartup>(SetupStinkyTrait);
        SubscribeLocalEvent<StinkyTraitComponent, ExaminedEvent>(OnExamined);
    }

    private void SetupStinkyTrait(EntityUid uid, StinkyTraitComponent component, ComponentStartup args)
    {
        component.NextIncidentTime =
            _random.NextFloat(component.TimeBetweenIncidents.X, component.TimeBetweenIncidents.Y);
    }

    public void AdjustStinkyTraitTimer(EntityUid uid, int TimerReset, StinkyTraitComponent? stinky = null)
    {
        if (!Resolve(uid, ref stinky, false))
            return;

        stinky.NextIncidentTime = TimerReset;
    }

    private void OnExamined(EntityUid uid, StinkyTraitComponent component, ExaminedEvent args)
    {
        if (args.IsInDetailsRange && !_net.IsClient && component.IsActive)
            args.PushMarkup(Loc.GetString("trait-stinky-examined", ("target", Identity.Entity(uid, EntityManager))));
    }

    private bool OnAirFreshener(EntityUid? uid)
    {
        if (HasComp<AirFreshenerComponent>(uid))
            return false;

        return true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<StinkyTraitComponent>();
        while (query.MoveNext(out var uid, out var component))
        {
            component.NextIncidentTime -= frameTime;

            if (component.NextIncidentTime >= 0)
                continue;

            component.IsActive = true;
            if (_inventory.TryGetSlotEntity(uid, "neck", out var neck)) // Not yet added to any item as neck
                component.IsActive = OnAirFreshener(neck);
            if (_inventory.TryGetSlotEntity(uid, "pocket1", out var pocket1))
                component.IsActive = OnAirFreshener(pocket1);
            if (_inventory.TryGetSlotEntity(uid, "pocket2", out var pocket2))
                component.IsActive = OnAirFreshener(pocket2);
            if (_inventory.TryGetSlotEntity(uid, "pocket3", out var pocket3))
                component.IsActive = OnAirFreshener(pocket3);
            if (_inventory.TryGetSlotEntity(uid, "pocket4", out var pocket4))
                component.IsActive = OnAirFreshener(pocket4);

            // Set the new time.
            component.NextIncidentTime +=
                _random.NextFloat(component.TimeBetweenIncidents.X, component.TimeBetweenIncidents.Y);

            if (!component.IsActive)
                continue;

            var othersMessage = Loc.GetString("trait-stinky-in-range-others", ("target", uid));
            _popup.PopupEntity(othersMessage, uid, Filter.PvsExcept(uid), true);

            var selfMessage = Loc.GetString("trait-stinky-in-range-self");
            _popup.PopupEntity(selfMessage, uid, uid);
        }
    }
}
