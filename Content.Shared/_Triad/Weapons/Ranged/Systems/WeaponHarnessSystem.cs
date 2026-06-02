using Content.Shared._Goobstation.Weapons.SmartGun;
using Content.Shared._Goobstation.Wizard.Projectiles;
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

public sealed class WeaponHarnessSystem : EntitySystem
{
    public const string BeltSlot = "belt";
    public const string SuitStorageSlot = "suitstorage";

    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _movement = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly SharedPowerCellSystem _powerCell = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ReqWeapHarnComponent, GunRefreshModifiersEvent>(OnGunRefreshModifiers);
        SubscribeLocalEvent<ReqWeapHarnComponent, HeldRelayedEvent<RefreshMovementSpeedModifiersEvent>>(OnRefreshMovementSpeed);
        SubscribeLocalEvent<ReqWeapHarnComponent, InventoryRelayedEvent<RefreshMovementSpeedModifiersEvent>>(OnGunInventoryRefreshMovementSpeed);
        SubscribeLocalEvent<ReqWeapHarnComponent, GotEquippedHandEvent>(OnGunEquippedHand);
        SubscribeLocalEvent<ReqWeapHarnComponent, GotUnequippedHandEvent>(OnGunUnequippedHand);
        SubscribeLocalEvent<ReqWeapHarnComponent, GotEquippedEvent>(OnGunEquippedInventory);
        SubscribeLocalEvent<ReqWeapHarnComponent, GotUnequippedEvent>(OnGunUnequippedInventory);
        SubscribeLocalEvent<ReqWeapHarnComponent, ItemWieldedEvent>(OnGunWielded);
        SubscribeLocalEvent<ReqWeapHarnComponent, ItemUnwieldedEvent>(OnGunUnwielded);
        SubscribeLocalEvent<ReqWeapHarnComponent, AmmoShotEvent>(OnAmmoShot, after: [typeof(SmartGunSystem)]);

        SubscribeLocalEvent<WeapHarnComponent, InventoryRelayedEvent<RefreshMovementSpeedModifiersEvent>>(OnHarnessRefreshMovementSpeed);
        SubscribeLocalEvent<WeapHarnComponent, GotEquippedEvent>(OnHarnessEquipped);
        SubscribeLocalEvent<WeapHarnComponent, GotUnequippedEvent>(OnHarnessUnequipped);
        SubscribeLocalEvent<WeapHarnComponent, PowerCellChangedEvent>(OnHarnessPowerCellChanged);
    }

    public bool HasActiveSupport(
        EntityUid gun,
        EntityUid user,
        ReqWeapHarnComponent? support = null)
    {
        return TryGetActivePoweredHarness(gun, user, support, out _);
    }

    public bool TryGetActivePoweredHarness(
        EntityUid gun,
        EntityUid user,
        ReqWeapHarnComponent? support,
        out Entity<WeapHarnComponent> harness)
    {
        harness = default;

        return Resolve(gun, ref support, false) &&
               TryComp(gun, out WieldableComponent? wieldable) &&
               wieldable.Wielded &&
               TryGetPoweredHarnessEntity(user, support.SupportKey, out harness);
    }

    public bool TryGetPoweredHarness(
        EntityUid user,
        string supportKey,
        out EntityUid harnessUid)
    {
        harnessUid = default;

        if (!TryGetPoweredHarnessEntity(user, supportKey, out var harness))
            return false;

        harnessUid = harness.Owner;
        return true;
    }

    public bool TryGetPoweredHarnessEntity(
        EntityUid user,
        string supportKey,
        out Entity<WeapHarnComponent> harness)
    {
        harness = default;

        if (!TryGetMatchingHarness(user, supportKey, out var matchingHarness) ||
            !_powerCell.HasActivatableCharge(matchingHarness.Owner))
            return false;

        harness = matchingHarness;
        return true;
    }

    public bool HasSupportedWeaponInHandOrSuitStorage(EntityUid user, string supportKey)
    {
        foreach (var held in _hands.EnumerateHeld(user))
        {
            if (IsSupportedWeapon(held, supportKey))
                return true;
        }

        return _inventory.TryGetSlotEntity(user, SuitStorageSlot, out var suitStorage) &&
               IsSupportedWeapon(suitStorage.Value, supportKey);
    }

    private void OnGunRefreshModifiers(Entity<ReqWeapHarnComponent> ent, ref GunRefreshModifiersEvent args)
    {
        if (args.User == null || !HasActiveSupport(ent.Owner, args.User.Value, ent.Comp))
            return;

        args.MinAngle += ent.Comp.MinAngleBonus;
        args.MaxAngle += ent.Comp.MaxAngleBonus;
        args.AngleDecay += ent.Comp.AngleDecayBonus;
        args.AngleIncrease += ent.Comp.AngleIncreaseBonus;
    }

    private void OnAmmoShot(Entity<ReqWeapHarnComponent> ent, ref AmmoShotEvent args)
    {
        var user = Transform(ent.Owner).ParentUid;
        if (HasActiveSupport(ent.Owner, user, ent.Comp))
            return;

        // Triad: supported smart weapons keep firing without a harness, but lose homing support.
        foreach (var projectile in args.FiredProjectiles)
        {
            if (!TryComp(projectile, out HomingProjectileComponent? homing))
                continue;

            homing.Target = null;
            Dirty(projectile, homing);
        }
    }

    private void OnRefreshMovementSpeed(
        Entity<ReqWeapHarnComponent> ent,
        ref HeldRelayedEvent<RefreshMovementSpeedModifiersEvent> args)
    {
        var user = Transform(ent.Owner).ParentUid;
        if (TryGetPoweredHarness(user, ent.Comp.SupportKey, out _))
        {
            if (TryComp<WieldableComponent>(ent.Owner, out var wieldable) && wieldable.Wielded)
                args.Args.ModifySpeed(ent.Comp.PoweredWieldedWalkModifier, ent.Comp.PoweredWieldedSprintModifier);

            return;
        }

        if (TryGetHarnessWithCell(user, ent.Comp.SupportKey, out _))
            return;

        args.Args.ModifySpeed(ent.Comp.UnsupportedWalkModifier, ent.Comp.UnsupportedSprintModifier);
    }

    private void OnGunInventoryRefreshMovementSpeed(
        Entity<ReqWeapHarnComponent> ent,
        ref InventoryRelayedEvent<RefreshMovementSpeedModifiersEvent> args)
    {
        var user = Transform(ent.Owner).ParentUid;

        if (!_inventory.TryGetSlotEntity(user, SuitStorageSlot, out var suitStorage) ||
            suitStorage.Value != ent.Owner ||
            TryGetPoweredHarness(user, ent.Comp.SupportKey, out _) ||
            TryGetHarnessWithCell(user, ent.Comp.SupportKey, out _))
        {
            return;
        }

        args.Args.ModifySpeed(ent.Comp.UnsupportedWalkModifier, ent.Comp.UnsupportedSprintModifier);
    }

    private void OnHarnessRefreshMovementSpeed(
        Entity<WeapHarnComponent> ent,
        ref InventoryRelayedEvent<RefreshMovementSpeedModifiersEvent> args)
    {
        var user = Transform(ent.Owner).ParentUid;

        if (!HasSupportedWeaponInHandOrSuitStorage(user, ent.Comp.SupportKey) ||
            !TryGetHarnessWithCell(user, ent.Comp.SupportKey, out var harness) ||
            harness.Owner != ent.Owner ||
            _powerCell.HasActivatableCharge(ent.Owner))
            return;

        args.Args.ModifySpeed(ent.Comp.DrainedWalkModifier, ent.Comp.DrainedSprintModifier);
    }

    private void OnGunEquippedHand(Entity<ReqWeapHarnComponent> ent, ref GotEquippedHandEvent args)
    {
        RefreshGunAndUser(ent.Owner, args.User);
        RaiseLocalEvent(new WeapHarnGunEquipEvent(ent.Owner, args.User));
    }

    private void OnGunUnequippedHand(Entity<ReqWeapHarnComponent> ent, ref GotUnequippedHandEvent args)
    {
        RefreshGunAndUser(ent.Owner, args.User);
        RaiseLocalEvent(new WeapHarnGunUnEquipEvent(args.User));
    }

    private void OnGunEquippedInventory(Entity<ReqWeapHarnComponent> ent, ref GotEquippedEvent args)
    {
        RefreshHeldSupportedWeapons(args.Equipee);
    }

    private void OnGunUnequippedInventory(Entity<ReqWeapHarnComponent> ent, ref GotUnequippedEvent args)
    {
        RefreshHeldSupportedWeapons(args.Equipee);
        RaiseLocalEvent(new WeapHarnGunUnEquipInvEvent(ent.Owner, args.Equipee, args.Slot));
    }

    private void OnGunWielded(Entity<ReqWeapHarnComponent> ent, ref ItemWieldedEvent args)
    {
        RefreshGunAndUser(ent.Owner, args.User);
    }

    private void OnGunUnwielded(Entity<ReqWeapHarnComponent> ent, ref ItemUnwieldedEvent args)
    {
        RefreshGunAndUser(ent.Owner, args.User);
    }

    private void OnHarnessEquipped(Entity<WeapHarnComponent> ent, ref GotEquippedEvent args)
    {
        RefreshHeldSupportedWeapons(args.Equipee);
        RaiseLocalEvent(new WeapHarnEquipEvent(ent.Owner, args.Equipee, args.Slot));
    }

    private void OnHarnessUnequipped(Entity<WeapHarnComponent> ent, ref GotUnequippedEvent args)
    {
        RefreshHeldSupportedWeapons(args.Equipee);
        RaiseLocalEvent(new WeapHarnUnequipEvent(ent.Owner, args.Equipee, args.Slot));
    }

    private void OnHarnessPowerCellChanged(Entity<WeapHarnComponent> ent, ref PowerCellChangedEvent args)
    {
        var wearer = Transform(ent.Owner).ParentUid;
        RefreshHeldSupportedWeapons(wearer);
        RaiseLocalEvent(new WeapHarnPowerCellChangeEvent(ent.Owner));
    }

    public void RefreshHeldSupportedWeapons(EntityUid user)
    {
        _movement.RefreshMovementSpeedModifiers(user);

        foreach (var held in _hands.EnumerateHeld(user))
        {
            if (!TryComp<GunComponent>(held, out var gun) ||
                !HasComp<ReqWeapHarnComponent>(held))
                continue;

            _gun.RefreshModifiers((held, gun), user);
        }
    }

    private void RefreshGunAndUser(EntityUid gunUid, EntityUid user)
    {
        _movement.RefreshMovementSpeedModifiers(user);

        if (TryComp<GunComponent>(gunUid, out var gun))
            _gun.RefreshModifiers((gunUid, gun), user);
    }

    private bool TryGetHarnessWithCell(
        EntityUid user,
        string supportKey,
        out Entity<WeapHarnComponent> harness)
    {
        harness = default;

        if (!TryGetMatchingHarness(user, supportKey, out var matchingHarness) ||
            !TryComp<PowerCellSlotComponent>(matchingHarness.Owner, out var slot) ||
            !_itemSlots.TryGetSlot(matchingHarness.Owner, slot.CellSlotId, out var itemSlot) ||
            itemSlot.Item == null)
            return false;

        harness = matchingHarness;
        return true;
    }

    private bool TryGetMatchingHarness(
        EntityUid user,
        string supportKey,
        out Entity<WeapHarnComponent> harness)
    {
        harness = default;

        if (!_inventory.TryGetSlotEntity(user, BeltSlot, out var belt) ||
            !TryComp<WeapHarnComponent>(belt.Value, out var harnessComp) ||
            harnessComp.SupportKey != supportKey)
            return false;

        harness = (belt.Value, harnessComp);
        return true;
    }

    private bool IsSupportedWeapon(EntityUid weaponUid, string supportKey)
    {
        return TryComp<ReqWeapHarnComponent>(weaponUid, out var support) &&
               support.SupportKey == supportKey;
    }
}
