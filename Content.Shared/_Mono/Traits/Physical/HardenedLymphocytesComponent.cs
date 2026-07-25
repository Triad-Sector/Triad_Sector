// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Damage.Prototypes;
using Robust.Shared.GameStates;

namespace Content.Shared._Mono.Traits.Physical;

/// <summary>
///     Grants flat damage protection for the HardenedLymphocytes trait.
///     Mirrors <see cref="Content.Shared.Damage.Components.DamageProtectionBuffComponent"/>, but as a
///     distinct component so it can coexist with DermalArmor's DamageProtectionBuff. Two traits adding
///     the same component type would otherwise silently drop one (engine AddComponents skips duplicates).
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class HardenedLymphocytesComponent : Component
{
    /// <summary>
    ///     The damage modifiers applied when the entity takes damage.
    /// </summary>
    [DataField]
    public Dictionary<string, DamageModifierSetPrototype> Modifiers = new();
}
