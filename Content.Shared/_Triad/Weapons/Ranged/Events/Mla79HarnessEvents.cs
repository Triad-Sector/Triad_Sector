namespace Content.Shared._Triad.Weapons.Ranged.Events;

public sealed class Mla79HarnessGunEquippedHandEvent : EntityEventArgs
{
    public readonly EntityUid Gun;
    public readonly EntityUid User;

    public Mla79HarnessGunEquippedHandEvent(EntityUid gun, EntityUid user)
    {
        Gun = gun;
        User = user;
    }
}

public sealed class Mla79HarnessGunUnequippedHandEvent : EntityEventArgs
{
    public readonly EntityUid User;

    public Mla79HarnessGunUnequippedHandEvent(EntityUid user)
    {
        User = user;
    }
}

public sealed class Mla79HarnessGunUnequippedInventoryEvent : EntityEventArgs
{
    public readonly EntityUid Gun;
    public readonly EntityUid User;
    public readonly string Slot;

    public Mla79HarnessGunUnequippedInventoryEvent(EntityUid gun, EntityUid user, string slot)
    {
        Gun = gun;
        User = user;
        Slot = slot;
    }
}

public sealed class Mla79HarnessEquippedEvent : EntityEventArgs
{
    public readonly EntityUid Harness;
    public readonly EntityUid User;
    public readonly string Slot;

    public Mla79HarnessEquippedEvent(EntityUid harness, EntityUid user, string slot)
    {
        Harness = harness;
        User = user;
        Slot = slot;
    }
}

public sealed class Mla79HarnessUnequippedEvent : EntityEventArgs
{
    public readonly EntityUid Harness;
    public readonly EntityUid User;
    public readonly string Slot;

    public Mla79HarnessUnequippedEvent(EntityUid harness, EntityUid user, string slot)
    {
        Harness = harness;
        User = user;
        Slot = slot;
    }
}

public sealed class Mla79HarnessPowerCellChangedEvent : EntityEventArgs
{
    public readonly EntityUid Harness;

    public Mla79HarnessPowerCellChangedEvent(EntityUid harness)
    {
        Harness = harness;
    }
}
