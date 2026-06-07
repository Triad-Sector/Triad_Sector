using System.Collections.Generic;
using System.Linq;

namespace Content.Server._Triad.Shipyard.Admin;

/// <summary>
/// Something that wants to be poked when the tamper audit log gains new entries. Implemented by the
/// open admin panels (<see cref="TriadTamperAdminEui"/>); the registry fans a single audit-write
/// signal out to every one of them.
/// </summary>
public interface ITriadTamperAuditObserver
{
    void OnAuditChanged();
}

/// <summary>
/// Tracks the currently-open tamper admin panels so one audit-write signal updates all of them, which
/// is what lets several admins watch the same live feed without re-opening it.
/// </summary>
/// <remarks>
/// Register/Unregister fire on panel open/close and NotifyAuditChanged fires from the policy service's
/// main-thread Update tick (the background audit consumer only flips a flag; the marshaling happens
/// there). All three therefore run on the main game thread, so the set needs no locking.
/// </remarks>
public sealed class TriadTamperAdminEuiRegistry
{
    private readonly HashSet<ITriadTamperAuditObserver> _observers = new();

    public void Register(ITriadTamperAuditObserver observer)
    {
        _observers.Add(observer);
    }

    public void Unregister(ITriadTamperAuditObserver observer)
    {
        _observers.Remove(observer);
    }

    public void NotifyAuditChanged()
    {
        // Snapshot: a panel could close (and unregister) while reacting to the poke, which would
        // otherwise mutate the set mid-iteration.
        foreach (var observer in _observers.ToArray())
            observer.OnAuditChanged();
    }
}
