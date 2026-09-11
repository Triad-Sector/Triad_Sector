using Content.Server._Triad.Shipyard.Admin;
using NUnit.Framework;

namespace Content.Tests.Server._Triad;

// The registry is the load-bearing piece behind live audit updates: one audit-write signal has to
// fan out to every open tamper panel so multiple admins all see new events without re-opening.
[TestFixture]
public sealed class TriadTamperAdminEuiRegistryTest
{
    private sealed class FakeObserver : ITriadTamperAuditObserver
    {
        public int NotifyCount;
        public void OnAuditChanged() => NotifyCount++;
    }

    [Test]
    public void NotifyAuditChanged_NotifiesRegisteredObserver()
    {
        var registry = new TriadTamperAdminEuiRegistry();
        var observer = new FakeObserver();
        registry.Register(observer);

        registry.NotifyAuditChanged();

        Assert.That(observer.NotifyCount, Is.EqualTo(1));
    }

    [Test]
    public void NotifyAuditChanged_SkipsUnregisteredObserver()
    {
        var registry = new TriadTamperAdminEuiRegistry();
        var observer = new FakeObserver();
        registry.Register(observer);
        registry.Unregister(observer);

        registry.NotifyAuditChanged();

        Assert.That(observer.NotifyCount, Is.EqualTo(0));
    }

    [Test]
    public void NotifyAuditChanged_NotifiesAllRegisteredObservers()
    {
        // The multi-admin case: every open panel must be poked off a single signal.
        var registry = new TriadTamperAdminEuiRegistry();
        var first = new FakeObserver();
        var second = new FakeObserver();
        registry.Register(first);
        registry.Register(second);

        registry.NotifyAuditChanged();

        Assert.That(first.NotifyCount, Is.EqualTo(1));
        Assert.That(second.NotifyCount, Is.EqualTo(1));
    }
}
