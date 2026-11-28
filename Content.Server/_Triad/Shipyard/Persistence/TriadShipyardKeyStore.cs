using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.IoC;

namespace Content.Server._Triad.Shipyard.Persistence;

/// <summary>
/// Own-key lookup over the signing-keys table. The table is now the read-only list of keys the
/// server once signed ship saves with: nothing signs, generates, or rotates keys any more, and the
/// private halves are no longer read. Only the public keys matter, to recognise our own legacy saves
/// on import.
/// </summary>
public sealed partial class TriadShipyardKeyStore : ITriadShipyardKeyStore
{
    [Dependency] private IServerDbManager _db = default!;

    // Triad: in-memory set of our own signing-key hashes (hex of SHA-256(PublicKey)), active and
    // retired. null until PopulateOwnKeysAsync runs at bootstrap. Guarded because the populate runs
    // on a thread-pool thread while the game thread reads via IsOwnKey.
    private readonly object _ownKeysLock = new();
    private HashSet<string>? _ownKeyHexHashes;

    // Triad start: own-key load authority. A ship is "ours" iff signed by a key we generated
    // (active or retired). Replaces the removed admin trust-table lookup.
    public bool IsOwnKey(byte[] keyHash)
    {
        var hex = Convert.ToHexString(keyHash).ToLowerInvariant();
        lock (_ownKeysLock)
        {
            if (_ownKeyHexHashes != null)
                return _ownKeyHexHashes.Contains(hex);
        }

        // Cache not populated yet (brief boot window before PopulateOwnKeysAsync runs). Pull the rows
        // once, seed the set, and answer from it, rather than rescanning the whole table on every call.
        // This is still one sync-over-async DB hit on the game thread, but only until the set is
        // populated (bootstrap does that early); after that the lock-guarded fast path above always
        // wins. We never false-reject our own signed ships during startup.
        var pulled = _db.RunTriadDbCommand(async (db, c) =>
        {
            var pubkeys = await db.TriadShipyardSigningKeys.Select(k => k.PublicKey).ToListAsync(c);
            var s = new HashSet<string>(pubkeys.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var pk in pubkeys)
                s.Add(Convert.ToHexString(SHA256.HashData(pk)).ToLowerInvariant());
            return s;
        }, default).GetAwaiter().GetResult();

        lock (_ownKeysLock)
        {
            // PopulateOwnKeysAsync may have published the set while we queried. Both sides read the
            // same read-only table, so whichever set is published answers the same.
            _ownKeyHexHashes ??= pulled;
            return _ownKeyHexHashes.Contains(hex);
        }
    }

    public Task PopulateOwnKeysAsync(CancellationToken ct)
    {
        return _db.RunTriadDbCommand(async (db, c) =>
        {
            var pubkeys = await db.TriadShipyardSigningKeys.Select(k => k.PublicKey).ToListAsync(c);
            var set = new HashSet<string>(pubkeys.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var pk in pubkeys)
                set.Add(Convert.ToHexString(SHA256.HashData(pk)).ToLowerInvariant());
            lock (_ownKeysLock)
                _ownKeyHexHashes = set;
        }, ct);
    }
    // Triad end
}
