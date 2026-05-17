using Content.Shared.Inventory.Events;
using Content.Shared.Clothing.Components;
using Robust.Shared.Serialization.Manager;

namespace Content.Shared.Eye.Blinding.Components;

public sealed partial class ClothesVisionSystem : EntitySystem
{
    [Dependency] private readonly ISerializationManager _serialization = default!;
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ClothesSLNightVisionComponent, GotEquippedEvent>(OnEquipped);
        SubscribeLocalEvent<ClothesSLNightVisionComponent, GotUnequippedEvent>(OnUnequipped);
    }

    private void OnEquipped(EntityUid uid, ClothesSLNightVisionComponent component, GotEquippedEvent args)
    {
        if (!TryComp<ClothingComponent>(uid, out var clothing)
            || !clothing.Slots.HasFlag(args.SlotFlags))
            return;

        if (!HasComp<SLNightVisionComponent>(args.Equipee))
        {
            var nightvision = EnsureComp<SLNightVisionComponent>(args.Equipee);
            nightvision.Clothes = true;
        }
    }

    private void OnUnequipped(EntityUid uid, ClothesSLNightVisionComponent component, GotUnequippedEvent args)
    {
        if (TryComp<SLNightVisionComponent>(args.Equipee, out var nightvision) && !nightvision.Clothes)
        {
            nightvision.Clothes = false;
            return;
        }

        RemComp<SLNightVisionComponent>(args.Equipee);
    }
}
