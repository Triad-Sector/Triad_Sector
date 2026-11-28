using Content.Server.Chat.Systems;
using Content.Server.RoundEnd;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Triad.RoundEnd;

/// <summary>
/// The round-end warning schedule over the round-end countdown. <see cref="RoundEndSystem"/> announces
/// the call itself; this announces each mark after it, and raises <see cref="RoundEndWarningEvent"/>
/// at the call and at every mark so other systems can warn on the same clock without owning one.
///
/// <para>The countdown is read from <see cref="RoundEndSystem.ExpectedCountdownEnd"/> every tick. A
/// countdown this system has not seen yet is a call. A mark fires once, the first tick the time left
/// is at or under it, and only if it is strictly shorter than the countdown's whole length, so a
/// ten-minute call from a comms console reaches the five-minute mark and never the fifteen. When the
/// countdown clears, by a recall or the round ending, everything re-arms for the next call.</para>
/// </summary>
public sealed class RoundEndWarningSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ChatSystem _chatSystem = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private RoundEndSystem _roundEnd = default!;

    private static readonly SoundSpecifier MarkSound = new SoundPathSpecifier("/Audio/Announcements/attention.ogg");

    private static readonly RoundEndWarningMark[] DefaultMarks =
    {
        new(TimeSpan.FromMinutes(15), "round-end-system-warning-15"),
        new(TimeSpan.FromMinutes(5), "round-end-system-warning-5"),
    };

    /// <summary>
    /// The marks, longest first. Test-only setter: production always runs the fixed 15 and 5 minute
    /// marks, and a fixture swaps in marks that fit a countdown it can afford to wait out.
    /// </summary>
    internal IReadOnlyList<RoundEndWarningMark> Marks { get; set; } = DefaultMarks;

    /// <summary>The countdown end this system last raised the call for, or null when disarmed.</summary>
    private TimeSpan? _trackedEnd;

    /// <summary>The marks already fired for <see cref="_trackedEnd"/>, by their time left.</summary>
    private readonly HashSet<TimeSpan> _fired = new();

    public override void Initialize()
    {
        base.Initialize();
        // A recall and a new call inside one tick would leave the same end in place for Update to
        // read, so a cleared countdown also disarms here, the moment RoundEndSystem reports it.
        SubscribeLocalEvent<RoundEndSystemChangedEvent>(OnRoundEndSystemChanged);
    }

    private void OnRoundEndSystemChanged(RoundEndSystemChangedEvent ev)
    {
        if (_roundEnd.ExpectedCountdownEnd == null)
            Disarm();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_roundEnd.ExpectedCountdownEnd is not { } end)
        {
            Disarm();
            return;
        }

        var remaining = end - _timing.CurTime;

        if (_trackedEnd != end)
        {
            Disarm();
            _trackedEnd = end;
            var call = new RoundEndWarningEvent(remaining, true);
            RaiseLocalEvent(ref call);
        }

        if (_roundEnd.ExpectedShuttleLength is not { } length)
            return;

        foreach (var mark in Marks)
        {
            if (mark.Remaining >= length || remaining > mark.Remaining || !_fired.Add(mark.Remaining))
                continue;

            Announce(mark, remaining);
            var ev = new RoundEndWarningEvent(remaining, false);
            RaiseLocalEvent(ref ev);
        }
    }

    private void Announce(RoundEndWarningMark mark, TimeSpan remaining)
    {
        var minutes = (int)Math.Round(remaining.TotalMinutes);

        _chatSystem.DispatchGlobalAnnouncement(Loc.GetString(mark.Announcement, ("minutes", minutes)),
            Loc.GetString("round-end-system-shuttle-sender-announcement"),
            false,
            null,
            Color.Gold);

        _audio.PlayGlobal(MarkSound, Filter.Broadcast(), true);
    }

    private void Disarm()
    {
        _trackedEnd = null;
        _fired.Clear();
    }
}

/// <summary>One warning mark: how much countdown is left when it fires, and what it announces.</summary>
internal readonly record struct RoundEndWarningMark(TimeSpan Remaining, LocId Announcement);
