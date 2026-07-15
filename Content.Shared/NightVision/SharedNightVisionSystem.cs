using Content.Shared.Actions;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.Overlays;
using Content.Shared.Popups; // Triad
using Content.Shared.Whitelist;
using Robust.Shared.Audio.Systems; // Triad

namespace Content.Shared.NightVision;

/// <summary>
/// Shows/hides the <see cref="NightVisionOverlay"/> based on whether the observed
/// entity has a <see cref="NightVisionComponent"/> equipped.
/// </summary>
public abstract partial class SharedNightVisionSystem : EntitySystem
{
    [Dependency] private SharedActionsSystem _actions = default!;
    [Dependency] private SharedAudioSystem _audio = default!; // Triad
    [Dependency] private EntityWhitelistSystem _whitelist = default!; // Triad
    [Dependency] private InventorySystem _inventory = default!; // Triad
    [Dependency] private SharedPopupSystem _popup = default!; // Triad

    public override void Initialize()
    {
        SubscribeLocalEvent<NightVisionComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<NightVisionComponent, ComponentRemove>(OnRemove);
        SubscribeLocalEvent<NightVisionComponent, GotEquippedEvent>(OnCompEquip);
        SubscribeLocalEvent<NightVisionComponent, GotUnequippedEvent>(OnCompUnequip); // Triad
        SubscribeLocalEvent<NightVisionComponent, DidEquipEvent>(OnCompDidEquip); // Triad
        SubscribeLocalEvent<NightVisionComponent, DidUnequipEvent>(OnCompDidUnequip);
        SubscribeLocalEvent<NightVisionComponent, InventoryRelayedEvent<RefreshNightVisionEvent>>(OnRefreshEquipmentHud);
        SubscribeLocalEvent<NightVisionComponent, RefreshNightVisionEvent>(OnRefreshComponentHud);
        SubscribeLocalEvent<ToggleNightVisionEvent>(OnToggleNightVisionEvent);
    }

    private void OnStartup(Entity<NightVisionComponent> ent, ref ComponentStartup args)
    {
        if (ent.Comp.RelayOverlay)
            return;

        RefreshOverlay(ent);
        _actions.AddAction(ent, ref ent.Comp.ActionEntity, ent.Comp.Action);
    }

    private void OnRemove(Entity<NightVisionComponent> ent, ref ComponentRemove args)
    {
        if (ent.Comp.RelayOverlay)
            return;

        RefreshOverlay(ent);
        _actions.RemoveAction(ent.Owner, ent.Comp.ActionEntity);
    }

    private void OnCompEquip(Entity<NightVisionComponent> ent, ref GotEquippedEvent args)
    {
        if (!ent.Comp.RelayOverlay)
            return;

        RefreshOverlay(args.Equipee);
        _actions.AddAction(args.Equipee, ref ent.Comp.ActionEntity, ent.Comp.Action, ent);
    }
    private void OnCompUnequip(Entity<NightVisionComponent> ent, ref GotUnequippedEvent args)
    {
        if (!ent.Comp.RelayOverlay)
            return;

        ent.Comp.Enabled = false; // mono
        Dirty(ent); // triad
        RefreshOverlay(args.Equipee);
    }
    // Triad start
    private void OnCompDidEquip(Entity<NightVisionComponent> ent, ref DidEquipEvent args)
    {
        RefreshOverlay(args.Equipee);
    }
    private void OnCompDidUnequip(Entity<NightVisionComponent> ent, ref DidUnequipEvent args)
    {
        RefreshOverlay(args.Equipee);
    }
    // Triad end
    protected virtual void OnRefreshEquipmentHud(Entity<NightVisionComponent> ent, ref InventoryRelayedEvent<RefreshNightVisionEvent> args)
    {
        var user = Transform(ent).ParentUid; // Triad, attempt to get the user if it was inventory relayed
        if (!IsEnabled(ent, user))
            return;

        args.Args.Entities.Add(ent);
    }
    protected virtual void OnRefreshComponentHud(Entity<NightVisionComponent> ent, ref RefreshNightVisionEvent args)
    {
        if (!IsEnabled(ent, ent.Owner))
            return;

        args.Entities.Add(ent);
    }

    private void OnToggleNightVisionEvent(ToggleNightVisionEvent args)
    {
        var ent = args.Action.Comp.Container;

        if (!TryComp<NightVisionComponent>(ent, out var nightVisionComp))
            return;

        if (!PassBlacklist((ent.Value, nightVisionComp), args.Performer))
        {
            if (nightVisionComp.BlacklistFailPopup is { } popupText)
            {
                var locString = Loc.GetString(popupText);
                _popup.PopupClient(locString, args.Performer, args.Performer, PopupType.SmallCaution);
            }

            if (nightVisionComp.Enabled)
                SetEnabled(ent.Value, false, args.Performer);

            return;
        }

        SetEnabled(ent.Value, !nightVisionComp.Enabled, args.Performer);
        args.Handled = true;
    }

    /// <param name="ent">The night vision to toggle.</param>
    /// <param name="enabled">Whether to enable or disable.</param>
    /// <param name="viewer">Viewer of the night vision, used to refresh their overlay. If null, assumes the night vision entity is the viewer.</param>
    public void SetEnabled(Entity<NightVisionComponent?> ent, bool enabled, EntityUid? viewer = null, bool playSound = true)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return;

        if (ent.Comp.Enabled == enabled)
            return;

        ent.Comp.Enabled = enabled;
        Dirty(ent);

        // Triad start - nvg sound
        var soundToPlay = enabled
            ? ent.Comp.ActivateSound
            : ent.Comp.DeactivateSound;
        if (playSound)
            _audio.PlayLocal(soundToPlay, ent, viewer);
        // Triad end

        RefreshOverlay(viewer ?? ent);
    }

    protected virtual void RefreshOverlay(EntityUid entity) { }

    // Triad start - blacklisted components for NVGs
    public bool IsEnabled(Entity<NightVisionComponent> ent, EntityUid? user = null)
    {
        if (!ent.Comp.Enabled)
            return false;

        if (!_whitelist.CheckBoth(ent.Owner, ent.Comp.BlacklistedComponents))
            return false;

        if (user != null && !PassBlacklist(ent, user.Value))
            return false;

        return true;
    }

    public bool PassBlacklist(Entity<NightVisionComponent> ent, EntityUid user)
    {
        var enumerator = _inventory.GetSlotEnumerator(user, ent.Comp.BlacklistSlotFlags);
        while (enumerator.MoveNext(out var containerSlot))
        {
            if (containerSlot.ContainedEntity is { } item)
            {
                if (!_whitelist.CheckBoth(item, ent.Comp.BlacklistedComponents))
                    return false;
            }
        }

        return true;
    }
    // Triad end
}

[ByRefEvent]
public record struct RefreshNightVisionEvent() : IInventoryRelayEvent
{
    public SlotFlags TargetSlots => SlotFlags.WITHOUT_POCKET;
    public List<Entity<NightVisionComponent>> Entities = new();
}
