using Content.Shared.Damage;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom; // Triad - for TimeOffsetSerializer

namespace Content.Server._Mono.Weapons.Melee;

/// <summary>
/// Toggles the weapon for <see cref="ActiveTime"/> amount of time. After this time passes or melee hit is performed, <see cref="Cooldown"/> is activated
/// Used in pair with ItemToggleMeleeWeaponComponent
///  </summary>
[RegisterComponent]
public sealed partial class WeaponMeleeChargeComponent : Component
{
    [DataField]
    public float ActiveTime = 1f;

    [DataField]
    public float Cooldown = 1f;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan CooldownEndTime = TimeSpan.Zero; // Triad - TimeOffsetSerializer: absolute game time, re-based on load so a saved ship does not carry the previous server clock

    [DataField]
    public TimeSpan ActiveEndTime = TimeSpan.Zero;

    [DataField]
    public DamageSpecifier CooldownDamagePenalty =  new DamageSpecifier();
}
