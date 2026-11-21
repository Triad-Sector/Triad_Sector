using Robust.Shared.Serialization;

namespace Content.Shared.Weapons.Ranged.Events;

/// <summary>
/// Raised whenever a muzzle flash client-side entity needs to be spawned.
/// </summary>
[Serializable, NetSerializable]
public sealed class MuzzleFlashEvent : EntityEventArgs
{
    public NetEntity Uid;
    public string Prototype;

    public Angle Angle;

    // Triad Start - Extra muzzle flash params
    public string ColorHex;
    public float MuzzleLightRadius;
    public float MuzzleLightEnergy;

    public MuzzleFlashEvent(NetEntity uid, string prototype, Angle angle, string colorHex, float lightRadius, float lightEnergy)
    {
        Uid = uid;
        Prototype = prototype;
        Angle = angle;
        ColorHex = colorHex;
        MuzzleLightRadius = lightRadius;
        MuzzleLightEnergy = lightEnergy;
    }
    // Triad End
}
