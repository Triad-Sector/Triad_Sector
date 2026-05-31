using Content.Shared._Goobstation.Wizard.Projectiles;
// Triad: MLA-79 harness support gates smartgun homing for the Triad smartgun harness system.
using Content.Shared._Triad.Weapons.Ranged.Components;
using Content.Shared._Triad.Weapons.Ranged.Systems;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Wieldable.Components;

namespace Content.Shared._Goobstation.Weapons.SmartGun;

public sealed class SmartGunSystem : EntitySystem
{
    // Triad: Shared helper checks whether an MLA-79 user has a powered harness equipped.
    [Dependency] private readonly Mla79HarnessSupportSystem _mla79Harness = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SmartGunComponent, AmmoShotEvent>(OnShot);
    }

    private void OnShot(Entity<SmartGunComponent> ent, ref AmmoShotEvent args)
    {
        var (uid, comp) = ent;

        if (!TryComp(uid, out GunComponent? gun) || gun.Target == null)
            return;

        if (comp.RequiresWield && !(TryComp(uid, out WieldableComponent? wieldable) && wieldable.Wielded))
            return;

        var user = Transform(uid).ParentUid;

        // Triad: MLA-79 keeps firing without the harness, but loses smartgun homing support.
        if (TryComp(uid, out RequiresMla79HarnessSupportComponent? harnessSupport) &&
            !_mla79Harness.HasActiveSupport(uid, user, harnessSupport))
        {
            return;
        }

        if (gun.Target == user)
            return;

        foreach (var projectile in args.FiredProjectiles)
        {
            if (!TryComp(projectile, out HomingProjectileComponent? homing))
                continue;

            homing.Target = gun.Target.Value;
            Dirty(projectile, homing);
        }
    }
}
