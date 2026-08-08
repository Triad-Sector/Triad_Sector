using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Containers;
using Robust.Shared.Utility;

namespace Content.Client.Weapons.Ranged.Systems;

public sealed partial class GunSystem
{
    protected override void InitializeRevolver()
    {
        base.InitializeRevolver();
        SubscribeLocalEvent<RevolverAmmoProviderComponent, AmmoCounterControlEvent>(OnRevolverCounter);
        SubscribeLocalEvent<RevolverAmmoProviderComponent, UpdateAmmoCounterEvent>(OnRevolverAmmoUpdate);
    }

    // Triad: the subscription moved to SharedGunSystem so the slot bookkeeping runs on the server too.
    // Subscribing here as well would be a duplicate subscription, since this is the same system instance.
    protected override void OnRevolverEntRemove(EntityUid uid, RevolverAmmoProviderComponent component, EntRemovedFromContainerMessage args)
    {
        base.OnRevolverEntRemove(uid, component, args);

        if (args.Container.ID != RevolverContainer)
            return;

        // See ChamberMagazineAmmoProvider
        if (!IsClientSide(args.Entity))
            return;

        QueueDel(args.Entity);
    }

    private void OnRevolverAmmoUpdate(EntityUid uid, RevolverAmmoProviderComponent component, UpdateAmmoCounterEvent args)
    {
        if (args.Control is not RevolverStatusControl control) return;
        control.Update(component.CurrentIndex, component.Chambers);
    }

    private void OnRevolverCounter(EntityUid uid, RevolverAmmoProviderComponent component, AmmoCounterControlEvent args)
    {
        args.Control = new RevolverStatusControl();
    }
}
