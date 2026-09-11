using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Shared._Triad.Shipyard.Save;
using Robust.Server.Player;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._Triad.Shipyard.TamperProtection;

[TestFixture]
public sealed class MigrateShipFileMessageTests
{
    /// <summary>
    /// Verifies that a <see cref="MigrateShipFileMessage"/> raised by the server to a connected
    /// client session is received by the client and processed by
    /// <c>Content.Client.Shuttles.Save.ShipFileManagementSystem.OnMigrateShipFile</c>, which
    /// writes the new envelope contents to the client's UserData at the requested path.
    /// </summary>
    [Test]
    public async Task MigrateShipFileMessage_RoundTrips()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Dirty = true,
            DummyTicker = false,
            Connected = true,
        });
        var server = pair.Server;
        var client = pair.Client;
        await server.WaitIdleAsync();
        await client.WaitIdleAsync();

        // Path is relative to the client's UserData root. The client handler normalizes
        // separators and prepends a leading slash if missing.
        const string path = "/Exports/tamper-test-migrated.yml";
        const string body = "version: 1.0\nappraisal: 42\nshipData: BASE64\n";
        var resPath = new ResPath(path);

        // Pre-clean: if a prior pooled run left this file around, drop it so the assertion
        // is actually exercising the new write.
        await client.WaitPost(() =>
        {
            var res = IoCManager.Resolve<IResourceManager>();
            if (res.UserData.Exists(resPath))
                res.UserData.Delete(resPath);
        });

        // The client only accepts a server-sent migrate for a path it previously vouched for via
        // MarkShipPathAsDeletable (F5 anti-path-traversal gate, added after this test). In the real
        // flow the client marks the path when it sends the ship to the server to load; simulate that
        // precondition here so the handler reaches its happy path.
        await client.WaitPost(() =>
        {
            Client._Triad.Shipyard.Save.ShipFileManagementSystem.MarkShipPathAsDeletable(path);
        });

        // Raise the network event from the server to the connected client's channel.
        // IEntityNetworkManager.SendSystemNetworkMessage(EntityEventArgs, INetChannel) is the
        // public, non-EntitySystem entry point for "send this event to one specific client".
        await server.WaitPost(() =>
        {
            var playerMgr = IoCManager.Resolve<IPlayerManager>();
            var session = playerMgr.Sessions.FirstOrDefault();
            Assert.That(session, Is.Not.Null, "Need at least one connected session for this test.");

            var entMan = IoCManager.Resolve<IEntityManager>();
            entMan.EntityNetManager!.SendSystemNetworkMessage(
                new MigrateShipFileMessage(path, body),
                session!.Channel);
        });

        // Let the message cross the wire and the client subscriber run.
        await pair.RunTicksSync(10);
        await server.WaitIdleAsync();
        await client.WaitIdleAsync();

        await client.WaitAssertion(() =>
        {
            var res = IoCManager.Resolve<IResourceManager>();
            Assert.That(res.UserData.Exists(resPath), Is.True,
                "Client handler must have written the migrated envelope to UserData.");

            using var stream = res.UserData.OpenRead(resPath);
            using var reader = new StreamReader(stream);
            var contents = reader.ReadToEnd();
            Assert.That(contents, Is.EqualTo(body),
                "File contents must match the body delivered in the network event.");
        });

        // Cleanup so the next pooled consumer doesn't see this file.
        await client.WaitPost(() =>
        {
            var res = IoCManager.Resolve<IResourceManager>();
            if (res.UserData.Exists(resPath))
                res.UserData.Delete(resPath);
        });

        await pair.CleanReturnAsync();
    }
}
