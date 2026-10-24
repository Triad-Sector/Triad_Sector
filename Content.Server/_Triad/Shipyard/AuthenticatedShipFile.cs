using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server._Triad.Shipyard;

/// <summary>
/// Read-only view of a legacy signed ship save. Old saves are files on the player's own disk, so
/// everything here is untrusted input: this parses the envelope, exposes the wrapped grid document,
/// and checks the signature against the public key the envelope itself carries. Whether that key is
/// one of ours is the tamper policy's question, not this class's. Nothing signs any more; the
/// format is described here only so it can be read.
/// </summary>
public sealed class AuthenticatedShipFile
{
    // private default constructor. Use a factory method
    private AuthenticatedShipFile() { _shipData = []; }

    // constant text in the current version.
    // An envelope with any other version is read as unsigned.
    private const string VersionKey = "version";
    private const string VersionValue = "1.0";

    // The signature.
    // This is serialized as a base 64 encoded sequence of bytes
    // that represents a cryptographic signature of the format and key
    // as described by the Oid and public key
    private const string SignatureValueKey = "signature";
    private byte[]? _signatureValue;

    // The public key the envelope claims signed it
    // Again, b64 for serialization
    // This uses the X509 SubjectKeyInfo format
    private const string SignaturePublicKeyKey = "signaturePublicKey";
    private byte[]? _signaturePublicKey;

    // The parameters of the signature.
    // We only support the one kind of signing: PKCS#1 v1.5 over SHA-256
    private const string SignatureOidKey = "signatureOid";
    private static readonly string SignatureOid = CryptoConfig.MapNameToOID("SHA256") ?? "1.2.840.113549.1.1.11";

    // The data for the ship itself.
    // This is serialized as a base64 encoded sequence of bytes
    // that form a valid utf8 string
    // in the yaml format/schema that robust expects
    private const string ShipDataKey = "shipData";
    private byte[] _shipData;

    // Advisory display-only field carrying the save-time appraisal. Deliberately not covered by the
    // signature, so it can be edited freely on disk without breaking signature validation. Treat it
    // like any other unsigned hint: useful for UI and the audit row, never for enforcement.
    private const string AppraisalKey = "appraisal";
    private int? _appraisal;

    // calculated as needed
    private byte[]? _hashValue;

    // get the public key the envelope carries. This says nothing about whether we trust it
    public byte[]? GetInstancePublicKeyInfo()
    {
        return _signaturePublicKey;
    }

    // the unsigned save-time appraisal, if the envelope carried a parseable one
    public int? Appraisal => _appraisal;

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

        if (Fetch(VersionKey, rootMap) != VersionValue
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

    // wrap raw text that is not an envelope as an unsigned ship
    private static AuthenticatedShipFile FromShipData(string ship)
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

    // test if the ship is signed according to its own public key
    // this does not test if that public key is trusted
    public bool IsShipSigned()
    {
        // F6 fix: previously this only guarded _signatureValue. _signaturePublicKey is also
        // nullable, and ImportSubjectPublicKeyInfo throws CryptographicException on null DER
        // (empty ReadOnlySpan) or malformed bytes. A client-crafted envelope with valid
        // b64 signature but absent/garbled pubkey would throw out of here, propagate up
        // through EvaluateLoad, and crash the network handler that received the file - a clean
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

    // Get the ship in the format that Robust-Core wants it
    public string ShipYamlString()
    {
        return Encoding.UTF8.GetString(_shipData);
    }
};
