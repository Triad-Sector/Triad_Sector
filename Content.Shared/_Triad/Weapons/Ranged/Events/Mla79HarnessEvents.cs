namespace Content.Shared._Triad.Weapons.Ranged.Events;

public sealed class Mla79HarnessGunEquippedHandEvent(EntityUid gun, EntityUid user) : EntityEventArgs
{
    public readonly EntityUid Gun = gun;
    public readonly EntityUid User = user;
}

public sealed class Mla79HarnessGunUnequippedHandEvent(EntityUid user) : EntityEventArgs
{
    public readonly EntityUid User = user;
}

public sealed class Mla79HarnessGunUnequippedInventoryEvent(EntityUid gun, EntityUid user, string slot) : EntityEventArgs
{
    public readonly EntityUid Gun = gun;
    public readonly EntityUid User = user;
    public readonly string Slot = slot;
}

public sealed class Mla79HarnessEquippedEvent(EntityUid harness, EntityUid user, string slot) : EntityEventArgs
{
    public readonly EntityUid Harness = harness;
    public readonly EntityUid User = user;
    public readonly string Slot = slot;
}

public sealed class Mla79HarnessUnequippedEvent(EntityUid harness, EntityUid user, string slot) : EntityEventArgs
{
    public readonly EntityUid Harness = harness;
    public readonly EntityUid User = user;
    public readonly string Slot = slot;
}

public sealed class Mla79HarnessPowerCellChangedEvent(EntityUid harness) : EntityEventArgs
{
    public readonly EntityUid Harness = harness;
}
