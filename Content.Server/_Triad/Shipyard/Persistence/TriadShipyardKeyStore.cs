using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._Triad.CCVar;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Configuration;
using Robust.Shared.IoC;

namespace Content.Server._Triad.Shipyard.Persistence;

public sealed class TriadShipyardKeyStore : ITriadShipyardKeyStore
{
    [Dependency] private readonly IServerDbManager _db = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;

    // Triad: in-memory set of our own signing-key hashes (hex of SHA-256(PublicKey)), active and
    // retired. null until PopulateOwnKeysAsync runs at bootstrap. Guarded because rotation (admin
    // thread) can extend it while the game thread reads via IsOwnKey.
    private readonly object _ownKeysLock = new();
    private HashSet<string>? _ownKeyHexHashes;

    /// <summary>
    /// F14 fix: load the active signing key from disk. The DB row tells us which key file to read
    /// (by KeyId); the actual private key bytes live on the filesystem with restrictive permissions.
    /// DB-only compromise yields no usable key material because there's no key material in the DB.
    ///
    /// If the active row references a missing or pre-F14 (null KeyId) key, we treat it as
    /// non-loadable: pre-F14 rows get retired automatically (so we don't pollute the audit history),
    /// and a missing file for a current-format row throws and refuses to start (deletion was
    /// either intentional and rotation should follow, or unintentional and silent recovery would
    /// be exactly the F3 ephemeral-key bug class).
    /// </summary>
    public Task<byte[]> GetOrCreateActivePrivateKeyAsync(CancellationToken ct)
    {
        return _db.RunTriadDbCommand(async (db, c) =>
        {
            var active = await db.TriadShipyardSigningKeys
                .Where(k => k.RetiredAt == null)
                .OrderByDescending(k => k.CreatedAt)
                .FirstOrDefaultAsync(c);

            if (active != null)
            {
                if (string.IsNullOrEmpty(active.KeyId))
                {
                    // Pre-F14 row with no KeyId reference. Retire it so we generate a fresh
                    // file-backed key below.
                    active.RetiredAt = DateTime.UtcNow;
                    if (string.IsNullOrEmpty(active.Notes))
                        active.Notes = "Auto-retired by F14 migration (no KeyId)";
                    await db.SaveChangesAsync(c);
                    active = null;
                }
                else
                {
                    var pemPath = ResolveKeyFilePath(active.KeyId);
                    if (!File.Exists(pemPath))
                    {
                        throw new InvalidOperationException(
                            $"TriadShipyardKeyStore: active signing-key row {active.Id} references "
                            + $"KeyId='{active.KeyId}' but '{pemPath}' does not exist. Refusing to start. "
                            + $"Admin must restore the key file from backup, or retire this row and rotate.");
                    }
                    return ReadPrivateKeyFromPem(pemPath);
                }
            }

            // No active row (fresh deploy, or retired pre-F14 row above). Generate a new keypair,
            // write the PEM to disk with restrictive permissions, insert the DB row.
            using var rsa = RSA.Create(2048);
            var keyId = GenerateKeyId();
            var pemPathNew = ResolveKeyFilePath(keyId);
            WritePrivateKeyToPem(pemPathNew, rsa);
            var privateBytes = rsa.ExportRSAPrivateKey();
            db.TriadShipyardSigningKeys.Add(new TriadShipyardSigningKey
            {
                PublicKey = rsa.ExportSubjectPublicKeyInfo(),
                KeyId = keyId,
                CreatedAt = DateTime.UtcNow,
                RetiredAt = null,
                Notes = "Generated on first run",
            });
            await db.SaveChangesAsync(c);
            return privateBytes;
        }, ct);
    }

    public Task<TriadShipyardSigningKey?> GetActiveAsync(CancellationToken ct)
    {
        return _db.RunTriadDbCommand(async (db, c) =>
        {
            return await db.TriadShipyardSigningKeys
                .Where(k => k.RetiredAt == null)
                .OrderByDescending(k => k.CreatedAt)
                .FirstOrDefaultAsync(c);
        }, ct);
    }

    public Task RotateAsync(string? notes, CancellationToken ct)
    {
        return _db.RunTriadDbCommand(async (db, c) =>
        {
            var now = DateTime.UtcNow;
            var actives = await db.TriadShipyardSigningKeys
                .Where(k => k.RetiredAt == null)
                .ToListAsync(c);
            foreach (var a in actives)
                a.RetiredAt = now;

            using var rsa = RSA.Create(2048);
            var keyId = GenerateKeyId();
            var pemPath = ResolveKeyFilePath(keyId);
            // Write the PEM to disk BEFORE the DB row; if the file write fails (disk full,
            // permission denied) we abort with no half-committed state in the DB.
            WritePrivateKeyToPem(pemPath, rsa);
            // F4 fix: capture the new private key bytes BEFORE the using-scope ends so we can
            // install them into the in-process signer after the DB write commits.
            var newPrivate = rsa.ExportRSAPrivateKey();
            db.TriadShipyardSigningKeys.Add(new TriadShipyardSigningKey
            {
                PublicKey = rsa.ExportSubjectPublicKeyInfo(),
                KeyId = keyId,
                CreatedAt = now,
                RetiredAt = null,
                Notes = notes,
            });
            await db.SaveChangesAsync(c);

            // F4 fix: keep DB and in-process signing key coherent. The install happens AFTER
            // SaveChanges so a DB rollback doesn't leave the in-process state pointing at a key
            // that isn't persisted. Old key file stays on disk for verifying old-signed ships.
            AuthenticatedShipFile.SetStaticKeyInfo(newPrivate);
            // Triad: the freshly-minted key is one of ours; add it to the load-authority set now so
            // ships signed after rotation verify without waiting for a restart.
            AddOwnKey(rsa.ExportSubjectPublicKeyInfo());
        }, ct);
    }

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
        // wins. We never false-reject our own freshly-signed ships during startup.
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
            // A rotation's AddOwnKey or PopulateOwnKeysAsync may have populated the set while we
            // queried. Union the existing set in so a boot-window rotation isn't lost when we overwrite,
            // then publish. Both sides read the same table, so last-writer-wins is otherwise harmless.
            if (_ownKeyHexHashes != null)
                pulled.UnionWith(_ownKeyHexHashes);
            _ownKeyHexHashes = pulled;
            return pulled.Contains(hex);
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

    private void AddOwnKey(byte[] publicKey)
    {
        var hex = Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();
        lock (_ownKeysLock)
            // A rotation in the boot window (before PopulateOwnKeysAsync seeds the set) must not be
            // dropped. If the set isn't created yet, start it from this key; the later populate /
            // IsOwnKey fallback unions in the rest of the rows. Previously this `?.` no-op'd and the
            // freshly-rotated key fell to the blocking DB path until populate ran.
            (_ownKeyHexHashes ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(hex);
    }
    // Triad end

    /// <summary>
    /// Returns a sortable, collision-resistant filename stem for a new key. The UTC timestamp
    /// makes directory listings chronological; the Guid suffix ensures collision-resistance
    /// even if two rotations land in the same second.
    /// </summary>
    private static string GenerateKeyId()
    {
        return $"tamper-{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}";
    }

    private string ResolveKeyFilePath(string keyId)
    {
        var dir = _cfg.GetCVar(TriadCCVars.TamperSigningKeysDir);
        EnsureKeysDirectory(dir);
        return Path.Combine(dir, $"{keyId}.pem");
    }

    /// <summary>
    /// Creates the keys directory if missing. On Unix, sets mode 0700 (rwx for owner only).
    /// On Windows, the default-inherited ACL is used; admin should restrict via icacls or
    /// equivalent if multiple OS users share the host (a follow-up to tighten cross-platform).
    /// </summary>
    private static void EnsureKeysDirectory(string dir)
    {
        if (Directory.Exists(dir))
            return;
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
                // Best-effort: if the platform/filesystem doesn't support mode bits, we proceed
                // and trust the umask + admin's directory-level controls.
            }
        }
    }

    private static byte[] ReadPrivateKeyFromPem(string path)
    {
        var pem = File.ReadAllText(path);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return rsa.ExportRSAPrivateKey();
    }

    private static void WritePrivateKeyToPem(string path, RSA rsa)
    {
        // ExportRSAPrivateKeyPem writes PKCS#1 ("-----BEGIN RSA PRIVATE KEY-----"); ImportFromPem
        // accepts both PKCS#1 and PKCS#8 on read, so PKCS#1 is fine and matches openssl defaults.
        var pem = rsa.ExportRSAPrivateKeyPem();
        File.WriteAllText(path, pem);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Best-effort; see EnsureKeysDirectory for reasoning.
            }
        }
    }
}
