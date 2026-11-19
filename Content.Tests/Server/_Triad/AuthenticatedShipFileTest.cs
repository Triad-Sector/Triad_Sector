using System;
using System.Security.Cryptography;
using System.Text;
using Content.Server._Triad.Shipyard;
using NUnit.Framework;

namespace Content.Tests.Server._Triad;

public static class RandomStringGen
{
    // return a uniformly distributed ascii string
    // it can include nulls
    public static string AsciiString(int testStringLength, int seed)
    {
        // use builtin to generate random ascii characters
        return new Random(seed).GetString(
            "\x00\x01\x02\x03\x04\x05\x06\x07\x08\x09\x0A\x0B\x0C\x0D\x0E\x0F"
            +"\x10\x11\x12\x13\x14\x15\x16\x17\x18\x19\x1A\x1B\x1C\x1D\x1E\x1F"
            +"\x20\x21\x22\x23\x24\x25\x26\x27\x28\x29\x2A\x2B\x2C\x2D\x2E\x2F"
            +"\x30\x31\x32\x33\x34\x35\x36\x37\x38\x39\x3A\x3B\x3C\x3D\x3E\x3F"
            +"\x40\x41\x42\x43\x44\x45\x46\x47\x48\x49\x4A\x4B\x4C\x4D\x4E\x4F"
            +"\x50\x51\x52\x53\x54\x55\x56\x57\x58\x59\x5A\x5B\x5C\x5D\x5E\x5F"
            +"\x60\x61\x62\x63\x64\x65\x66\x67\x68\x69\x6A\x6B\x6C\x6D\x6E\x6F"
            +"\x70\x71\x72\x73\x74\x75\x76\x77\x78\x79\x7A\x7B\x7C\x7D\x7E\x7F",
            testStringLength);
    }



    // return a uniformly distributed Unicode string.
    // It can contain any Unicode code point,
    // including unassigned ones and ones that take more than one wchar to store
    public static string UnicodeString(int testStringLength, int seed)
    {
        var rng = new Random(seed);
        var outputBuffer = new byte[testStringLength*4];
        var offset = 0;
        var characterCount = 0;
        while (characterCount < testStringLength)
        {
            var codePoint = rng.Next(0, 0x110000); // the valid non-null unicode codepoint range

            if (codePoint <= 0b01111111) // 1 byte utf8
            {
                // ascii
                // 0b0xxxxxxx
                outputBuffer[offset] = (byte)(0b01111111 & codePoint);
                if (System.Text.Unicode.Utf8.IsValid(outputBuffer.AsSpan()[offset..(offset+1)]))
                {
                    offset += 1;
                    characterCount += 1;
                }
            }
            else if (codePoint <= 0b011111_1111111) // 2 byte utf8
            {
                // 0b110xxxxx_1xxxxxxx
                outputBuffer[offset+0] = (byte)(0x110_00000 | (0b00001111 & (codePoint >> 7)));
                outputBuffer[offset+1] = (byte)(0x10_000000 | (0b01111111 & codePoint));
                if (System.Text.Unicode.Utf8.IsValid(outputBuffer.AsSpan()[offset..(offset+2)]))
                {
                    offset += 2;
                    characterCount += 1;
                }
            }
            else if (codePoint <= 0b01111_1111111_1111111) // 3 byte utf8
            {
                // 0b1110xxxx_1xxxxxxx_1xxxxxxx
                outputBuffer[offset+0] = (byte)(0x1110_0000 | (0b00001111 & (codePoint >> (7+7))));
                outputBuffer[offset+1] = (byte)(0x10_000000 | (0b01111111 & (codePoint >> 7)));
                outputBuffer[offset+2] = (byte)(0x10_000000 | (0b01111111 & codePoint));
                if (System.Text.Unicode.Utf8.IsValid(outputBuffer.AsSpan()[offset..(offset+3)]))
                {
                    offset += 3;
                    characterCount += 1;
                }
            }
            else if(codePoint <= 0b0111_1111111_1111111_1111111) // 4 byte utf8
            {
                // 0b11110xxx_1xxxxxxx_1xxxxxxx_1xxxxxxx
                outputBuffer[offset+0] = (byte)(0x11110_000 | (0b00000111 & (codePoint >> (7+7+7))));
                outputBuffer[offset+1] = (byte)(0x10_000000 | (0b01111111 & (codePoint >> (7+7))));
                outputBuffer[offset+2] = (byte)(0x10_000000 | (0b01111111 & (codePoint >> 7)));
                outputBuffer[offset+3] = (byte)(0x10_000000 | (0b01111111 & codePoint));
                if (System.Text.Unicode.Utf8.IsValid(outputBuffer.AsSpan()[offset..(offset+4)]))
                {
                    offset += 4;
                    characterCount += 1;
                }
            }
        }
        return Encoding.UTF8.GetString(outputBuffer.AsSpan()[0..offset]);
    }
}

/// <summary>
/// Writes legacy signed-save envelopes the way the removed save path did, so the read-only verifier
/// can be tested against real envelope text. PKCS#1 v1.5 over SHA-256 of the UTF-8 ship data.
/// </summary>
public static class LegacyEnvelopeWriter
{
    public static readonly string SignatureOid = CryptoConfig.MapNameToOID("SHA256") ?? "1.2.840.113549.1.1.11";

    public static byte[] Sign(RSA key, byte[] shipData)
    {
        return key.SignHash(SHA256.HashData(shipData), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    // A correctly signed envelope for shipData under key.
    public static string Signed(RSA key, string shipData, int? appraisal = null)
    {
        var data = Encoding.UTF8.GetBytes(shipData);
        return Envelope(
            Convert.ToBase64String(Sign(key, data)),
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(data),
            appraisal);
    }

    // Raw field values, so a test can garble any one of them. A null field is left out.
    public static string Envelope(string signatureB64, string publicKeyB64, string shipDataB64, int? appraisal = null)
    {
        var sb = new StringBuilder();
        sb.Append("version: \"1.0\"\n");
        if (signatureB64 != null)
            sb.Append($"signature: \"{signatureB64}\"\n");
        if (publicKeyB64 != null)
            sb.Append($"signaturePublicKey: \"{publicKeyB64}\"\n");
        sb.Append($"signatureOid: \"{SignatureOid}\"\n");
        sb.Append($"shipData: \"{shipDataB64}\"\n");
        if (appraisal.HasValue)
            sb.Append($"appraisal: \"{appraisal.Value}\"\n");
        return sb.ToString();
    }
}

[TestFixture]
public sealed class AuthenticatedShipFileTest
{
    private static RSA _key = default!;

    [OneTimeSetUp]
    public static void CreateKey()
    {
        // One keypair for the fixture: generation is the slow part and no test mutates it.
        _key = RSA.Create(2048);
    }

    [OneTimeTearDown]
    public static void DisposeKey()
    {
        _key.Dispose();
    }

    private static string MangleLineEndings(string text, int seed)
    {
        return (seed & 1) == 0 ? text.ReplaceLineEndings("\n") : text.ReplaceLineEndings("\r\n");
    }

    [Test]
    public void WhenReadFromAsciiEnvelope_ShipDataUnchanged(
        [Values(1, 10, 1000, 10000)] int testStringLength,
        [Random(int.MinValue, int.MaxValue, 10)]
        int seed
        )
    {
        var rawShipData = RandomStringGen.AsciiString(testStringLength, seed);
        var asf = AuthenticatedShipFile.FromShipFile(LegacyEnvelopeWriter.Signed(_key, rawShipData));
        var internalRawShipData = asf.ShipYamlString();
        Assert.That(internalRawShipData, Is.Not.Null);
        Assert.That(internalRawShipData, Is.EqualTo(rawShipData));
    }

    [Test]
    public void WhenReadFromUnicodeEnvelope_ShipDataUnchanged(
        [Values(1, 10, 1000)] int testStringLength,
        [Random(int.MinValue, int.MaxValue, 10)]
        int seed
    )
    {
        var rawShipData = RandomStringGen.UnicodeString(testStringLength, seed);
        var asf = AuthenticatedShipFile.FromShipFile(LegacyEnvelopeWriter.Signed(_key, rawShipData));
        var internalRawShipData = asf.ShipYamlString();
        Assert.That(internalRawShipData, Is.Not.Null);
        Assert.That(internalRawShipData, Is.EqualTo(rawShipData));
    }

    [Test]
    public void WhenReadFromAsciiEnvelope_SignatureVerifies(
        [Values(1, 10, 1000, 10000)] int testStringLength,
        [Random(int.MinValue, int.MaxValue, 10)]
        int seed
    )
    {
        var rawShipData = RandomStringGen.AsciiString(testStringLength, seed);
        var text = MangleLineEndings(LegacyEnvelopeWriter.Signed(_key, rawShipData), seed);

        var loadedAsf = AuthenticatedShipFile.FromShipFile(text);
        Assert.That(loadedAsf.IsShipSigned());
        Assert.That(loadedAsf.GetInstancePublicKeyInfo(), Is.EqualTo(_key.ExportSubjectPublicKeyInfo()));
    }

    [Test]
    public void WhenReadFromUnicodeEnvelope_SignatureVerifies(
        [Values(1, 10, 1000)] int testStringLength,
        [Random(int.MinValue, int.MaxValue, 10)]
        int seed
    )
    {
        var rawShipData = RandomStringGen.UnicodeString(testStringLength, seed);
        var text = MangleLineEndings(LegacyEnvelopeWriter.Signed(_key, rawShipData), seed);

        var loadedAsf = AuthenticatedShipFile.FromShipFile(text);
        Assert.That(loadedAsf.IsShipSigned());
    }

    [Test]
    public void TamperedShipData_IsNotSigned()
    {
        var original = Encoding.UTF8.GetBytes(RandomStringGen.AsciiString(100, seed: 7));
        var tampered = Encoding.UTF8.GetBytes(RandomStringGen.AsciiString(100, seed: 8));
        var text = LegacyEnvelopeWriter.Envelope(
            Convert.ToBase64String(LegacyEnvelopeWriter.Sign(_key, original)),
            Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(tampered));

        var loaded = AuthenticatedShipFile.FromShipFile(text);
        Assert.That(loaded.GetInstancePublicKeyInfo(), Is.Not.Null);
        Assert.That(loaded.IsShipSigned(), Is.False);
    }

    [Test]
    public void GarbledPublicKey_IsNotSignedAndDoesNotThrow()
    {
        var data = Encoding.UTF8.GetBytes("garbled key ship");
        var text = LegacyEnvelopeWriter.Envelope(
            Convert.ToBase64String(LegacyEnvelopeWriter.Sign(_key, data)),
            Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 }),
            Convert.ToBase64String(data));

        var loaded = AuthenticatedShipFile.FromShipFile(text);
        Assert.That(() => loaded.IsShipSigned(), Throws.Nothing);
        Assert.That(loaded.IsShipSigned(), Is.False);
    }

    [Test]
    public void MissingPublicKey_IsNotSigned()
    {
        var data = Encoding.UTF8.GetBytes("no key ship");
        var text = LegacyEnvelopeWriter.Envelope(
            Convert.ToBase64String(LegacyEnvelopeWriter.Sign(_key, data)),
            null,
            Convert.ToBase64String(data));

        var loaded = AuthenticatedShipFile.FromShipFile(text);
        Assert.That(loaded.GetInstancePublicKeyInfo(), Is.Null);
        Assert.That(loaded.IsShipSigned(), Is.False);
    }

    [Test]
    public void NotAnEnvelope_IsUnsignedAndPassesThrough()
    {
        // A bare grid document (the pre-envelope format) has no shipData key, so it is read as the
        // ship itself, unsigned.
        const string raw = "meta:\n  format: 7\n";
        var loaded = AuthenticatedShipFile.FromShipFile(raw);
        Assert.That(loaded.GetInstancePublicKeyInfo(), Is.Null);
        Assert.That(loaded.IsShipSigned(), Is.False);
        Assert.That(loaded.ShipYamlString(), Is.EqualTo(raw));
    }

    [Test]
    public void Appraisal_ParsesFromEnvelope(
        [Values(0, 1, 12345, int.MaxValue)] int appraisal)
    {
        var rawShipData = RandomStringGen.AsciiString(100, seed: 42);
        var loaded = AuthenticatedShipFile.FromShipFile(LegacyEnvelopeWriter.Signed(_key, rawShipData, appraisal));
        Assert.That(loaded.Appraisal, Is.EqualTo(appraisal));
        Assert.That(loaded.IsShipSigned(), Is.True);
    }

    [Test]
    public void Appraisal_AbsentParsesAsNull()
    {
        var rawShipData = RandomStringGen.AsciiString(100, seed: 1);
        var loaded = AuthenticatedShipFile.FromShipFile(LegacyEnvelopeWriter.Signed(_key, rawShipData));
        Assert.That(loaded.Appraisal, Is.Null);
        Assert.That(loaded.IsShipSigned(), Is.True);
    }
}
