// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Mono.Traits.Physical;
using Content.Shared.Damage;

namespace Content.Server._Mono.Traits.Physical;

/// <summary>
///     Applies HardenedLymphocytes damage protection. A dedicated mirror of
///     <c>DamageProtectionBuffSystem</c> keyed to its own component so it stacks with
///     DermalArmor's DamageProtectionBuff instead of colliding with it.
/// </summary>
public sealed class HardenedLymphocytesSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<HardenedLymphocytesComponent, DamageModifyEvent>(OnDamageModify);
    }

    private void OnDamageModify(EntityUid uid, HardenedLymphocytesComponent component, DamageModifyEvent args)
    {
        foreach (var modifier in component.Modifiers.Values)
            args.Damage = DamageSpecifier.ApplyModifierSet(args.Damage,
                DamageSpecifier.PenetrateArmor(modifier, args.ArmorPenetration));
    }
}
