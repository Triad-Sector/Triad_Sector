namespace Content.Server._Triad.RoundEnd;

/// <summary>
/// Raised broadcast by <see cref="RoundEndWarningSystem"/> once when a round-end countdown starts and
/// once at each warning mark that countdown reaches. A recall followed by a new call raises it all
/// over again.
/// </summary>
/// <param name="Remaining">Time left on the countdown when the event was raised.</param>
/// <param name="IsCall">True for the raise at the call itself, false for a mark.</param>
[ByRefEvent]
public readonly record struct RoundEndWarningEvent(TimeSpan Remaining, bool IsCall);
