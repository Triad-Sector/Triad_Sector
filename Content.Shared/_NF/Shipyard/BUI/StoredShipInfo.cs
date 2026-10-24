// Triad: drydock tab. New file in the NF namespace because it is part of the shipyard console's
// existing interface state rather than a surface of its own.
using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shipyard.BUI;

/// <summary>
/// One stored ship as the drydock tab renders it: the persistent ship id plus display fields.
///
/// <para>This is the whole of what the client is told about a stored ship. Blobs, revisions,
/// checksums and the audit timeline stay server-side, so a client cannot learn what is in a ship
/// it cannot retrieve, and cannot ask for one by any handle other than an id the server just
/// sent it.</para>
/// </summary>
[Serializable, NetSerializable]
public sealed class StoredShipInfo
{
    public Guid ShipId;
    public string Name = string.Empty;

    /// <summary>
    /// Carried as the stored string rather than the enum. The column is written from
    /// <c>ShipSizeClass.ToString()</c> at store, so a row filed by an older build can name a class
    /// this build does not have; round-tripping it through the enum would turn that into a cast
    /// failure on the client instead of a label nobody recognises.
    /// </summary>
    public string? SizeClass;

    /// <summary>
    /// The row state as text, spelled as <c>DrydockShipState.ToString</c> gives it. Every hull the
    /// account has is listed, the ones that are out included, so the player sees why a berth is
    /// empty and so the tab can warn when an action would leave a ship with nowhere to dock; only a
    /// stored ship offers Retrieve.
    /// </summary>
    public string State = string.Empty;

    public int? BerthId;

    public StoredShipInfo(Guid shipId, string name, string? sizeClass, string state, int? berthId)
    {
        ShipId = shipId;
        Name = name;
        SizeClass = sizeClass;
        State = state;
        BerthId = berthId;
    }
}
