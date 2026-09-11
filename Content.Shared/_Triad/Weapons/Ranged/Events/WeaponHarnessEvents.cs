namespace Content.Shared._Triad.Weapons.Ranged.Events;

public sealed class WeapHarnGunEquipEvent(EntityUid gun, EntityUid user) : EntityEventArgs
{
    public readonly EntityUid Gun = gun;
    public readonly EntityUid User = user;
}

public sealed class WeapHarnGunUnEquipEvent(EntityUid user) : EntityEventArgs
{
    public readonly EntityUid User = user;
}

public sealed class WeapHarnGunUnEquipInvEvent(EntityUid gun, EntityUid user, string slot) : EntityEventArgs
{
    public readonly EntityUid Gun = gun;
    public readonly EntityUid User = user;
    public readonly string Slot = slot;
}

public sealed class WeapHarnEquipEvent(EntityUid harness, EntityUid user, string slot) : EntityEventArgs
{
    public readonly EntityUid Harness = harness;
    public readonly EntityUid User = user;
    public readonly string Slot = slot;
}

public sealed class WeapHarnUnequipEvent(EntityUid harness, EntityUid user, string slot) : EntityEventArgs
{
    public readonly EntityUid Harness = harness;
    public readonly EntityUid User = user;
    public readonly string Slot = slot;
}

public sealed class WeapHarnPowerCellChangeEvent(EntityUid harness) : EntityEventArgs
{
    public readonly EntityUid Harness = harness;
}
