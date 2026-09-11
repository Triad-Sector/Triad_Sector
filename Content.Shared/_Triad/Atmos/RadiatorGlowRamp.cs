using Robust.Shared.Maths;

namespace Content.Shared._Triad.Atmos;

/// <summary>
/// Triad: the blackbody appearance of each radiator thermal bucket.
/// </summary>
/// <remarks>
/// Shared because both sides need it and they must not disagree. The server
/// writes these as the resting values of the networked <c>PointLight</c>, which
/// is what makes a hot radiator keep glowing; the client reads the same table
/// to interpolate between buckets during a transition. If only the client set
/// the light, server state would revert it the moment the fade ended.
/// </remarks>
public static class RadiatorGlowRamp
{
    /// <summary>
    /// The hue a radiator fades out through, so dimming stays on colour instead
    /// of sliding toward black.
    /// </summary>
    public static readonly Color EmberHue = new(1.7f, 0.30f, 0.09f, 1f);

    /// <summary>
    /// Sprite tint, light energy and light radius for a bucket.
    /// </summary>
    /// <remarks>
    /// Tints are deliberately over-driven past 1.0. The glow layer multiplies
    /// its tint against the fin texture, whose lines are mid-grey (#545454,
    /// about 0.33), so an ordinary hex colour can never exceed a third of its
    /// nominal brightness and reads grey rather than incandescent. Colour is
    /// unclamped float and the renderer only rejects negative components, so
    /// scaling by roughly 1/0.33 is what puts hot metal on screen; divide by
    /// about 3 to read these as the intended on-screen colour.
    ///
    /// Alpha is 1 on every lit bucket so the incandescent layer fully covers the
    /// cold grey fin beneath it (same sprite state, so it occludes exactly).
    /// Brightness rides on tint magnitude; alpha is left to the fade.
    ///
    /// Light energy and radius climb far more gently than the tint does. The
    /// fin itself should go from dull red to white hot across this range, but
    /// the light it throws into the room should not: a white-hot run washed out
    /// a whole compartment at wider spreads. Energy spans 2.5x across the ramp
    /// and radius 1.6x, against the tint's full blackbody sweep, so a hot
    /// radiator reads hot by its own colour rather than by drowning everything
    /// around it. Tune the top of this range, not the bottom.
    /// </remarks>
    public static (Color Tint, float Energy, float Radius) Get(RadiatorThermalBucket bucket)
    {
        return bucket switch
        {
            RadiatorThermalBucket.DullRed => (new Color(1.7f, 0.30f, 0.09f, 1f), 0.6f, 2.0f),
            RadiatorThermalBucket.CherryRed => (new Color(2.6f, 0.55f, 0.15f, 1f), 0.8f, 2.3f),
            RadiatorThermalBucket.Orange => (new Color(3.0f, 1.40f, 0.30f, 1f), 1.0f, 2.6f),
            RadiatorThermalBucket.Yellow => (new Color(3.1f, 2.40f, 0.75f, 1f), 1.25f, 2.9f),
            RadiatorThermalBucket.White => (new Color(3.2f, 2.90f, 2.60f, 1f), 1.5f, 3.2f),
            _ => (EmberHue.WithAlpha(0f), 0f, 2.0f),
        };
    }

    /// <summary>
    /// Whether a bucket emits at all.
    /// </summary>
    public static bool IsLit(RadiatorThermalBucket bucket) => bucket >= RadiatorThermalBucket.DullRed;

    /// <summary>
    /// Scale an over-driven sprite tint back into 0-1 without shifting its hue,
    /// for use as a light colour. A light wants a real colour with its intensity
    /// carried by energy, not a tint built to beat a grey texture.
    /// </summary>
    public static Color ToLightColor(Color tint)
    {
        var peak = MathF.Max(tint.R, MathF.Max(tint.G, tint.B));
        if (peak <= 1f)
            return tint.WithAlpha(1f);

        return new Color(tint.R / peak, tint.G / peak, tint.B / peak, 1f);
    }
}
