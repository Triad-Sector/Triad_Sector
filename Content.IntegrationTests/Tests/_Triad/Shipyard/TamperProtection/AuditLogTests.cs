using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Content.Server._Triad.Shipyard;
using Content.Server._Triad.Shipyard.Persistence;
using Content.Server.Database;
using Robust.Shared.IoC;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._Triad.Shipyard.TamperProtection;

[TestFixture]
public sealed class AuditLogTests
{
    [Test]
    public async Task AuditLog_RecordsAllEventTypes()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var allTypes = new[]
        {
            TriadShipyardEventType.SaveSigned,
            TriadShipyardEventType.LoadVerifiedTrusted,
            TriadShipyardEventType.LoadVerifiedUntrusted,
            TriadShipyardEventType.LoadUnsigned,
            TriadShipyardEventType.LoadInvalidSignature,
            TriadShipyardEventType.LoadMigrated,
            TriadShipyardEventType.LoadRejected,
            TriadShipyardEventType.LoadRejectedUnsigned,
            TriadShipyardEventType.LoadRejectedInvalidSignature,
            TriadShipyardEventType.LoadRejectedForeignKey,
        };

        var player = new NetUserId(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        var hash = new byte[] { 1, 2, 3, 4 };

        await server.WaitPost(() =>
        {
            var log = IoCManager.Resolve<ITriadShipyardAuditLog>();
            foreach (var t in allTypes)
            {
                log.RecordAsync(new TriadShipyardAuditEvent
                {
                    At = DateTime.UtcNow,
                    EventType = t,
                    PlayerUserId = player.UserId,
                    PlayerName = "tester",
                    ShipName = "Ship-" + t,
                    ShipHash = hash,
                }, default).GetAwaiter().GetResult();
            }
        });

        PagedResult<TriadShipyardAuditEvent> page = default!;
        await server.WaitPost(() =>
        {
            var log = IoCManager.Resolve<ITriadShipyardAuditLog>();
            page = log.QueryAsync(new AuditFilter(null, null, player.UserId, null, null), 0, 100, default).GetAwaiter().GetResult();
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(page.TotalCount, Is.EqualTo(allTypes.Length));
            var seen = new HashSet<TriadShipyardEventType>();
            foreach (var row in page.Rows)
                seen.Add(row.EventType);
            foreach (var t in allTypes)
                Assert.That(seen, Does.Contain(t));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AuditLog_RecordsEventWithoutShipHash()
    {
        // Regression: rejected-load and permit-action rows carry no ship hash, and ship_hash is a
        // NOT NULL column. An event that never sets ShipHash must still persist (the entity defaults
        // it to empty) instead of throwing a constraint violation that rolls back the whole batch.
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var player = new NetUserId(Guid.Parse("dddddddd-eeee-ffff-0000-111111111111"));

        await server.WaitPost(() =>
        {
            var log = IoCManager.Resolve<ITriadShipyardAuditLog>();
            // ShipHash deliberately not set — mirrors RecordRejectedLoadAsync / RecordPermitAction.
            log.RecordAsync(new TriadShipyardAuditEvent
            {
                At = DateTime.UtcNow,
                EventType = TriadShipyardEventType.LoadRejected,
                PlayerUserId = player.UserId,
                PlayerName = "rejected-tester",
            }, default).GetAwaiter().GetResult();
        });

        PagedResult<TriadShipyardAuditEvent> page = default!;
        await server.WaitPost(() =>
        {
            var log = IoCManager.Resolve<ITriadShipyardAuditLog>();
            page = log.QueryAsync(new AuditFilter(null, null, player.UserId, null, null), 0, 100, default)
                .GetAwaiter().GetResult();
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(page.TotalCount, Is.EqualTo(1));
            Assert.That(page.Rows[0].ShipHash, Is.Not.Null);
            Assert.That(page.Rows[0].ShipHash, Is.Empty);
        });

        await pair.CleanReturnAsync();
    }
}
