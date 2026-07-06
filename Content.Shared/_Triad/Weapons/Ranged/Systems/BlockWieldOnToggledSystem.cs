using Content.Shared.Popups;
using Content.Shared.Item.ItemToggle;
using Content.Shared._Triad.Weapons.Ranged.Components;
using Content.Shared.Item.ItemToggle.Components;
using Content.Shared.Wieldable;
using Content.Shared.Wieldable.Components;

namespace Content.Shared._Triad.Weapons.Ranged.Systems;

public sealed class BlockWieldOnUntoggledSystem : EntitySystem
{
    [Dependency] private readonly ItemToggleSystem _itemToggle = default!;
    [Dependency] private readonly SharedWieldableSystem _wieldable = default!;

    public override void Initialize()
    {
        base.Initialize();
        
        SubscribeLocalEvent<BlockWieldOnUntoggledComponent, WieldAttemptEvent>(OnWeaponWield);
        SubscribeLocalEvent<BlockWieldOnUntoggledComponent, ItemToggledEvent>(OnItemToggled);
    }
    
    private void OnWeaponWield(Entity<BlockWieldOnUntoggledComponent> ent, ref WieldAttemptEvent args)
    {
        if (ent.Comp.BlockWieldOnUntoggled && !_itemToggle.IsActivated(ent.Owner))
        {
            args.Cancelled = true;
        }
    }

    private void OnItemToggled(Entity<BlockWieldOnUntoggledComponent> ent, ref ItemToggledEvent args)
    {
        // Only care when turning off
        if (args.Activated)
            return;

        if (!ent.Comp.BlockWieldOnUntoggled)
            return;

        if (TryComp<WieldableComponent>(ent, out var wieldable)
            && wieldable.Wielded
            && args.User is EntityUid user)
        {
            _wieldable.TryUnwield(ent.Owner, wieldable, user);
        }
    }
}
