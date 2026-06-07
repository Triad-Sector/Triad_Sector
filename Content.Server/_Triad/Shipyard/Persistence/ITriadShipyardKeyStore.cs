using System;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;

namespace Content.Server._Triad.Shipyard.Persistence;

public interface ITriadShipyardKeyStore
{
    Task<byte[]> GetOrCreateActivePrivateKeyAsync(CancellationToken ct);
    Task<TriadShipyardSigningKey?> GetActiveAsync(CancellationToken ct);
    Task RotateAsync(string? notes, CancellationToken ct);

    /// <summary>
    /// True if <paramref name="keyHash"/> (SHA-256 of an X.509 SubjectPublicKeyInfo) is one of the
    /// server's own signing keys, active or retired. This is the load-authority check: a ship is
    /// ours iff it was signed by a key we generated. Answered from an in-memory set seeded by
    /// <see cref="PopulateOwnKeysAsync"/> at bootstrap and extended on rotation.
    /// </summary>
    bool IsOwnKey(byte[] keyHash);

    /// <summary>
    /// Seeds the own-key set from every row in the signing-keys table (active and retired).
    /// Call once at startup, before any load can be evaluated.
    /// </summary>
    Task PopulateOwnKeysAsync(CancellationToken ct);
}
