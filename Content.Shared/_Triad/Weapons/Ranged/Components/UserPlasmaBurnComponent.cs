using Content.Shared.Damage;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._Triad.Weapons.Ranged.Components;

/// <summary>
///     The component applied to a mob when it is being burned by plasma weapons for its damage over time effect.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(fieldDeltas: true), AutoGenerateComponentPause]
public sealed partial class UserPlasmaBurnComponent : Component
{
    [DataField, AutoNetworkedField]
    public DamageSpecifier Damage = new();

    [DataField, AutoNetworkedField]
    public float ArmorPenetration = 0;

    [DataField, AutoNetworkedField]
    public Color Color;

    [DataField, AutoNetworkedField]
    public int MaxStacks = 15;

    [DataField, AutoNetworkedField]
    public int Stacks = 0;

    [DataField, AutoNetworkedField]
    public float SaturationIncreasePerStack = 0.065f;

    [DataField, AutoNetworkedField]
    public float BrightnessIncreasePerStack = 0.07f;

    [DataField, AutoNetworkedField]
    public TimeSpan Duration = TimeSpan.FromSeconds(5);

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan ExpiresAt;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan NextDamageAt;

    [DataField, AutoNetworkedField]
    public TimeSpan DamageInterval = TimeSpan.FromSeconds(1.5);

    [DataField, AutoNetworkedField]
    public TimeSpan ParalyzeOnResistDuration = TimeSpan.FromSeconds(3);

    [DataField, AutoNetworkedField]
    public SoundSpecifier BurnSound = new SoundCollectionSpecifier("AcidSizzle")
    {
        Params = AudioParams.Default.WithVariation(0.1f).WithVolume(-5),
    };

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan NextSoundAt;

    [DataField, AutoNetworkedField]
    public TimeSpan SoundCooldown = TimeSpan.FromSeconds(5);
}

[Serializable, NetSerializable]
public enum PlasmaBurnVisuals
{
    Burning,
}

[Serializable, NetSerializable]
public enum PlasmaBurningState
{
    None,
    Burning
}
