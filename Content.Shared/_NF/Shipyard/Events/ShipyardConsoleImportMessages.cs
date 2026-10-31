// Triad: legacy import. The two messages that drain the old save system into the drydock. The old
// ships are files on the player's disk, so the client is the only party that can enumerate them and
// the server is the only party that can judge them; these are the two halves of that conversation.
using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shipyard.Events;

/// <summary>
/// One local save file as the client offers it for judgement. Everything needed to verify the
/// signature is here and the ship itself is not: a signature covers the hash, so the server can
/// decide the file is one it signed without the payload ever crossing the wire. The payload is sent
/// once, for the one ship the player actually imports.
/// </summary>
[Serializable, NetSerializable]
public sealed class DrydockImportCandidate
{
    /// <summary>The client's handle for the file. Opaque to the server; never a path it acts on.</summary>
    public string FileId = string.Empty;

    public string Name = string.Empty;

    /// <summary>Unsigned display hint from the envelope. Never used for a decision.</summary>
    public int? Appraisal;

    /// <summary>Null when the file carries no signature at all, which is a legitimate state to offer.</summary>
    public byte[]? Signature;

    /// <summary>
    /// The X.509 SubjectPublicKeyInfo the file was signed under. Null with no signature. This is the
    /// whole of what the listing decision is made on, and it is the only part of the envelope small
    /// enough to send for every save at once.
    ///
    /// <para>No hash travels with it: the envelope does not store one, it is derived from the ship
    /// data, and the client cannot compute a SHA-256 in a packaged build. So the ledger and signature
    /// checks that need a hash happen at import, where the payload is.</para>
    /// </summary>
    public byte[]? PublicKey;

    public DrydockImportCandidate(string fileId, string name, int? appraisal, byte[]? signature, byte[]? publicKey)
    {
        FileId = fileId;
        Name = name;
        Appraisal = appraisal;
        Signature = signature;
        PublicKey = publicKey;
    }
}

/// <summary>
/// The client's inventory of local saves, sent when the drydock tab opens. The server answers by
/// putting the ones it will accept into the tab's state; it does not reply per candidate, so a
/// refusal is never itemised back to a modified client probing which files would pass.
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleImportManifestMessage : BoundUserInterfaceMessage
{
    public readonly List<DrydockImportCandidate> Candidates;

    public ShipyardConsoleImportManifestMessage(List<DrydockImportCandidate> candidates)
    {
        Candidates = candidates;
    }
}

/// <summary>
/// Import one listed save. Carries the payload because this is the point at which the server needs
/// it: it re-derives the hash, checks it against the candidate it listed, and only then loads.
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleImportMessage : BoundUserInterfaceMessage
{
    public readonly string FileId;
    public readonly string YamlData;

    public ShipyardConsoleImportMessage(string fileId, string yamlData)
    {
        FileId = fileId;
        YamlData = yamlData;
    }
}
