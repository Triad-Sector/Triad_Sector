using System.Threading;
using System.Threading.Tasks;

namespace Content.Server._Triad.Shipyard.Persistence;

/// <summary>
/// Read-only answer to "did this server sign that?" for legacy import. The signing-keys table is the
/// list of keys the server once signed ship saves with; nothing adds to it any more.
/// </summary>
public interface ITriadShipyardKeyStore
{
    /// <summary>
    /// True if <paramref name="keyHash"/> (SHA-256 of an X.509 SubjectPublicKeyInfo) is one of the
    /// keys this server once signed with, active or retired. This is the load-authority check: a ship
    /// is ours iff it was signed by a key we generated. Answered from an in-memory set seeded by
    /// <see cref="PopulateOwnKeysAsync"/> at bootstrap.
    /// </summary>
    bool IsOwnKey(byte[] keyHash);

    /// <summary>
    /// Seeds the own-key set from every row in the signing-keys table (active and retired).
    /// Call once at startup, before any load can be evaluated.
    /// </summary>
    Task PopulateOwnKeysAsync(CancellationToken ct);
}
