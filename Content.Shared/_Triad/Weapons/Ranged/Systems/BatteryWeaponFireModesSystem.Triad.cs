using Content.Shared.Weapons.Ranged.Components;

namespace Content.Shared.Weapons.Ranged.Systems;

public sealed partial class BatteryWeaponFireModesSystem : EntitySystem
{
    [SubscribeLocalEvent]
    private void OnMapInit(Entity<BatteryWeaponFireModesComponent> ent, ref MapInitEvent args)
    {
        if (ent.Comp.CurrentFireMode == default)
            return;

        TrySetFireMode(ent.Owner, ent.Comp, ent.Comp.CurrentFireMode);
    }
}
