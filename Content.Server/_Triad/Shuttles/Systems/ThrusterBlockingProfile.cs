using System.Numerics;

namespace Content.Server._Triad.Shuttles.Systems;

public enum ThrusterBlockingProfile
{
    AlwaysBlocked = 0,
    NeverBlocked = 1,
    Small = 2,
    Large = 3,
}

public sealed class ThrusterBlockingProfileData(List<ThrusterBlockingRayData> rays, int requiredQuality)
{
    public List<ThrusterBlockingRayData> Rays = rays;
    public int RequiredQuality = requiredQuality;
}

public sealed class ThrusterBlockingRayData(double angle, Vector2 offset, int quality = 1)
{
    /// <summary>
    /// The direction/angle the raycast goes, relative to the entity's rotation.
    ///
    /// In radians, clockwise from the direction the thruster is facing
    /// </summary>
    private Angle Angle
    {
        get
        {
            var t = (RelativeAngle + 180.0) % 360.0d;
            if (t < 0)
                t += 360.0d;

            return Robust.Shared.Maths.Angle.FromDegrees(t);
        }
    }

    public Angle AngleInRadians() { return Angle; }

    /// <summary>
    /// The direction/angle the raycast goes, relative to the entity's rotation.
    ///
    /// In degrees, clockwise from the direction the thruster is facing
    /// </summary>
    public double RelativeAngle = angle;

    /// <summary>
    /// How much is the ray offset from the 'origin' of the entity's position?
    /// Useful for large thrusters where their 'origin' is on the tile they rotate by.
    /// </summary>
    public float OffsetX = offset.X;

    /// <summary>
    /// How much is the ray offset from the 'origin' of the entity's position?
    /// Useful for large thrusters where their 'origin' is on the tile they rotate by.
    /// +y is an offset in the direction of the fire
    /// </summary>
    public float OffsetY = offset.Y;

    /// <summary>
    /// How much is this ray worth if it can see space
    /// </summary>
    public int Quality = quality;
}
