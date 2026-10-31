// Triad: legacy import. New file in the NF namespace for the same reason the other drydock state
// types are there - it is part of the shipyard console's interface state, not a surface of its own.
using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shipyard.BUI;

/// <summary>
/// One legacy save file the server has agreed this player may import, as the drydock tab draws it.
///
/// <para>Old saves live on the player's own disk, not on the server, so this list is assembled from
/// a manifest the client offers and then filtered by the server. Only files that pass the tamper
/// check appear; the rest are absent rather than greyed, so the tab never explains the check to
/// someone trying to get around it.</para>
///
/// <para>No size class here on purpose. The class is the hull's built tile count, which is not
/// knowable until the ship is on a map, so the berth is sized from the server's own measurement at
/// import rather than from anything the client claims.</para>
/// </summary>
[Serializable, NetSerializable]
public sealed class DrydockImportShipInfo
{
    /// <summary>
    /// The client's own handle for the file, echoed back on the import message. Opaque to the
    /// server beyond matching it to a candidate it just listed; the payload's hash is what actually
    /// identifies the ship.
    /// </summary>
    public string FileId = string.Empty;

    public string Name = string.Empty;

    /// <summary>
    /// The save-time appraisal from the envelope. Display only, and deliberately so: the field is
    /// outside the signature and can be edited freely on disk.
    /// </summary>
    public int? Appraisal;

    public DrydockImportShipInfo(string fileId, string name, int? appraisal)
    {
        FileId = fileId;
        Name = name;
        Appraisal = appraisal;
    }
}
