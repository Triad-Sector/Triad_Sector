#nullable enable

using System;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._NF.Market.Components;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Content.Shared._NF.Market;
using Content.Shared._Triad.ShipSize;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The durability layer's retrieve half against a real round trip: the drift gate, the pin a
    /// fallback leaves on the document it stepped past, and the timeline row for captured state that
    /// no longer restores. Each case doctors a stored document in place and writes back a checksum
    /// and size that match, so the integrity check passes and the case reaches the gate it is about.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockRetrieveDurabilityTest
    {
        private const string ItemProtoId = "SheetSteel1";
        private const string PhantomProtoId = "DrydockRetrieveTestPhantomPrototype";
        private const string CapturedKey = "CargoMarketDataComponent|MarketDataList";

        /// <summary>
        /// A current document naming a prototype that does not exist is refused with its own reason,
        /// before anything is materialized, and the row goes back to stored. The same document with
        /// that id swapped for a real rename source retrieves: the loader heals a rename, so the gate
        /// must not refuse one.
        /// </summary>
        [Test]
        public async Task ACurrentDocumentNamingMissingContentIsRefusedAndARenameIsNot()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await DrydockRoundTripTest.BuildShipAndStation(pair);
            await server.WaitPost(() => entMan.SpawnEntity(ItemProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f))));
            await pair.RunTicksSync(2);

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var ship = shipId!.Value;
            var revision = (await store.GetShipHeader(ship))!.CurrentRevision;
            var original = await ReadDocument(db, ship, revision);
            var newline = original.Contains("\r\n") ? "\r\n" : "\n";
            var group = $"- proto: {ItemProtoId}{newline}";
            Assert.That(original, Does.Contain(group), "Control: the item is a proto group in the document, or the doctoring below changes nothing.");

            await WriteDocument(db, ship, revision, original.Replace(group, $"- proto: {PhantomProtoId}{newline}"));

            var stagingBefore = 0;
            await server.WaitPost(() => stagingBefore = DrydockRoundTripTest.CountStagingMaps(entMan));
            var refusalsBefore = DrydockMetrics.DriftRefusals.Value;

            var refused = await DrydockTestHelpers.Quietly(pair, () => DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(ship, owner, station, null)));

            var header = await store.GetShipHeader(ship);
            var audit = await store.GetAudit(ship);
            var driftRows = audit.Where(a => a.Action == DrydockAuditAction.DriftRefused).ToList();
            var liveCopies = 0;
            var stagingAfter = 0;
            await server.WaitPost(() =>
            {
                liveCopies = CountLiveCopies(entMan, ship);
                stagingAfter = DrydockRoundTripTest.CountStagingMaps(entMan);
            });

            Assert.Multiple(() =>
            {
                Assert.That(refused.Result, Is.EqualTo(DrydockRetrieveResult.ContentDrift));
                Assert.That(refused.Grid, Is.Null);
                Assert.That(liveCopies, Is.Zero, "Refused before the load: no grid carries this hull.");
                Assert.That(stagingAfter, Is.EqualTo(stagingBefore), "Nor was a staging map made and left behind.");
                Assert.That(header!.State, Is.EqualTo(DrydockShipState.Stored), "The claim went back.");
                Assert.That(audit.Any(a => a.Action == DrydockAuditAction.ClaimReleased), Is.True, "Through the wrapper's release, like any refusal.");
                Assert.That(driftRows, Has.Count.EqualTo(1));
                Assert.That(driftRows.Single().Revision, Is.EqualTo(revision), "The current revision is the one refused.");
                Assert.That(driftRows.Single().ActorUserId, Is.EqualTo(owner));
                Assert.That(driftRows.Single().Reason, Does.Contain(PhantomProtoId), "The row names the id that did not resolve.");
                Assert.That(audit.Any(a => a.Action is DrydockAuditAction.Fallback or DrydockAuditAction.RevisionPinned), Is.False,
                    "A drifted current document is never answered with an older one.");
                Assert.That(DrydockMetrics.DriftRefusals.Value, Is.GreaterThan(refusalsBefore));
            });

            // The control: a real rename source whose target is an item, so it stands in for the sheet.
            var table = drydock.MigrationTable;
            var rename = table.Renamed
                .Where(kv => !table.Deleted.Contains(kv.Key)
                             && !protoMan.HasIndex<EntityPrototype>(kv.Key)
                             && protoMan.TryIndex<EntityPrototype>(kv.Value, out var target)
                             && !target.Abstract
                             && target.Components.ContainsKey("Item"))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .First();

            await WriteDocument(db, ship, revision, original.Replace(group, $"- proto: {rename.Key}{newline}"));

            var healed = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(ship, owner, station, null));
            await pair.RunTicksSync(5);

            Assert.Multiple(() =>
            {
                Assert.That(healed.Result, Is.EqualTo(DrydockRetrieveResult.Success), $"The rename {rename.Key} -> {rename.Value} is the loader's to heal.");
            });

            Assert.That((await store.GetAudit(ship)).Count(a => a.Action == DrydockAuditAction.DriftRefused), Is.EqualTo(1),
                "The healed retrieve wrote no refusal of its own.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A current document that passes its checksum and the drift gate and still cannot be used
        /// falls back to the revision before it, and is pinned so the stores that follow cannot prune
        /// the newer state. The load failure is a header claiming the document is a map rather than a
        /// grid, which the loader refuses and neither the parse nor the drift read looks at. Taking
        /// the shuttle component out does not work: <c>ShuttleSystem.OnGridInit</c> puts it back on
        /// every grid the load initializes.
        /// </summary>
        [Test]
        public async Task ACurrentDocumentThatWillNotLoadIsPinnedAndTheOlderOneRetrieves()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await DrydockRoundTripTest.BuildShipAndStation(pair);

            var (first, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(first, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);
            var ship = shipId!.Value;

            var out1 = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(ship, owner, station, null));
            Assert.That(out1.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            var (second, _) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(out1.Grid!.Value, owner, null));
            Assert.That(second, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var current = (await store.GetShipHeader(ship))!.CurrentRevision;
            var older = (await store.ListRetrievableRevisions(ship)).First(r => r < current);

            var document = await ReadDocument(db, ship, current);
            var doctored = document.Replace("  category: Grid", "  category: Map");
            Assert.That(doctored, Is.Not.EqualTo(document), "Control: the current document declared itself a grid.");
            await WriteDocument(db, ship, current, doctored);

            var fallbacksBefore = DrydockMetrics.RetrieveFallbacks.Value;
            var retrieved = await DrydockTestHelpers.Quietly(pair, () => DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(ship, owner, station, null)));
            await pair.RunTicksSync(5);

            var pinned = await PinnedRevisions(db, ship);
            var audit = await store.GetAudit(ship);
            var fallback = audit.Where(a => a.Action == DrydockAuditAction.Fallback).ToList();
            var pinRows = audit.Where(a => a.Action == DrydockAuditAction.RevisionPinned).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success), "The older revision is a ship.");
                Assert.That(pinned, Is.EqualTo(new[] { current }), "The stepped-past revision is pinned, and only it.");
                Assert.That(pinRows, Has.Count.EqualTo(1));
                Assert.That(pinRows.Single().Revision, Is.EqualTo(current));
                Assert.That(pinRows.Single().ActorUserId, Is.Null, "The system pinned it.");
                Assert.That(pinRows.Single().Reason, Does.Contain("a retrieve stepped past it"));
                Assert.That(fallback, Has.Count.EqualTo(1));
                Assert.That(fallback.Single().Revision, Is.EqualTo(older), "The fallback row names the revision that came back.");
                Assert.That(DrydockMetrics.RetrieveFallbacks.Value, Is.GreaterThan(fallbacksBefore));
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// An older document reached only because the current one is corrupt, and refused for drift,
        /// is stepped past and pinned rather than ending the walk; when nothing older loads either, the
        /// refusal names drift, not an unreadable ship.
        /// </summary>
        [Test]
        public async Task ALadderThatRunsOutOnADriftedOlderDocumentRefusesForDrift()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await DrydockRoundTripTest.BuildShipAndStation(pair);
            await server.WaitPost(() => entMan.SpawnEntity(ItemProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f))));
            await pair.RunTicksSync(2);

            var (first, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(first, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);
            var ship = shipId!.Value;

            var out1 = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(ship, owner, station, null));
            Assert.That(out1.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            var (second, _) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(out1.Grid!.Value, owner, null));
            Assert.That(second, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var current = (await store.GetShipHeader(ship))!.CurrentRevision;
            var older = (await store.ListRetrievableRevisions(ship)).First(r => r < current);

            var document = await ReadDocument(db, ship, older);
            var newline = document.Contains("\r\n") ? "\r\n" : "\n";
            var group = $"- proto: {ItemProtoId}{newline}";
            Assert.That(document, Does.Contain(group), "Control: the older document carries the item group.");
            await WriteDocument(db, ship, older, document.Replace(group, $"- proto: {PhantomProtoId}{newline}"));

            // The current document's bytes, broken outright: it fails its checksum before any gate.
            await db.RunTriadDbCommand(async (context, token) =>
                await context.DrydockBlob.Where(b => b.ShipGuid == ship && b.Revision == current)
                    .ExecuteUpdateAsync(set => set.SetProperty(b => b.Blob, new byte[] { 1, 2, 3 }), token), CancellationToken.None);

            var refused = await DrydockTestHelpers.Quietly(pair, () => DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(ship, owner, station, null)));

            var header = await store.GetShipHeader(ship);
            var audit = await store.GetAudit(ship);
            var driftRows = audit.Where(a => a.Action == DrydockAuditAction.DriftRefused).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(refused.Result, Is.EqualTo(DrydockRetrieveResult.ContentDrift));
                Assert.That(header!.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(driftRows, Has.Count.EqualTo(1));
                Assert.That(driftRows.Single().Revision, Is.EqualTo(older), "The refused document is the older one.");
                Assert.That(driftRows.Single().Reason, Does.Contain(PhantomProtoId));
            });

            Assert.That(await PinnedRevisions(db, ship), Is.EqualTo(new[] { older }),
                "The drifted document was stepped past and pinned; the corrupt one was not.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A captured key nothing answers to any more still lets the ship come back, and the loss goes
        /// on the timeline naming the key rather than only into a log line.
        /// </summary>
        [Test]
        public async Task ACapturedKeyThatNoLongerResolvesIsRecorded()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await DrydockRoundTripTest.BuildShipAndStation(pair);

            await server.WaitPost(() =>
            {
                var market = entMan.EnsureComponent<CargoMarketDataComponent>(shipGrid);
#pragma warning disable RA0002
                market.MarketDataList.Add(new MarketData(ItemProtoId, null, quantity: 7, price: 42.5));
#pragma warning restore RA0002
            });
            await pair.RunTicksSync(5);

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);
            var ship = shipId!.Value;

            var revision = (await store.GetShipHeader(ship))!.CurrentRevision;
            var document = await ReadDocument(db, ship, revision);
            Assert.That(document, Does.Contain(CapturedKey), "Control: the market list was captured under this key.");

            const string renamed = CapturedKey + "Renamed";
            await WriteDocument(db, ship, revision, document.Replace(CapturedKey, renamed));

            var skippedBefore = DrydockMetrics.SkippedStateKeys.WithLabels("captured").Value;
            var retrieved = await DrydockTestHelpers.Quietly(pair, () => DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(ship, owner, station, null)));
            await pair.RunTicksSync(5);

            var rows = (await store.GetAudit(ship)).Where(a => a.Action == DrydockAuditAction.StateSkipped).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success), "A skipped key never costs the ship.");
                Assert.That(rows, Has.Count.EqualTo(1), "One row for the captured sidecar; the appearance sidecar skipped nothing.");
                Assert.That(rows.Single().Reason, Does.StartWith("captured: "));
                Assert.That(rows.Single().Reason, Does.Contain(renamed), "The row names the key.");
                Assert.That(rows.Single().Revision, Is.EqualTo(revision));
                Assert.That(rows.Single().ActorUserId, Is.EqualTo(owner));
                Assert.That(DrydockMetrics.SkippedStateKeys.WithLabels("captured").Value, Is.GreaterThan(skippedBefore));
            });

            await pair.CleanReturnAsync();
        }

        private static int CountLiveCopies(IEntityManager entMan, Guid ship)
        {
            var count = 0;
            var query = entMan.AllEntityQueryEnumerator<DrydockIdentityComponent>();
            while (query.MoveNext(out _, out var identity))
            {
                if (identity.ShipId == ship)
                    count++;
            }

            return count;
        }

        private static Task<string> ReadDocument(IServerDbManager db, Guid ship, int revision)
        {
            return db.RunTriadDbCommand(async (context, token) =>
            {
                var blob = await context.DrydockBlob.AsNoTracking()
                    .Where(b => b.ShipGuid == ship && b.Revision == revision)
                    .Select(b => b.Blob)
                    .SingleAsync(token);

                return Encoding.UTF8.GetString(DrydockSystem.DecompressZstd(blob));
            }, CancellationToken.None);
        }

        /// <summary>
        /// Writes a document back as the store would have filed it: compressed, with the revision's
        /// checksum and size over the new bytes, so the retrieve's integrity check passes.
        /// </summary>
        private static Task WriteDocument(IServerDbManager db, Guid ship, int revision, string yaml)
        {
            var bytes = Encoding.UTF8.GetBytes(yaml);
            var checksum = SHA256.HashData(bytes);
            var payload = DrydockSystem.CompressZstd(bytes);

            return db.RunTriadDbCommand(async (context, token) =>
            {
                await context.DrydockBlob
                    .Where(b => b.ShipGuid == ship && b.Revision == revision)
                    .ExecuteUpdateAsync(set => set.SetProperty(b => b.Blob, payload), token);

                await context.DrydockRevision
                    .Where(r => r.ShipGuid == ship && r.Revision == revision)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(r => r.Checksum, checksum)
                        .SetProperty(r => r.SizeBytes, bytes.Length), token);
            }, CancellationToken.None);
        }

        private static Task<int[]> PinnedRevisions(IServerDbManager db, Guid ship)
        {
            return db.RunTriadDbCommand(async (context, token) => await context.DrydockRevision.AsNoTracking()
                .Where(r => r.ShipGuid == ship && r.Pinned)
                .Select(r => r.Revision)
                .OrderBy(r => r)
                .ToArrayAsync(token), CancellationToken.None);
        }
    }
}
