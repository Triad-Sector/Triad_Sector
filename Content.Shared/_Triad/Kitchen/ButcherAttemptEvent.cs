namespace Content.Shared._Triad.Kitchen;

/// <summary>
/// Butcherable attempt event. Ran on the user.
/// </summary>
[ByRefEvent]
public sealed class ButcherAttemptEvent(EntityUid user, EntityUid knife, EntityUid target)
    : CancellableEntityEventArgs
{
    public readonly EntityUid User = user;
    public readonly EntityUid Knife = knife;
    public readonly EntityUid Target = target;

    /// <summary>
    /// Pop-up that gets shown to users explaining why the attempt was cancelled.
    /// </summary>
    public string? Popup { get; set; }
}
