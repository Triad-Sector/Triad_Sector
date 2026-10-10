using Content.Shared.Clothing.EntitySystems;
using Content.Shared.Item;
using Content.Shared.Item.ItemToggle.Components;

namespace Content.Shared._Triad.Item.ItemToggle;

public sealed partial class ItemToggleVisualsSystem : EntitySystem
{
    [Dependency] private ClothingSystem _clothing = default!;
    [Dependency] private SharedItemSystem _item = default!;

    [Dependency] private EntityQuery<ItemToggleComponent> _query;

    [SubscribeLocalEvent]
    private void OnToggled(Entity<ItemToggleVisualsComponent> ent, ref ItemToggledEvent args)
    {
        var prefix = args.Activated ? ent.Comp.OnPrefix : ent.Comp.OffPrefix;
        _item.SetHeldPrefix(ent, prefix);
        _clothing.SetEquippedPrefix(ent, prefix);
    }
}
