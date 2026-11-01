// Triad: drydock tab.
using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shipyard.Events;

/// <summary>
/// Triad: which of the three long drydock waits a progress figure belongs to. The tab draws three
/// separate indicators - the deed card's Store button, a berth row's Retrieve, an import row's
/// Import - and one message type serves all of them rather than three near-identical ones.
/// </summary>
[Serializable, NetSerializable]
public enum DrydockProgressKind : byte
{
    Store,
    Retrieve,
    Import,
}

/// <summary>
/// Triad: server -&gt; client, sent to the acting player only.
///
/// <para>Not a BUI state. Republishing the whole console state per tick would re-run the shuttle
/// listing and a grid appraisal on every publish, which costs more than the work being reported
/// on; the state carries a percentage too, but only so a console reopened mid-store draws the
/// indicator, and it is refreshed at the pace the state already moves at.</para>
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleDrydockProgressMessage : BoundUserInterfaceMessage
{
    public readonly DrydockProgressKind Kind;

    /// <summary>Whole percent, 0 to 100. Honest, not a spinner: the pipeline knows its entity count up front.</summary>
    public readonly int Percent;

    public ShipyardConsoleDrydockProgressMessage(DrydockProgressKind kind, int percent)
    {
        Kind = kind;
        Percent = percent;
    }
}
