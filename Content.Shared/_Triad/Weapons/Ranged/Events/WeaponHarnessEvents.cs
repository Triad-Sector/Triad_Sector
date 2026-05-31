namespace Content.Shared._Triad.Weapons.Ranged.Events;

public sealed class WeaponHarnessGunEquippedHandEvent(EntityUid gun, EntityUid user) : EntityEventArgs
{
    public readonly EntityUid Gun = gun;
    public readonly EntityUid User = user;
}

public sealed class WeaponHarnessGunUnequippedHandEvent(EntityUid user) : EntityEventArgs
{
    public readonly EntityUid User = user;
}

public sealed class WeaponHarnessGunUnequippedInventoryEvent(EntityUid gun, EntityUid user, string slot) : EntityEventArgs
{
    public readonly EntityUid Gun = gun;
    public readonly EntityUid User = user;
    public readonly string Slot = slot;
}

public sealed class WeaponHarnessEquippedEvent(EntityUid harness, EntityUid user, string slot) : EntityEventArgs
{
    public readonly EntityUid Harness = harness;
    public readonly EntityUid User = user;
    public readonly string Slot = slot;
}

public sealed class WeaponHarnessUnequippedEvent(EntityUid harness, EntityUid user, string slot) : EntityEventArgs
{
    public readonly EntityUid Harness = harness;
    public readonly EntityUid User = user;
    public readonly string Slot = slot;
}

public sealed class WeaponHarnessPowerCellChangedEvent(EntityUid harness) : EntityEventArgs
{
    public readonly EntityUid Harness = harness;
}
