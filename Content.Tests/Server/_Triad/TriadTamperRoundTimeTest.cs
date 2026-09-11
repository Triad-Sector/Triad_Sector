using System;
using Content.Server._Triad.Shipyard.Admin;
using NUnit.Framework;

namespace Content.Tests.Server._Triad;

// Round-time filter boxes are relative offsets into a round; the server has to anchor them against the
// round's StartDate to get the absolute timestamp window the audit query actually filters on.
[TestFixture]
public sealed class TriadTamperRoundTimeTest
{
    private static readonly DateTime Start = new(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public void NullStart_CannotAnchor_ReturnsNoWindow()
    {
        // No recorded round start means the offsets can't be anchored; must not produce a bogus window.
        var (from, to) = TriadTamperRoundTime.AnchorRoundTime(null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

        Assert.That(from, Is.Null);
        Assert.That(to, Is.Null);
    }

    [Test]
    public void BothBounds_AnchorToStart()
    {
        var (from, to) = TriadTamperRoundTime.AnchorRoundTime(Start, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

        Assert.That(from, Is.EqualTo(Start + TimeSpan.FromMinutes(5)));
        Assert.That(to, Is.EqualTo(Start + TimeSpan.FromMinutes(10)));
    }

    [Test]
    public void OnlyFrom_LeavesUpperUnbounded()
    {
        var (from, to) = TriadTamperRoundTime.AnchorRoundTime(Start, TimeSpan.FromMinutes(5), null);

        Assert.That(from, Is.EqualTo(Start + TimeSpan.FromMinutes(5)));
        Assert.That(to, Is.Null);
    }

    [Test]
    public void OnlyTo_LeavesLowerUnbounded()
    {
        var (from, to) = TriadTamperRoundTime.AnchorRoundTime(Start, null, TimeSpan.FromMinutes(10));

        Assert.That(from, Is.Null);
        Assert.That(to, Is.EqualTo(Start + TimeSpan.FromMinutes(10)));
    }

    [Test]
    public void NoBounds_ReturnsNoWindow()
    {
        var (from, to) = TriadTamperRoundTime.AnchorRoundTime(Start, null, null);

        Assert.That(from, Is.Null);
        Assert.That(to, Is.Null);
    }
}
