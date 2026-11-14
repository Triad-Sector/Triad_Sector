using System.Security.Cryptography;
using System.Threading.Tasks;
using Content.Server._Triad.Shipyard.Persistence;
using Content.Server.Database;
using Robust.Shared.IoC;

namespace Content.IntegrationTests.Tests._Triad.Shipyard.TamperProtection;

[TestFixture]
public sealed class KeyStoreTests
{
    [Test]
    public async Task IsOwnKey_TrueForRecordedKey_FalseForForeign()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        // The signing-keys table is read-only now, so the test records a key the way a key the server
        // once signed with sits there, then seeds the own-key set explicitly so the test is
        // self-contained (in production BootstrapCachesAsync seeds it at startup).
        using var ours = RSA.Create(2048);
        await TamperTestHelpers.InsertOwnKey(server.ResolveDependency<IServerDbManager>(), ours);

        await server.WaitPost(() =>
        {
            var keyStore = IoCManager.Resolve<ITriadShipyardKeyStore>();
            keyStore.PopulateOwnKeysAsync(default).GetAwaiter().GetResult();

            var ourHash = SHA256.HashData(ours.ExportSubjectPublicKeyInfo());
            Assert.That(keyStore.IsOwnKey(ourHash), Is.True,
                "A key in the signing-keys table must be recognised as our own.");

            using var foreign = RSA.Create(2048);
            var foreignHash = SHA256.HashData(foreign.ExportSubjectPublicKeyInfo());
            Assert.That(keyStore.IsOwnKey(foreignHash), Is.False,
                "A key the server never generated must not be recognised as our own.");
        });

        await pair.CleanReturnAsync();
    }
}
