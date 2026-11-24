using Content.Shared.Whitelist;
using Content.Shared.Projectiles;
using Content.Shared._Triad.Weapons.Ranged.Components;

namespace Content.Shared._Triad.Weapons.Ranged.Systems;

public sealed partial class TriadGunSystem : EntitySystem
{
    [Dependency] private EntityWhitelistSystem _whitelist = default!;

    [SubscribeLocalEvent]
    private void OnAddComponentsOnHit(Entity<AddComponentsOnHitComponent> ent, ref ProjectileHitEvent args)
    {
        var target = args.Target;

        if (!_whitelist.CheckBoth(target, ent.Comp.Blacklist, ent.Comp.Whitelist))
            return;

        EntityManager.AddComponents(args.Target, ent.Comp.AddComponents, ent.Comp.OverrideExisting);
    }
}
