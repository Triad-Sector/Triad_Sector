using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.Server._Triad.Shipyard;

public sealed class AuthenticatedShipFile
{
    // private default constructor. Use a factory method
    private AuthenticatedShipFile() { _shipData = []; }

    // constant text in the current version.
    // This will allow us to make modifications later without
    // needing to break support for earlier files
    private const string VersionKey = "version";
    private const string VersionValue = "1.0";

    // The signature.
    // This is serialized as a base 64 encoded sequence of bytes
    // that represents a cryptographic signature of the format and key
    // as described by the Oid and public key
    private const string SignatureValueKey = "signature";
    private byte[]? _signatureValue;

    // The public key used to sign this ship
    // Again, b64 for serialization
    // This uses the X509 SubjectKeyInfo format
    // Stored so that we know what key to check against
    private const string SignaturePublicKeyKey = "signaturePublicKey";
    private byte[]? _signaturePublicKey;

    // The parameters of the signature.
    // We only support the one kind of signing
    private const string SignatureOidKey = "signatureOid";
    private static readonly string SignatureOid = CryptoConfig.MapNameToOID("SHA256") ?? "1.2.840.113549.1.1.11";

    // The data for the ship itself.
    // This is serialized as a base64 encoded sequence of bytes
    // that form a valid utf8 string
    // in the yaml format/schema that robust expects
    private const string ShipDataKey = "shipData";
    private byte[] _shipData;

    // Advisory display-only field carrying the save-time appraisal. Deliberately not covered
    // by the signature: the load-side affordability gate, when wired, will read the authoritative
    // price from the audit log row keyed by ShipHash (TriadShipyardAuditEvent.SaveTimeAppraisal),
    // not from this envelope field. That means the field can be edited freely on disk without
    // breaking signature validation and without granting any economic advantage. Treat it like
    // any other unsigned hint: useful for UI, never for enforcement.
    private const string AppraisalKey = "appraisal";
    private int? _appraisal;

    // calculated as needed
    private byte[]? _hashValue;

    // F3 fix: no ephemeral key on class load. Callers must install a key via SetStaticKeyInfo
    // before any sign/get operation. TriadTamperPolicyService.BootstrapKeyAsync does this at
    // server start using the DB-persisted private key; tests do it via [OneTimeSetUp].
    // Operations that need the key throw InvalidOperationException rather than fabricating a
    // one-shot random key whose public half will never appear in the trust table.
    //
    // F4 fix relies on this: TriadShipyardKeyStore.RotateAsync now calls SetStaticKeyInfo
    // after persisting the new row, keeping the DB and in-process state coherent.
    //
    // All access is guarded by RsaLock because RSA instances are not thread-safe and signing on the
    // game thread can race with rotation initiated from an admin EUI thread. We use the cross-platform
    // RSA.Create() factory, not RSACryptoServiceProvider: the latter is the Windows-CAPI type, and its
    // keysize validation differs on Linux (the old `new RSACryptoServiceProvider(1)` keygen-skip trick
    // threw "Specified key is not a valid size for this algorithm" on the Linux CI runner).
    private static RSA? Rsa;
    private static readonly object RsaLock = new();

    // the current version
    public static string Version() { return VersionValue; }

    // get the current signing key's public key part
    // in the X.509 SubjectPublicKeyInfo format
    public static byte[] GetStaticPublicKeyInfo()
    {
        lock (RsaLock)
        {
            if (Rsa == null)
                throw new InvalidOperationException(
                    "AuthenticatedShipFile: no signing key installed. Call SetStaticKeyInfo first.");
            return Rsa.ExportSubjectPublicKeyInfo();
        }
    }

    // get the current signing key's private key part
    // Do not leak this!
    public static byte[] GetStaticPrivateKeyInfo()
    {
        lock (RsaLock)
        {
            if (Rsa == null)
                throw new InvalidOperationException(
                    "AuthenticatedShipFile: no signing key installed. Call SetStaticKeyInfo first.");
            return Rsa.ExportRSAPrivateKey();
        }
    }

    // set the current signing key. This only takes the private key
    public static int SetStaticKeyInfo(byte[] privateKeyInfo)
    {
        lock (RsaLock)
        {
            // RSA.Create() generates lazily; ImportRSAPrivateKey sets the key params before any
            // keygen is triggered, so no throwaway key is produced (same intent as the old
            // RSACryptoServiceProvider(1) trick, but valid on every platform).
            var fresh = RSA.Create();
            fresh.ImportRSAPrivateKey(privateKeyInfo, out var read);
            // Dispose the previous key on rotation so its native handle doesn't leak. Safe under
            // RsaLock: every reader (SignShip, GetStatic*) takes the same lock, so nothing is using the
            // old instance while we swap it.
            Rsa?.Dispose();
            Rsa = fresh;
            return read;
        }
    }


    // get this instance's public key. This can be different from the signing key
    public byte[]? GetInstancePublicKeyInfo()
    {
        return _signaturePublicKey;
    }


    public int? Appraisal
    {
        get => _appraisal;
        set => _appraisal = value;
    }

    private static bool FromShipFile(MappingDataNode rootMap, [NotNullWhen(true)] out AuthenticatedShipFile? signedShip)
    {
        signedShip = null;

        var output = new AuthenticatedShipFile();

        output._shipData = FetchB64(ShipDataKey, rootMap) ?? [];

        if (output._shipData.Length == 0)
        {
            // no shipData, this must not be the right format
            return false;
        }

        // appraisal is optional. Parser failure returns null and does not reject the envelope.
        if (rootMap.TryGet(AppraisalKey, out var appraisalRaw)
            && appraisalRaw is ValueDataNode appraisalValNode
            && int.TryParse(appraisalValNode.Value, out var appraisalInt))
        {
            output._appraisal = appraisalInt;
        }

        signedShip = output;

        if (Fetch(VersionKey, rootMap) != Version()
            || Fetch(SignatureOidKey, rootMap) != SignatureOid
            )
        {
            // a mandatory fixed value field didn't match up
            // we are going to ignore the signature in this file.
            // It isn't correctly signed
            return true;
        }

        output._signatureValue = FetchB64(SignatureValueKey, rootMap);
        output._signaturePublicKey = FetchB64(SignaturePublicKeyKey, rootMap);

        return true;

        // local methods to make the logic above clearer

        string? Fetch(in string key, in MappingDataNode node)
        {
            if (node.TryGet(key, out var nodeRaw) && nodeRaw is ValueDataNode dataNode)
            {
                return dataNode.Value;
            }

            return null;
        }

        byte[]? FetchB64 (in string key, in MappingDataNode node)
        {
            var str = Fetch(key, node);
            if (str == null)
            {
                return null;
            }
            try
            {
                return Convert.FromBase64String(str);
            }
            catch (FormatException)
            {
                // hash wasn't a b64 string
                return null;
            }
        }
    }


    // provide a raw ship-grid in Robust-Core's grid format to get an AuthenticatedShipFile
    // It isn't signed yet
    public static AuthenticatedShipFile FromShipData(string ship)
    {
        return new AuthenticatedShipFile{_shipData = Encoding.UTF8.GetBytes(ship)};
    }

    // provide a ship file from a client to get an AuthenticatedShipFile
    // It is only signed if the file is well-formed and valid.
    public static AuthenticatedShipFile FromShipFile(string untrustedShipFileContents)
    {
        var sax = DataNodeParser.ParseYamlStream(new StringReader(untrustedShipFileContents)).ToArray();
        if (sax.Length == 0)
        {
            // an empty document, or one that didn't parse
            // this is an unsigned ship
            return FromShipData(untrustedShipFileContents);
        }

        var root = sax.First();
        if (root == null)
        {
            // an empty document, or one that didn't parse
            // this is an unsigned ship
            return FromShipData(untrustedShipFileContents);
        }

        if (root.Root is not MappingDataNode rootMap)
        {
            // The root node wasn't a key-value map
            // this is an unsigned ship
            return FromShipData(untrustedShipFileContents);
        }

        return FromShipFile(rootMap, out var ship)
            ? ship
            : /* this is an unsigned ship */ FromShipData(untrustedShipFileContents);

    }

    // return the bytes of a hash of the current ship data
    // It is calculated lazily
    public byte[] GetHash()
    {
        if (_hashValue == null)
        {
            _hashValue = SHA256.HashData(_shipData);
        }
        return _hashValue;
    }

    // Sign using the current static signing key
    public void SignShip()
    {
        lock (RsaLock)
        {
            if (Rsa == null)
                throw new InvalidOperationException(
                    "AuthenticatedShipFile.SignShip: no signing key installed. "
                    + "TriadTamperPolicyService.BootstrapKeyAsync must complete before any save.");
            // PKCS#1 v1.5 over the SHA-256 hash. Byte-identical to the old RSACryptoServiceProvider
            // SignHash(hash, oid) output, so the SignatureOid marker written into the envelope still
            // describes the signature.
            _signatureValue = Rsa.SignHash(GetHash(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            _signaturePublicKey = Rsa.ExportSubjectPublicKeyInfo();
        }
    }

    // test if the ship is signed according to its own public key
    // this does not test if that public key is trusted
    public bool IsShipSigned()
    {
        // F6 fix: previously this only guarded _signatureValue. _signaturePublicKey is also
        // nullable, and ImportSubjectPublicKeyInfo throws CryptographicException on null DER
        // (empty ReadOnlySpan) or malformed bytes. A client-crafted envelope with valid
        // b64 signature but absent/garbled pubkey would throw out of here, propagate up
        // through EvaluateLoad, and crash the OnLoadMessage network handler - a clean
        // client-triggered DoS. Any malformed pubkey condition now returns false (caller
        // treats this as 'not signed', which routes through the normal unsigned/invalid
        // signature decision path with appropriate audit + popup).
        if (_signatureValue == null || _signaturePublicKey == null)
        {
            return false;
        }

        // using: dispose the native key handle on every call. This runs once per signed-ship load, so
        // not disposing (the old behavior) leaks a handle each time and accumulates on a busy server.
        using var localRsa = RSA.Create();
        try
        {
            localRsa.ImportSubjectPublicKeyInfo(_signaturePublicKey, out var bytesRead);
            return localRsa.VerifyHash(GetHash(), _signatureValue, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }


    // get the ship in a format suitable for storage on disk
    public string ShipFileString()
    {
        var output = new MappingDataNode();

        output.Add(VersionKey, Version());

        if (_signatureValue != null && _signaturePublicKey != null)
        {
            output.Add(SignatureValueKey, Convert.ToBase64String(_signatureValue));
            output.Add(SignaturePublicKeyKey, Convert.ToBase64String(_signaturePublicKey));
            output.Add(SignatureOidKey, SignatureOid);
        }

        output.Add(ShipDataKey, Convert.ToBase64String(_shipData));

        if (_appraisal.HasValue)
        {
            output.Add(AppraisalKey, _appraisal.Value.ToString());
        }

        // I lifted the implementation of a small
        // private function that fenndragon wrote
        // called ShipyardGridSaveSystem::WriteYamlToString
        var writer = new StringWriter();
        new YamlStream { new YamlDocument(output.ToYaml()) }.Save(new YamlMappingFix(new Emitter(writer)), false);
        return writer.ToString();

    }

    // Get the ship in the format that Robust-Core wants it
    public string ShipYamlString()
    {
        return Encoding.UTF8.GetString(_shipData);
    }


};
