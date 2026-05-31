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

        SubscribeLocalEvent<RequiresWeaponHarnessComponent, GunRefreshModifiersEvent>(OnGunRefreshModifiers);
        SubscribeLocalEvent<RequiresWeaponHarnessComponent, HeldRelayedEvent<RefreshMovementSpeedModifiersEvent>>(OnRefreshMovementSpeed);
        SubscribeLocalEvent<RequiresWeaponHarnessComponent, InventoryRelayedEvent<RefreshMovementSpeedModifiersEvent>>(OnGunInventoryRefreshMovementSpeed);
        SubscribeLocalEvent<RequiresWeaponHarnessComponent, GotEquippedHandEvent>(OnGunEquippedHand);
        SubscribeLocalEvent<RequiresWeaponHarnessComponent, GotUnequippedHandEvent>(OnGunUnequippedHand);
        SubscribeLocalEvent<RequiresWeaponHarnessComponent, GotEquippedEvent>(OnGunEquippedInventory);
        SubscribeLocalEvent<RequiresWeaponHarnessComponent, GotUnequippedEvent>(OnGunUnequippedInventory);
        SubscribeLocalEvent<RequiresWeaponHarnessComponent, ItemWieldedEvent>(OnGunWielded);
        SubscribeLocalEvent<RequiresWeaponHarnessComponent, ItemUnwieldedEvent>(OnGunUnwielded);
        SubscribeLocalEvent<RequiresWeaponHarnessComponent, AmmoShotEvent>(OnAmmoShot, after: [typeof(SmartGunSystem)]);

        SubscribeLocalEvent<WeaponHarnessComponent, InventoryRelayedEvent<RefreshMovementSpeedModifiersEvent>>(OnHarnessRefreshMovementSpeed);
        SubscribeLocalEvent<WeaponHarnessComponent, GotEquippedEvent>(OnHarnessEquipped);
        SubscribeLocalEvent<WeaponHarnessComponent, GotUnequippedEvent>(OnHarnessUnequipped);
        SubscribeLocalEvent<WeaponHarnessComponent, PowerCellChangedEvent>(OnHarnessPowerCellChanged);
    }

    public bool HasActiveSupport(
        EntityUid gun,
        EntityUid user,
        RequiresWeaponHarnessComponent? support = null)
    {
        return Resolve(gun, ref support, false) &&
               TryComp(gun, out WieldableComponent? wieldable) &&
               wieldable.Wielded &&
               TryGetPoweredHarness(user, support.SupportKey, out _);
    }

    public bool TryGetPoweredHarness(
        EntityUid user,
        string supportKey,
        out EntityUid harnessUid)
    {
        harnessUid = default;

        if (!_inventory.TryGetSlotEntity(user, BeltSlot, out var belt) ||
            !IsMatchingHarness(belt.Value, supportKey) ||
            !_powerCell.HasActivatableCharge(belt.Value))
            return false;

        harnessUid = belt.Value;
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

    private void OnGunRefreshModifiers(Entity<RequiresWeaponHarnessComponent> ent, ref GunRefreshModifiersEvent args)
    {
        if (args.User == null || !HasActiveSupport(ent.Owner, args.User.Value, ent.Comp))
            return;

        args.MinAngle += ent.Comp.MinAngleBonus;
        args.MaxAngle += ent.Comp.MaxAngleBonus;
        args.AngleDecay += ent.Comp.AngleDecayBonus;
        args.AngleIncrease += ent.Comp.AngleIncreaseBonus;
    }

    private void OnAmmoShot(Entity<RequiresWeaponHarnessComponent> ent, ref AmmoShotEvent args)
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
        Entity<RequiresWeaponHarnessComponent> ent,
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
        Entity<RequiresWeaponHarnessComponent> ent,
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
        Entity<WeaponHarnessComponent> ent,
        ref InventoryRelayedEvent<RefreshMovementSpeedModifiersEvent> args)
    {
        var user = Transform(ent.Owner).ParentUid;

        if (!HasSupportedWeaponInHandOrSuitStorage(user, ent.Comp.SupportKey) ||
            TryGetPoweredHarness(user, ent.Comp.SupportKey, out _))
            return;

        if (!TryGetHarnessWithCell(user, ent.Comp.SupportKey, out var harnessUid) ||
            harnessUid != ent.Owner)
            return;

        args.Args.ModifySpeed(ent.Comp.DrainedWalkModifier, ent.Comp.DrainedSprintModifier);
    }

    private void OnGunEquippedHand(Entity<RequiresWeaponHarnessComponent> ent, ref GotEquippedHandEvent args)
    {
        RefreshGunAndUser(ent.Owner, args.User);
        RaiseLocalEvent(new WeaponHarnessGunEquippedHandEvent(ent.Owner, args.User));
    }

    private void OnGunUnequippedHand(Entity<RequiresWeaponHarnessComponent> ent, ref GotUnequippedHandEvent args)
    {
        RefreshGunAndUser(ent.Owner, args.User);
        RaiseLocalEvent(new WeaponHarnessGunUnequippedHandEvent(args.User));
    }

    private void OnGunEquippedInventory(Entity<RequiresWeaponHarnessComponent> ent, ref GotEquippedEvent args)
    {
        RefreshHeldSupportedWeapons(args.Equipee);
    }

    private void OnGunUnequippedInventory(Entity<RequiresWeaponHarnessComponent> ent, ref GotUnequippedEvent args)
    {
        RefreshHeldSupportedWeapons(args.Equipee);
        RaiseLocalEvent(new WeaponHarnessGunUnequippedInventoryEvent(ent.Owner, args.Equipee, args.Slot));
    }

    private void OnGunWielded(Entity<RequiresWeaponHarnessComponent> ent, ref ItemWieldedEvent args)
    {
        RefreshGunAndUser(ent.Owner, args.User);
    }

    private void OnGunUnwielded(Entity<RequiresWeaponHarnessComponent> ent, ref ItemUnwieldedEvent args)
    {
        RefreshGunAndUser(ent.Owner, args.User);
    }

    private void OnHarnessEquipped(Entity<WeaponHarnessComponent> ent, ref GotEquippedEvent args)
    {
        RefreshHeldSupportedWeapons(args.Equipee);
        RaiseLocalEvent(new WeaponHarnessEquippedEvent(ent.Owner, args.Equipee, args.Slot));
    }

    private void OnHarnessUnequipped(Entity<WeaponHarnessComponent> ent, ref GotUnequippedEvent args)
    {
        RefreshHeldSupportedWeapons(args.Equipee);
        RaiseLocalEvent(new WeaponHarnessUnequippedEvent(ent.Owner, args.Equipee, args.Slot));
    }

    private void OnHarnessPowerCellChanged(Entity<WeaponHarnessComponent> ent, ref PowerCellChangedEvent args)
    {
        var wearer = Transform(ent.Owner).ParentUid;
        RefreshHeldSupportedWeapons(wearer);
        RaiseLocalEvent(new WeaponHarnessPowerCellChangedEvent(ent.Owner));
    }

    public void RefreshHeldSupportedWeapons(EntityUid user)
    {
        _movement.RefreshMovementSpeedModifiers(user);

        foreach (var held in _hands.EnumerateHeld(user))
        {
            if (!TryComp<GunComponent>(held, out var gun) ||
                !HasComp<RequiresWeaponHarnessComponent>(held))
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

    private bool TryGetHarnessWithCell(EntityUid user, string supportKey, out EntityUid harnessUid)
    {
        harnessUid = default;

        if (!_inventory.TryGetSlotEntity(user, BeltSlot, out var belt) ||
            !IsMatchingHarness(belt.Value, supportKey) ||
            !TryComp<PowerCellSlotComponent>(belt.Value, out var slot) ||
            !_itemSlots.TryGetSlot(belt.Value, slot.CellSlotId, out var itemSlot) ||
            itemSlot.Item == null)
            return false;

        harnessUid = belt.Value;
        return true;
    }

    private bool IsMatchingHarness(EntityUid harnessUid, string supportKey)
    {
        return TryComp<WeaponHarnessComponent>(harnessUid, out var harness) &&
               harness.SupportKey == supportKey;
    }

    private bool IsSupportedWeapon(EntityUid weaponUid, string supportKey)
    {
        return TryComp<RequiresWeaponHarnessComponent>(weaponUid, out var support) &&
               support.SupportKey == supportKey;
    }
}
