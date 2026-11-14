using System;

namespace Content.Shared._Triad.Drydock;

/// <summary>
/// The impound fee arithmetic, shared so the admin dialog's preview and the server's charge are one
/// computation rather than two that agree by luck. A share of the appraisal, rounded down, so the
/// credits owed can never exceed what the hull is worth; the share is clamped on every way in.
/// </summary>
public static class DrydockImpoundFee
{
    public const int MaxPercent = 100;

    public static int ClampPercent(int percent) => Math.Clamp(percent, 0, MaxPercent);

    public static int Against(int appraisal, int percent)
        => (int)((long)Math.Max(0, appraisal) * ClampPercent(percent) / MaxPercent);
}
