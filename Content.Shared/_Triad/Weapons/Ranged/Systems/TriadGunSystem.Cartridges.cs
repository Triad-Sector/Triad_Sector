using Content.Shared.Damage.Events;
using Content.Shared.Projectiles;
using Content.Shared._Triad.Weapons.Ranged.Components;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Content.Shared.Weapons.Ranged.Components;

namespace Content.Shared._Triad.Weapons.Ranged.Systems;

public sealed partial class TriadGunSystem : EntitySystem
{
    [Dependency] private IComponentFactory _componentFactory = default!;
    [Dependency] private IPrototypeManager _prototype = default!;
    [Dependency] private INetManager _net = default!;

    [SubscribeLocalEvent]
    private void OnCartridgeDamageExamine(Entity<ProjectileAmmoPenetrationExamineComponent> ent, ref DamageExamineEvent args)
    {
        // TODO - Predict
        if (_net.IsClient)
            return;

        if (!TryComp<CartridgeAmmoComponent>(ent.Owner, out var cartridge))
            return;

        var ap = GetProjectileArmorPenetration(cartridge.Prototype);

        var msg = args.Message;

        if (ap == 0f)
            return;

        var dirtyPercent = Math.Abs(100 * ap);
        var apPercent = Math.Round(dirtyPercent, 1, MidpointRounding.ToZero);

        msg.AddMarkupOrThrow(Loc.GetString("damage-examine-ap"));
        msg.PushNewline();

        if (ap > 0)
        {
            msg.AddMarkupOrThrow(Loc.GetString("damage-ap-value", ("amount", apPercent)));
        }
        else // Negative numbers
        {
            msg.AddMarkupOrThrow(Loc.GetString("damage-ap-value-less-effective", ("amount", apPercent)));
        }

        msg.PushNewline();
    }

    private float GetProjectileArmorPenetration(EntProtoId proto)
    {
        if (!_prototype.TryIndex(proto, out var entityProto))
            return 0f;

        if (!entityProto.TryComp<ProjectileComponent>(out var projectile, _componentFactory))
            return 0f;

        if (projectile.IgnoreResistances)
            return 1f;

        return Math.Min(projectile.ArmorPenetration, 1);
    }
}
