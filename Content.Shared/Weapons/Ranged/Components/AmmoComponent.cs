using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.Weapons.Ranged.Components;

/// <summary>
/// Allows the entity to be fired from a gun.
/// </summary>
[RegisterComponent, Virtual]
public partial class AmmoComponent : Component, IShootable
{
    // Muzzle flash stored on ammo because if we swap a gun to whatever we may want to override it.

    [DataField]
    public EntProtoId? MuzzleFlash = "MuzzleFlashEffect";

    // Triad start - extra muzzle flash light params
    [DataField]
    public Color MuzzleLightColor = Color.FromHex("#cc8e2b");

    [DataField]
    public float MuzzleLightRadius = 2f;

    [DataField]
    public float MuzzleLightEnergy = 5f;
    // Triad end
}

/// <summary>
/// Spawns another prototype to be shot instead of itself.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(fieldDeltas: true)]
public sealed partial class CartridgeAmmoComponent : AmmoComponent
{
    [ViewVariables(VVAccess.ReadWrite), DataField("proto", required: true)]
    public EntProtoId Prototype;

    [ViewVariables(VVAccess.ReadWrite), DataField]
    [AutoNetworkedField]
    public bool Spent;

    /// <summary>
    /// Caseless ammunition.
    /// </summary>
    [DataField]
    public bool DeleteOnSpawn;

    [DataField("soundEject")]
    public SoundSpecifier? EjectSound = new SoundCollectionSpecifier("CasingEject");

    // Mono start
    /// <summary>
    /// Whether to addcomp timeddespawn, and dictates the lifetime of spent boolets. Set to 0 to ignore.
    /// </summary>
    [DataField]
    public float AutoTimedDespawn = 30f;
    // Mono end
}
