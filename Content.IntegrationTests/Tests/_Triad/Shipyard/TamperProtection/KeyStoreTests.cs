using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Content.Server._Triad.Shipyard.Persistence;
using Robust.Shared.IoC;

namespace Content.IntegrationTests.Tests._Triad.Shipyard.TamperProtection;

[TestFixture]
public sealed class KeyStoreTests
{
    [Test]
    public async Task KeyStoreIsStableWithinSession()
    {
        byte[] firstKey = default!;
        await using (var firstPair = await PoolManager.GetServerClient())
        {
            var firstServer = firstPair.Server;
            await firstServer.WaitIdleAsync();

            await firstServer.WaitPost(() =>
            {
                var keyStore = IoCManager.Resolve<ITriadShipyardKeyStore>();
                firstKey = keyStore.GetOrCreateActivePrivateKeyAsync(default).GetAwaiter().GetResult();
            });

            Assert.That(firstKey, Is.Not.Null);
            await firstPair.CleanReturnAsync();
        }

        // Reacquire the pool. With Sqlite in-memory this resets state, so this test really
        // verifies "on second call the key is stable within the same DB," which is the
        // useful guarantee for Postgres. For an actual cross-restart proof, swap to a
        // file-backed Sqlite or run against Postgres.
        await using var secondPair = await PoolManager.GetServerClient();
        var secondServer = secondPair.Server;
        await secondServer.WaitIdleAsync();

        byte[] secondCallKey = default!;
        byte[] thirdCallKey = default!;
        await secondServer.WaitPost(() =>
        {
            var keyStore = IoCManager.Resolve<ITriadShipyardKeyStore>();
            secondCallKey = keyStore.GetOrCreateActivePrivateKeyAsync(default).GetAwaiter().GetResult();
            thirdCallKey = keyStore.GetOrCreateActivePrivateKeyAsync(default).GetAwaiter().GetResult();
        });

        Assert.That(firstKey.SequenceEqual(secondCallKey), Is.True,
            "First-pair and second-pair active keys must match within a single DB session.");
        Assert.That(secondCallKey.SequenceEqual(thirdCallKey), Is.True,
            "Two consecutive calls must return the same active private key.");
        await secondPair.CleanReturnAsync();
    }

    [Test]
    public async Task IsOwnKey_TrueForActive_FalseForForeign()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        await server.WaitPost(() =>
        {
            var keyStore = IoCManager.Resolve<ITriadShipyardKeyStore>();
            // Ensure an active key exists, then seed the own-key set explicitly so the test is
            // self-contained (in production BootstrapKeyAsync seeds it at startup).
            keyStore.GetOrCreateActivePrivateKeyAsync(default).GetAwaiter().GetResult();
            keyStore.PopulateOwnKeysAsync(default).GetAwaiter().GetResult();

            var active = keyStore.GetActiveAsync(default).GetAwaiter().GetResult();
            Assert.That(active, Is.Not.Null);

            var ourHash = SHA256.HashData(active!.PublicKey);
            Assert.That(keyStore.IsOwnKey(ourHash), Is.True,
                "The active signing key must be recognised as our own.");

            using var foreign = RSA.Create(2048);
            var foreignHash = SHA256.HashData(foreign.ExportSubjectPublicKeyInfo());
            Assert.That(keyStore.IsOwnKey(foreignHash), Is.False,
                "A key the server never generated must not be recognised as our own.");
        });

        await pair.CleanReturnAsync();
    }
}
