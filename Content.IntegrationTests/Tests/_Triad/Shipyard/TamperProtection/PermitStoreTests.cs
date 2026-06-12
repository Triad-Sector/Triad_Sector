using System;
using System.Threading.Tasks;
using Content.Server._Triad.Shipyard.Persistence;
using Robust.Shared.IoC;

namespace Content.IntegrationTests.Tests._Triad.Shipyard.TamperProtection;

[TestFixture]
public sealed class PermitStoreTests
{
    [Test]
    public async Task PerPlayerPermit_GrantedThenRevoked()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        await server.WaitIdleAsync();
        var player = Guid.NewGuid();

        await server.WaitAssertion(() =>
        {
            var permits = IoCManager.Resolve<ITriadShipyardPermitStore>();
            // Seed the cache so HasPermitFor answers from memory (in production BootstrapKeyAsync
            // does this at startup).
            permits.PopulateAsync(default).GetAwaiter().GetResult();
            Assert.That(permits.HasPermitFor(player), Is.False);

            permits.GrantAsync(player, Guid.NewGuid(), DateTime.UtcNow, "straggler", default).GetAwaiter().GetResult();
            Assert.That(permits.HasPermitFor(player), Is.True, "A granted per-player permit must be active.");

            var revoked = permits.RevokeAsync(player, default).GetAwaiter().GetResult();
            Assert.That(revoked, Is.True);
            Assert.That(permits.HasPermitFor(player), Is.False, "A revoked permit must no longer be active.");
        });

        await pair.CleanReturnAsync();
    }
}
