using System.Collections.Generic;
using Content.Server._Triad.RoundEnd;
using Content.Server.RoundEnd;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._Triad;

/// <summary>
/// The round-end warning schedule: one event at the call, one per mark shorter than the countdown,
/// none twice, and all of it again after a recall and a new call.
/// </summary>
[TestFixture]
[TestOf(typeof(RoundEndWarningSystem))]
public sealed class RoundEndWarningTest
{
    private static readonly TimeSpan Countdown = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Longest first, as the system requires. The first equals the countdown's length and must never
    /// fire; the other two sit inside it and leave two seconds to recall before the round would end.
    /// </summary>
    private static readonly RoundEndWarningMark[] TestMarks =
    {
        new(TimeSpan.FromSeconds(4), "round-end-system-warning-15"),
        new(TimeSpan.FromSeconds(3), "round-end-system-warning-15"),
        new(TimeSpan.FromSeconds(2), "round-end-system-warning-5"),
    };

    private sealed class RoundEndWarningRecorderSystem : EntitySystem
    {
        public readonly List<RoundEndWarningEvent> Seen = new();

        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<RoundEndWarningEvent>(OnWarning);
        }

        private void OnWarning(ref RoundEndWarningEvent ev)
        {
            Seen.Add(ev);
        }
    }

    [Test]
    public async Task FiresOncePerMarkAndRearmsAfterRecall()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            DummyTicker = false,
            Connected = true,
            Dirty = true,
        });

        var server = pair.Server;
        var roundEnd = server.System<RoundEndSystem>();
        var warnings = server.System<RoundEndWarningSystem>();
        var recorder = server.System<RoundEndWarningRecorderSystem>();

        var originalCountdown = TimeSpan.Zero;
        IReadOnlyList<RoundEndWarningMark> originalMarks = null!;

        await server.WaitAssertion(() =>
        {
            originalCountdown = roundEnd.DefaultCountdownDuration;
            originalMarks = warnings.Marks;

            roundEnd.DefaultCountdownDuration = Countdown;
            warnings.Marks = TestMarks;
            recorder.Seen.Clear();

            roundEnd.RequestRoundEnd(null, false);
            Assert.That(roundEnd.ExpectedCountdownEnd, Is.Not.Null, "The call did not start a countdown");
        });

        // The call and the two eligible marks.
        await RunUntilSeen(3);
        await AssertCycle(0);

        // Nothing fires twice while the countdown keeps running.
        await pair.RunTicksSync(10);
        await server.WaitAssertion(() => Assert.That(recorder.Seen, Has.Count.EqualTo(3), "A warning fired twice"));

        await server.WaitAssertion(() =>
        {
            roundEnd.CancelRoundEndCountdown(null, false);
            Assert.That(roundEnd.ExpectedCountdownEnd, Is.Null, "The recall did not clear the countdown");
        });

        await pair.RunTicksSync(10);
        await server.WaitAssertion(() => Assert.That(recorder.Seen, Has.Count.EqualTo(3), "A recall raised a warning"));

        await server.WaitAssertion(() => roundEnd.RequestRoundEnd(null, false));

        // A new call after the recall runs the whole schedule again.
        await RunUntilSeen(6);
        await AssertCycle(3);

        await pair.RunTicksSync(10);
        await server.WaitAssertion(() => Assert.That(recorder.Seen, Has.Count.EqualTo(6), "A warning fired twice after the re-call"));

        await server.WaitAssertion(() =>
        {
            roundEnd.CancelRoundEndCountdown(null, false);
            roundEnd.DefaultCountdownDuration = originalCountdown;
            warnings.Marks = originalMarks;
        });

        await pair.CleanReturnAsync();

        async Task RunUntilSeen(int count)
        {
            var timeout = Task.Delay(TimeSpan.FromSeconds(10));
            var seen = 0;
            while (!timeout.IsCompleted)
            {
                await server.WaitPost(() => seen = recorder.Seen.Count);
                if (seen >= count)
                    return;

                await pair.RunTicksSync(1);
            }

            Assert.Fail($"Saw {seen} round-end warning(s) inside the timeout, expected {count}");
        }

        Task AssertCycle(int start)
        {
            return server.WaitAssertion(() =>
            {
                Assert.That(recorder.Seen, Has.Count.EqualTo(start + 3));
                var call = recorder.Seen[start];
                var first = recorder.Seen[start + 1];
                var second = recorder.Seen[start + 2];

                Assert.Multiple(() =>
                {
                    Assert.That(call.IsCall, Is.True, "The first raise of a countdown was not the call");
                    Assert.That(call.Remaining, Is.LessThanOrEqualTo(Countdown).And.GreaterThan(TestMarks[1].Remaining));

                    Assert.That(first.IsCall, Is.False);
                    Assert.That(first.Remaining, Is.LessThanOrEqualTo(TestMarks[1].Remaining).And.GreaterThan(TestMarks[2].Remaining));

                    Assert.That(second.IsCall, Is.False);
                    Assert.That(second.Remaining, Is.LessThanOrEqualTo(TestMarks[2].Remaining).And.GreaterThan(TimeSpan.Zero));
                });
            });
        }
    }
}
