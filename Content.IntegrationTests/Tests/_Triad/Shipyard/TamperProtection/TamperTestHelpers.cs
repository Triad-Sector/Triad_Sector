using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._Triad.Shipyard;
using Content.Server.Database;

namespace Content.IntegrationTests.Tests._Triad.Shipyard.TamperProtection;

/// <summary>
/// Shared setup for the tamper tests. Nothing in the server signs ship saves any more, so a test that
/// needs a signed legacy envelope writes one here with its own key, and a test that needs that key to
/// count as ours records it in the signing-keys table the way a key the server once signed with sits
/// there.
/// </summary>
internal static class TamperTestHelpers
{
    /// <summary>
    /// A legacy envelope for <paramref name="content"/> signed by <paramref name="key"/>: PKCS#1 v1.5
    /// over SHA-256 of the UTF-8 ship data, in the field layout the removed save path wrote.
    /// </summary>
    internal static string SignedEnvelopeText(RSA key, string content)
    {
        return EnvelopeText(key, content, content);
    }

    /// <summary>
    /// An envelope whose signature covers <paramref name="signedContent"/> but whose payload is
    /// <paramref name="payload"/>: a file edited on disk after it was signed.
    /// </summary>
    internal static AuthenticatedShipFile TamperedEnvelope(RSA key, string signedContent, string payload)
    {
        return AuthenticatedShipFile.FromShipFile(EnvelopeText(key, signedContent, payload));
    }

    private static string EnvelopeText(RSA key, string signedContent, string payload)
    {
        var sig = key.SignHash(SHA256.HashData(Encoding.UTF8.GetBytes(signedContent)),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var shipData = Encoding.UTF8.GetBytes(payload);
        var pub = key.ExportSubjectPublicKeyInfo();
        // OID string is the envelope's algorithm marker (matches AuthenticatedShipFile.SignatureOid).
        var oid = CryptoConfig.MapNameToOID("SHA256") ?? "1.2.840.113549.1.1.11";

        return
            "version: \"1.0\"\n" +
            $"signature: \"{Convert.ToBase64String(sig)}\"\n" +
            $"signaturePublicKey: \"{Convert.ToBase64String(pub)}\"\n" +
            $"signatureOid: \"{oid}\"\n" +
            $"shipData: \"{Convert.ToBase64String(shipData)}\"\n";
    }

    internal static AuthenticatedShipFile SignedEnvelope(RSA key, string content)
    {
        return AuthenticatedShipFile.FromShipFile(SignedEnvelopeText(key, content));
    }

    /// <summary>
    /// Records <paramref name="key"/>'s public half as one of the server's own signing keys. The
    /// caller still has to reseed the key store's cache with PopulateOwnKeysAsync.
    /// </summary>
    internal static Task InsertOwnKey(IServerDbManager db, RSA key)
    {
        var publicKey = key.ExportSubjectPublicKeyInfo();
        return db.RunTriadDbCommand(async (context, token) =>
        {
            context.TriadShipyardSigningKeys.Add(new TriadShipyardSigningKey
            {
                PublicKey = publicKey,
                CreatedAt = DateTime.UtcNow,
                Notes = "tamper test key",
            });
            await context.SaveChangesAsync(token);
        }, CancellationToken.None);
    }
}
