using Content.Shared._Triad.Weapons.Ranged.Components;
using Content.Shared._Triad.Weapons.Ranged.Events;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Hands;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.PowerCell;
using Content.Shared.PowerCell.Components;
using Content.Shared.Wieldable;
using Content.Shared.Wieldable.Components;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;

namespace Content.Shared._Triad.Weapons.Ranged.Systems;

public sealed class Mla79HarnessSupportSystem : EntitySystem
{
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _movement = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly SharedPowerCellSystem _powerCell = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RequiresMla79HarnessSupportComponent, GunRefreshModifiersEvent>(OnGunRefreshModifiers);
        SubscribeLocalEvent<RequiresMla79HarnessSupportComponent, HeldRelayedEvent<RefreshMovementSpeedModifiersEvent>>(OnRefreshMovementSpeed);
        SubscribeLocalEvent<RequiresMla79HarnessSupportComponent, InventoryRelayedEvent<RefreshMovementSpeedModifiersEvent>>(OnGunInventoryRefreshMovementSpeed);
        SubscribeLocalEvent<RequiresMla79HarnessSupportComponent, GotEquippedHandEvent>(OnGunEquippedHand);
        SubscribeLocalEvent<RequiresMla79HarnessSupportComponent, GotUnequippedHandEvent>(OnGunUnequippedHand);
        SubscribeLocalEvent<RequiresMla79HarnessSupportComponent, GotEquippedEvent>(OnGunEquippedInventory);
        SubscribeLocalEvent<RequiresMla79HarnessSupportComponent, GotUnequippedEvent>(OnGunUnequippedInventory);
        SubscribeLocalEvent<RequiresMla79HarnessSupportComponent, ItemWieldedEvent>(OnGunWielded);
        SubscribeLocalEvent<RequiresMla79HarnessSupportComponent, ItemUnwieldedEvent>(OnGunUnwielded);

        SubscribeLocalEvent<Mla79HarnessComponent, InventoryRelayedEvent<RefreshMovementSpeedModifiersEvent>>(OnHarnessRefreshMovementSpeed);
        SubscribeLocalEvent<Mla79HarnessComponent, GotEquippedEvent>(OnHarnessEquipped);
        SubscribeLocalEvent<Mla79HarnessComponent, GotUnequippedEvent>(OnHarnessUnequipped);
        SubscribeLocalEvent<Mla79HarnessComponent, PowerCellChangedEvent>(OnHarnessPowerCellChanged);
    }

    public bool HasActiveSupport(
        EntityUid gun,
        EntityUid user,
        RequiresMla79HarnessSupportComponent? support = null)
    {
        if (!Resolve(gun, ref support, false))
            return false;

        if (!TryComp(gun, out WieldableComponent? wieldable) || !wieldable.Wielded)
            return false;

        return TryGetPoweredHarness(user, out _);
    }

    public bool TryGetPoweredHarness(
        EntityUid user,
        out EntityUid harnessUid)
    {
        harnessUid = default;

        if (!_inventory.TryGetSlotEntity(user, "belt", out var belt) ||
            !HasComp<Mla79HarnessComponent>(belt.Value))
        {
            return false;
        }

        if (!_powerCell.HasActivatableCharge(belt.Value))
            return false;

        harnessUid = belt.Value;
        return true;
    }

    public bool HasMla79InHandOrSuitStorage(EntityUid user)
    {
        foreach (var held in _hands.EnumerateHeld(user))
        {
            if (HasComp<RequiresMla79HarnessSupportComponent>(held))
                return true;
        }

        return _inventory.TryGetSlotEntity(user, "suitstorage", out var suitStorage) &&
               HasComp<RequiresMla79HarnessSupportComponent>(suitStorage.Value);
    }

    private void OnGunRefreshModifiers(Entity<RequiresMla79HarnessSupportComponent> ent, ref GunRefreshModifiersEvent args)
    {
        if (args.User == null || !HasActiveSupport(ent.Owner, args.User.Value, ent.Comp))
            return;

        args.MinAngle += ent.Comp.MinAngleBonus;
        args.MaxAngle += ent.Comp.MaxAngleBonus;
        args.AngleDecay += ent.Comp.AngleDecayBonus;
        args.AngleIncrease += ent.Comp.AngleIncreaseBonus;
    }

    private void OnRefreshMovementSpeed(
        Entity<RequiresMla79HarnessSupportComponent> ent,
        ref HeldRelayedEvent<RefreshMovementSpeedModifiersEvent> args)
    {
        var user = Transform(ent.Owner).ParentUid;
        if (TryGetPoweredHarness(user, out _))
        {
            if (TryComp<WieldableComponent>(ent.Owner, out var wieldable) && wieldable.Wielded)
                args.Args.ModifySpeed(ent.Comp.PoweredWieldedWalkModifier, ent.Comp.PoweredWieldedSprintModifier);

            return;
        }

        if (TryGetHarnessWithCell(user, out _))
            return;

        args.Args.ModifySpeed(ent.Comp.UnsupportedWalkModifier, ent.Comp.UnsupportedSprintModifier);
    }

    private void OnGunInventoryRefreshMovementSpeed(
        Entity<RequiresMla79HarnessSupportComponent> ent,
        ref InventoryRelayedEvent<RefreshMovementSpeedModifiersEvent> args)
    {
        var user = Transform(ent.Owner).ParentUid;

        if (!_inventory.TryGetSlotEntity(user, "suitstorage", out var suitStorage) ||
            suitStorage.Value != ent.Owner ||
            TryGetPoweredHarness(user, out _) ||
            TryGetHarnessWithCell(user, out _))
        {
            return;
        }

        args.Args.ModifySpeed(ent.Comp.UnsupportedWalkModifier, ent.Comp.UnsupportedSprintModifier);
    }

    private void OnHarnessRefreshMovementSpeed(
        Entity<Mla79HarnessComponent> ent,
        ref InventoryRelayedEvent<RefreshMovementSpeedModifiersEvent> args)
    {
        var user = Transform(ent.Owner).ParentUid;

        if (!HasMla79InHandOrSuitStorage(user) ||
            TryGetPoweredHarness(user, out _))
        {
            return;
        }

        if (!TryGetHarnessWithCell(user, out var harnessUid) ||
            harnessUid != ent.Owner)
        {
            return;
        }

        args.Args.ModifySpeed(ent.Comp.DrainedWalkModifier, ent.Comp.DrainedSprintModifier);
    }

    private void OnGunEquippedHand(Entity<RequiresMla79HarnessSupportComponent> ent, ref GotEquippedHandEvent args)
    {
        RefreshGunAndUser(ent.Owner, args.User);
        RaiseLocalEvent(new Mla79HarnessGunEquippedHandEvent(ent.Owner, args.User));
    }

    private void OnGunUnequippedHand(Entity<RequiresMla79HarnessSupportComponent> ent, ref GotUnequippedHandEvent args)
    {
        RefreshGunAndUser(ent.Owner, args.User);
        RaiseLocalEvent(new Mla79HarnessGunUnequippedHandEvent(args.User));
    }

    private void OnGunEquippedInventory(Entity<RequiresMla79HarnessSupportComponent> ent, ref GotEquippedEvent args)
    {
        RefreshHeldMla79(args.Equipee);
    }

    private void OnGunUnequippedInventory(Entity<RequiresMla79HarnessSupportComponent> ent, ref GotUnequippedEvent args)
    {
        RefreshHeldMla79(args.Equipee);
        RaiseLocalEvent(new Mla79HarnessGunUnequippedInventoryEvent(ent.Owner, args.Equipee, args.Slot));
    }

    private void OnGunWielded(Entity<RequiresMla79HarnessSupportComponent> ent, ref ItemWieldedEvent args)
    {
        RefreshGunAndUser(ent.Owner, args.User);
    }

    private void OnGunUnwielded(Entity<RequiresMla79HarnessSupportComponent> ent, ref ItemUnwieldedEvent args)
    {
        RefreshGunAndUser(ent.Owner, args.User);
    }

    private void OnHarnessEquipped(Entity<Mla79HarnessComponent> ent, ref GotEquippedEvent args)
    {
        RefreshHeldMla79(args.Equipee);
        RaiseLocalEvent(new Mla79HarnessEquippedEvent(ent.Owner, args.Equipee, args.Slot));
    }

    private void OnHarnessUnequipped(Entity<Mla79HarnessComponent> ent, ref GotUnequippedEvent args)
    {
        RefreshHeldMla79(args.Equipee);
        RaiseLocalEvent(new Mla79HarnessUnequippedEvent(ent.Owner, args.Equipee, args.Slot));
    }

    private void OnHarnessPowerCellChanged(Entity<Mla79HarnessComponent> ent, ref PowerCellChangedEvent args)
    {
        var wearer = Transform(ent.Owner).ParentUid;
        RefreshHeldMla79(wearer);
        RaiseLocalEvent(new Mla79HarnessPowerCellChangedEvent(ent.Owner));
    }

    public void RefreshHeldMla79(EntityUid user)
    {
        _movement.RefreshMovementSpeedModifiers(user);

        foreach (var held in _hands.EnumerateHeld(user))
        {
            if (TryComp<GunComponent>(held, out var gun) &&
                HasComp<RequiresMla79HarnessSupportComponent>(held))
            {
                _gun.RefreshModifiers((held, gun), user);
            }
        }
    }

    private void RefreshGunAndUser(EntityUid gunUid, EntityUid user)
    {
        _movement.RefreshMovementSpeedModifiers(user);

        if (TryComp<GunComponent>(gunUid, out var gun))
            _gun.RefreshModifiers((gunUid, gun), user);
    }

    private bool TryGetHarnessWithCell(EntityUid user, out EntityUid harnessUid)
    {
        harnessUid = default;

        if (!_inventory.TryGetSlotEntity(user, "belt", out var belt) ||
            !HasComp<Mla79HarnessComponent>(belt.Value) ||
            !TryComp<PowerCellSlotComponent>(belt.Value, out var slot) ||
            !_itemSlots.TryGetSlot(belt.Value, slot.CellSlotId, out var itemSlot) ||
            itemSlot.Item == null)
        {
            return false;
        }

        harnessUid = belt.Value;
        return true;
    }

}
