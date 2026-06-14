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
using System.Diagnostics.CodeAnalysis;

namespace Content.Shared._Triad.Weapons.Ranged.Systems;

/// Applies weapon harness handling and movement behavior.
/// Matching harnesses are found by <see cref="WeapHarnComponent.SupportKey"/> and must be equipped in their configured
/// <see cref="WeapHarnComponent.HarnessSlot"/>. Supported weapons count when held or stored in the harness-configured
/// <see cref="WeapHarnComponent.RetrievalSlot"/>.
public sealed class WeaponHarnessSystem : EntitySystem
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

        SubscribeLocalEvent<ReqWeapHarnComponent, GunRefreshModifiersEvent>(OnGunRefreshModifiers);
        SubscribeLocalEvent<ReqWeapHarnComponent, HeldRelayedEvent<RefreshMovementSpeedModifiersEvent>>(OnRefreshMovementSpeed);
        SubscribeLocalEvent<ReqWeapHarnComponent, InventoryRelayedEvent<RefreshMovementSpeedModifiersEvent>>(OnGunInventoryRefreshMovementSpeed);
        SubscribeLocalEvent<ReqWeapHarnComponent, GotEquippedHandEvent>(OnSupWeapEquipHand);
        SubscribeLocalEvent<ReqWeapHarnComponent, GotUnequippedHandEvent>(OnSupWeapUnequipHand);
        SubscribeLocalEvent<ReqWeapHarnComponent, GotEquippedEvent>(OnSupWeapEquipInv);
        SubscribeLocalEvent<ReqWeapHarnComponent, GotUnequippedEvent>(OnSupWeapUnequipInv);
        SubscribeLocalEvent<ReqWeapHarnComponent, ItemWieldedEvent>(OnSupWeapWield);
        SubscribeLocalEvent<ReqWeapHarnComponent, ItemUnwieldedEvent>(OnSupWeapUnwield);
        SubscribeLocalEvent<ReqWeapHarnComponent, AmmoShotEvent>(OnAmmoShot, after: [typeof(SmartGunSystem)]);

        SubscribeLocalEvent<WeapHarnComponent, InventoryRelayedEvent<RefreshMovementSpeedModifiersEvent>>(OnHarnRefreshMoveSpeed);
        SubscribeLocalEvent<WeapHarnComponent, GotEquippedEvent>(OnHarnEquip);
        SubscribeLocalEvent<WeapHarnComponent, GotUnequippedEvent>(OnHarnUnequip);
        SubscribeLocalEvent<WeapHarnComponent, PowerCellChangedEvent>(OnHarnPowerCellChange);
    }

    public bool HasActiveSup(
        EntityUid gun,
        EntityUid user,
        ReqWeapHarnComponent? support = null)
    {
        return TryGetActivePowHarn(gun, user, support, out _);
    }

    public bool TryGetActivePowHarn(
        EntityUid gun,
        EntityUid user,
        ReqWeapHarnComponent? support,
        out Entity<WeapHarnComponent> harness)
    {
        harness = default;

        return Resolve(gun, ref support, false) &&
               TryComp(gun, out WieldableComponent? wieldable) &&
               wieldable.Wielded &&
               TryGetPowHarnEntity(user, support.SupportKey, out harness);
    }

    public bool TryGetPowHarn(
        EntityUid user,
        string supportKey,
        out EntityUid harnessUid)
    {
        harnessUid = default;

        if (!TryGetPowHarnEntity(user, supportKey, out var harness))
            return false;

        harnessUid = harness.Owner;
        return true;
    }

    public bool TryGetPowHarnEntity(
        EntityUid user,
        string supportKey,
        out Entity<WeapHarnComponent> harness)
    {
        harness = default;

        if (!TryGetMatchHarn(user, supportKey, out var matchingHarness) ||
            !_powerCell.HasActivatableCharge(matchingHarness.Owner))
            return false;

        harness = matchingHarness;
        return true;
    }

    public bool HasSupWeapInHandOrRetrievalSlot(EntityUid user, string supportKey)
    {
        foreach (var held in _hands.EnumerateHeld(user))
        {
            if (IsSupWeap(held, supportKey))
                return true;
        }

        return TryGetMatchHarn(user, supportKey, out var harness) &&
               TryGetSlotEntity(user, harness.Comp.RetrievalSlot, out var retrievalSlot) &&
               IsSupWeap(retrievalSlot.Value, supportKey);
    }

    private void OnGunRefreshModifiers(Entity<ReqWeapHarnComponent> ent, ref GunRefreshModifiersEvent args)
    {
        if (args.User == null || !HasActiveSup(ent.Owner, args.User.Value, ent.Comp))
            return;

        args.MinAngle += ent.Comp.MinAngleBonus;
        args.MaxAngle += ent.Comp.MaxAngleBonus;
        args.AngleDecay += ent.Comp.AngleDecayBonus;
        args.AngleIncrease += ent.Comp.AngleIncreaseBonus;
    }

    private void OnAmmoShot(Entity<ReqWeapHarnComponent> ent, ref AmmoShotEvent args)
    {
        var user = Transform(ent.Owner).ParentUid;
        if (HasActiveSup(ent.Owner, user, ent.Comp))
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
        if (TryGetPowHarn(user, ent.Comp.SupportKey, out _))
        {
            if (TryComp<WieldableComponent>(ent.Owner, out var wieldable) && wieldable.Wielded)
                args.Args.ModifySpeed(ent.Comp.PoweredWieldedWalkModifier, ent.Comp.PoweredWieldedSprintModifier);

            return;
        }

        if (TryGetHarnWithCell(user, ent.Comp.SupportKey, out _))
            return;

        args.Args.ModifySpeed(ent.Comp.UnsupportedWalkModifier, ent.Comp.UnsupportedSprintModifier);
    }

    private void OnGunInventoryRefreshMovementSpeed(
        Entity<ReqWeapHarnComponent> ent,
        ref InventoryRelayedEvent<RefreshMovementSpeedModifiersEvent> args)
    {
        var user = Transform(ent.Owner).ParentUid;

        if (!TryGetMatchHarn(user, ent.Comp.SupportKey, out var matchingHarness) ||
            !TryGetSlotEntity(user, matchingHarness.Comp.RetrievalSlot, out var retrievalSlot) ||
            retrievalSlot.Value != ent.Owner ||
            TryGetPowHarn(user, ent.Comp.SupportKey, out _) ||
            TryGetHarnWithCell(user, ent.Comp.SupportKey, out _))
        {
            return;
        }

        args.Args.ModifySpeed(ent.Comp.UnsupportedWalkModifier, ent.Comp.UnsupportedSprintModifier);
    }

    private void OnHarnRefreshMoveSpeed(
        Entity<WeapHarnComponent> ent,
        ref InventoryRelayedEvent<RefreshMovementSpeedModifiersEvent> args)
    {
        var user = Transform(ent.Owner).ParentUid;

        if (!HasSupWeapInHandOrRetrievalSlot(user, ent.Comp.SupportKey) ||
            !TryGetHarnWithCell(user, ent.Comp.SupportKey, out var harness) ||
            harness.Owner != ent.Owner ||
            _powerCell.HasActivatableCharge(ent.Owner))
            return;

        args.Args.ModifySpeed(ent.Comp.DrainedWalkModifier, ent.Comp.DrainedSprintModifier);
    }

    private void OnSupWeapEquipHand(Entity<ReqWeapHarnComponent> ent, ref GotEquippedHandEvent args)
    {
        RefreshSupWeapAndUser(ent.Owner, args.User);
        RaiseLocalEvent(new WeapHarnGunEquipEvent(ent.Owner, args.User));
    }

    private void OnSupWeapUnequipHand(Entity<ReqWeapHarnComponent> ent, ref GotUnequippedHandEvent args)
    {
        RefreshSupWeapAndUser(ent.Owner, args.User);
        RaiseLocalEvent(new WeapHarnGunUnEquipEvent(args.User));
    }

    private void OnSupWeapEquipInv(Entity<ReqWeapHarnComponent> ent, ref GotEquippedEvent args)
    {
        RefreshHeldSupWeap(args.Equipee);
    }

    private void OnSupWeapUnequipInv(Entity<ReqWeapHarnComponent> ent, ref GotUnequippedEvent args)
    {
        RefreshHeldSupWeap(args.Equipee);
        RaiseLocalEvent(new WeapHarnGunUnEquipInvEvent(ent.Owner, args.Equipee, args.Slot));
    }

    private void OnSupWeapWield(Entity<ReqWeapHarnComponent> ent, ref ItemWieldedEvent args)
    {
        RefreshSupWeapAndUser(ent.Owner, args.User);
    }

    private void OnSupWeapUnwield(Entity<ReqWeapHarnComponent> ent, ref ItemUnwieldedEvent args)
    {
        RefreshSupWeapAndUser(ent.Owner, args.User);
    }

    private void OnHarnEquip(Entity<WeapHarnComponent> ent, ref GotEquippedEvent args)
    {
        RefreshHeldSupWeap(args.Equipee);
        RaiseLocalEvent(new WeapHarnEquipEvent(ent.Owner, args.Equipee, args.Slot));
    }

    private void OnHarnUnequip(Entity<WeapHarnComponent> ent, ref GotUnequippedEvent args)
    {
        RefreshHeldSupWeap(args.Equipee);
        RaiseLocalEvent(new WeapHarnUnequipEvent(ent.Owner, args.Equipee, args.Slot));
    }

    private void OnHarnPowerCellChange(Entity<WeapHarnComponent> ent, ref PowerCellChangedEvent args)
    {
        var wearer = Transform(ent.Owner).ParentUid;
        RefreshHeldSupWeap(wearer);
        RaiseLocalEvent(new WeapHarnPowerCellChangeEvent(ent.Owner));
    }

    public void RefreshHeldSupWeap(EntityUid user)
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

    private void RefreshSupWeapAndUser(EntityUid gunUid, EntityUid user)
    {
        _movement.RefreshMovementSpeedModifiers(user);

        if (TryComp<GunComponent>(gunUid, out var gun))
            _gun.RefreshModifiers((gunUid, gun), user);
    }

    private bool TryGetHarnWithCell(
        EntityUid user,
        string supportKey,
        out Entity<WeapHarnComponent> harness)
    {
        harness = default;

        if (!TryGetMatchHarn(user, supportKey, out var matchingHarness) ||
            !TryComp<PowerCellSlotComponent>(matchingHarness.Owner, out var slot) ||
            !_itemSlots.TryGetSlot(matchingHarness.Owner, slot.CellSlotId, out var itemSlot) ||
            itemSlot.Item == null)
            return false;

        harness = matchingHarness;
        return true;
    }

    private bool TryGetMatchHarn(
        EntityUid user,
        string supportKey,
        out Entity<WeapHarnComponent> harness)
    {
        harness = default;

        var enumerator = _inventory.GetSlotEnumerator(user);
        while (enumerator.NextItem(out var item, out var slot))
        {
            if (!TryComp<WeapHarnComponent>(item, out var harnessComp) ||
                harnessComp.SupportKey != supportKey ||
                (slot.SlotFlags & harnessComp.HarnessSlot) == 0)
                continue;

            harness = (item, harnessComp);
            return true;
        }

        return false;
    }

    public bool TryGetSlotEntity(EntityUid user, SlotFlags slotFlags, [NotNullWhen(true)] out EntityUid? slotEntity)
    {
        slotEntity = null;

        if (slotFlags == SlotFlags.NONE)
            return false;

        var enumerator = _inventory.GetSlotEnumerator(user, slotFlags);
        if (!enumerator.NextItem(out var item, out _))
            return false;

        slotEntity = item;
        return true;
    }

    public bool TryGetSlotName(EntityUid user, SlotFlags slotFlags, [NotNullWhen(true)] out string? slotName)
    {
        slotName = null;

        if (slotFlags == SlotFlags.NONE ||
            !_inventory.TryGetSlots(user, out var slots))
            return false;

        foreach (var slot in slots)
        {
            if ((slot.SlotFlags & slotFlags) == 0)
                continue;

            slotName = slot.Name;
            return true;
        }

        return false;
    }

    private bool IsSupWeap(EntityUid weaponUid, string supportKey)
    {
        return TryComp<ReqWeapHarnComponent>(weaponUid, out var support) &&
               support.SupportKey == supportKey;
    }
}
