#nullable enable

using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Content.Shared._Triad.ShipSize;
using Microsoft.EntityFrameworkCore;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The durability layer's store half against a real database: what a pin protects from pruning,
    /// the two-document floor under keep-N, and a re-bake that becomes current only while the ship is
    /// still stored on the revision it was derived from. Every ship and player is freshly minted, so
    /// assertions are on this test's own ids and never on table-wide counts.
    /// </summary>
    [TestFixture]
    public sealed class DrydockDurabilityStoreTest
    {
        /// <summary>
        /// A pinned document survives any number of stores past keep-N, the pin and its clearing each
        /// write one timeline row, and once cleared the next store prunes it. A sibling ship filed the
        /// same way without a pin is the control that pruning ran at all.
        /// </summary>
        [Test]
        public async Task APinnedDocumentOutlivesKeepNUntilItIsUnpinned()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var admin = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var pinnedShip = Guid.NewGuid();
            var control = Guid.NewGuid();
            await store.FileRevision(Request(pinnedShip, owner, "Kestrel"), Doc(1), keepBlobs: 2);
            await store.FileRevision(Request(control, owner, "Harrier"), Doc(1), keepBlobs: 2);

            Assert.That(await store.TryPinRevision(pinnedShip, 1, admin, null, "stepped past by a fallback"), Is.EqualTo(DrydockPinResult.Success));
            Assert.That(await store.TryPinRevision(pinnedShip, 1, admin, null, "again"), Is.EqualTo(DrydockPinResult.AlreadyInState),
                "Pinning a pinned revision is not a change and must not log one.");

            var pinRows = (await store.GetAudit(pinnedShip)).Where(a => a.Action == DrydockAuditAction.RevisionPinned).ToList();
            Assert.That(pinRows, Has.Count.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(pinRows[0].Revision, Is.EqualTo(1));
                Assert.That(pinRows[0].ActorUserId, Is.EqualTo(admin));
                Assert.That(pinRows[0].SubjectUserId, Is.EqualTo(owner));
                Assert.That(pinRows[0].ShipName, Is.EqualTo("Kestrel"));
                Assert.That(pinRows[0].Reason, Is.EqualTo("pinned revision 1: stepped past by a fallback"));
            });

            // Five stores past it with keep-2: revisions 2 to 6.
            for (var i = 2; i <= 6; i++)
            {
                await store.FileRevision(Request(pinnedShip, owner, "Kestrel"), Doc(i), keepBlobs: 2);
                await store.FileRevision(Request(control, owner, "Harrier"), Doc(i), keepBlobs: 2);
            }

            var pinnedBlobs = await BlobRevisions(db, pinnedShip);
            var controlBlobs = await BlobRevisions(db, control);
            var retrievable = await store.ListRetrievableRevisions(pinnedShip);
            Assert.Multiple(() =>
            {
                Assert.That(pinnedBlobs, Is.EqualTo(new[] { 1, 5, 6 }),
                    "Keep-N took everything between the pin and the window, and never the pin.");
                Assert.That(controlBlobs, Is.EqualTo(new[] { 5, 6 }),
                    "Control: the same five stores without a pin prune revision 1.");
                Assert.That(retrievable, Is.EqualTo(new[] { 6, 5, 1 }),
                    "What a retrieve can fall back to is every document that exists, newest first, the pin included.");
            });

            var pinnedLoad = await store.LoadRevision(pinnedShip, 1);
            Assert.That(pinnedLoad?.Blob, Is.EqualTo(Doc(1)), "The pinned document is intact, not just present.");

            Assert.That(await store.TryUnpinRevision(pinnedShip, 1, admin, null, "re-baked"), Is.EqualTo(DrydockPinResult.Success));
            Assert.That(await store.TryUnpinRevision(pinnedShip, 1, admin, null, "again"), Is.EqualTo(DrydockPinResult.AlreadyInState));

            var unpinRows = (await store.GetAudit(pinnedShip)).Where(a => a.Action == DrydockAuditAction.RevisionUnpinned).ToList();
            Assert.That(unpinRows, Has.Count.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(unpinRows[0].Revision, Is.EqualTo(1));
                Assert.That(unpinRows[0].ActorUserId, Is.EqualTo(admin));
                Assert.That(unpinRows[0].Reason, Is.EqualTo("unpinned revision 1: re-baked"));
            });

            Assert.That(await BlobRevisions(db, pinnedShip), Is.EqualTo(new[] { 1, 5, 6 }), "Unpinning deletes nothing by itself.");

            await store.FileRevision(Request(pinnedShip, owner, "Kestrel"), Doc(7), keepBlobs: 2);
            Assert.That(await BlobRevisions(db, pinnedShip), Is.EqualTo(new[] { 6, 7 }), "The next store after the unpin prunes it.");

            // The refusals, none of which writes a row.
            Assert.That(await store.TryPinRevision(pinnedShip, 1, admin, null, null), Is.EqualTo(DrydockPinResult.NotFound),
                "A revision whose document is gone has nothing left to protect.");
            Assert.That(await store.TryPinRevision(pinnedShip, 99, admin, null, null), Is.EqualTo(DrydockPinResult.NotFound));
            Assert.That(await store.TryPinRevision(Guid.NewGuid(), 1, admin, null, null), Is.EqualTo(DrydockPinResult.NotFound));
            Assert.That(await store.TryUnpinRevision(pinnedShip, 99, admin, null, null), Is.EqualTo(DrydockPinResult.NotFound));

            Assert.That((await store.GetAudit(pinnedShip)).Count(a => a.Action is DrydockAuditAction.RevisionPinned or DrydockAuditAction.RevisionUnpinned),
                Is.EqualTo(2), "One pin and one unpin, and no row for any refusal.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Keep one is floored at two: the current document and one step back for the retrieve ladder.
        /// </summary>
        [Test]
        public async Task KeepingOneIsFlooredAtTwoDocuments()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var ship = Guid.NewGuid();
            await store.FileRevision(Request(ship, owner, "Kestrel"), Doc(1), keepBlobs: 1);
            Assert.That(await BlobRevisions(db, ship), Is.EqualTo(new[] { 1 }));

            await store.FileRevision(Request(ship, owner, "Kestrel"), Doc(2), keepBlobs: 1);
            Assert.That(await BlobRevisions(db, ship), Is.EqualTo(new[] { 1, 2 }), "Two exist, so two stay.");

            await store.FileRevision(Request(ship, owner, "Kestrel"), Doc(3), keepBlobs: 1);
            await store.FileRevision(Request(ship, owner, "Kestrel"), Doc(4), keepBlobs: 1);
            Assert.That(await BlobRevisions(db, ship), Is.EqualTo(new[] { 3, 4 }), "Pruning still runs; it stops at two.");
            Assert.That(await store.ListRetrievableRevisions(ship), Is.EqualTo(new[] { 4, 3 }));

            // Keep three is unaffected by the floor: control that the floor raises only, never lowers.
            await store.FileRevision(Request(ship, owner, "Kestrel"), Doc(5), keepBlobs: 3);
            await store.FileRevision(Request(ship, owner, "Kestrel"), Doc(6), keepBlobs: 3);
            Assert.That(await BlobRevisions(db, ship), Is.EqualTo(new[] { 4, 5, 6 }));

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A ship already down to one document, which is the shape the tight prune left before the
        /// floor existed, keeps it through the next store: the window counts the documents that exist,
        /// not revision numbers, so a gap below the current revision cannot pull the edge past it.
        /// </summary>
        [Test]
        public async Task AShipsOnlyRemainingDocumentIsNeverPruned()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var ship = Guid.NewGuid();
            for (var i = 1; i <= 3; i++)
                await store.FileRevision(Request(ship, owner, "Kestrel"), Doc(i), keepBlobs: 0);

            // Take revisions 1 and 2's documents directly, leaving revision 3's as the only one.
            await db.RunTriadDbCommand(async (context, token) =>
                await context.DrydockBlob.Where(b => b.ShipGuid == ship && b.Revision < 3).ExecuteDeleteAsync(token), CancellationToken.None);
            Assert.That(await BlobRevisions(db, ship), Is.EqualTo(new[] { 3 }), "Control: the ship is down to one document.");

            await store.FileRevision(Request(ship, owner, "Kestrel"), Doc(4), keepBlobs: 1);
            Assert.That(await BlobRevisions(db, ship), Is.EqualTo(new[] { 3, 4 }),
                "The only document the ship had is kept alongside the new one.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A re-bake files a system revision derived from the ship's current one and becomes current,
        /// with no actor, no round, the source's appraisal and the display cache untouched. When
        /// anything else became current first, it files nothing at all.
        /// </summary>
        [Test]
        public async Task ARebakeBecomesCurrentOnlyWhileTheShipIsStillOnItsSource()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var ship = Guid.NewGuid();
            var filed = await store.FileRevision(Request(ship, owner, "Kestrel"), Doc(1), keepBlobs: 2);
            Assert.That(filed.Outcome, Is.EqualTo(DrydockBerthResult.Success));

            var rebaked = Encoding.UTF8.GetBytes("re-baked document");
            var result = await store.FileRebakeRevision(Rebake(ship, sourceRevision: 1), rebaked, keepBlobs: 2);
            Assert.Multiple(() =>
            {
                Assert.That(result.Outcome, Is.EqualTo(DrydockRebakeResult.Success));
                Assert.That(result.Revision, Is.EqualTo(2));
            });

            var current = await store.LoadCurrent(ship);
            Assert.That(current, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(current!.Ship.CurrentRevision, Is.EqualTo(2), "The pointer advanced to the re-bake.");
                Assert.That(current.Ship.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(current.Ship.BerthId, Is.EqualTo(filed.BerthId), "A re-bake never touches the berth.");
                Assert.That(current.Ship.ShipName, Is.EqualTo("Kestrel"));
                Assert.That(current.Blob, Is.EqualTo(rebaked));
                Assert.That(current.Revision.Kind, Is.EqualTo(DrydockRevisionKind.SystemRebake));
                Assert.That(current.Revision.DerivedFromRevision, Is.EqualTo(1));
                Assert.That(current.Revision.RebakeVersion, Is.EqualTo(1));
                Assert.That(current.Revision.ActorUserId, Is.Null);
                Assert.That(current.Revision.CreatedRoundId, Is.Null);
                Assert.That(current.Revision.AppraisedValue, Is.EqualTo(24000), "Copied from the source: a stored hull has nothing left to appraise.");
                Assert.That(current.Revision.ProtoFingerprint, Is.EqualTo(new byte[] { 11, 12 }));
                Assert.That(current.Revision.CapturedKeyHash, Is.EqualTo(new byte[] { 13, 14 }));
                Assert.That(current.Revision.Checksum, Is.EqualTo(new byte[] { 15, 16 }));
                Assert.That(current.Revision.SizeBytes, Is.EqualTo(17));
                Assert.That(current.Revision.EngineFormatVer, Is.EqualTo(8));
                Assert.That(current.Revision.Manifest, Is.EqualTo("{\"v\":1,\"e\":[\"rebaked\"]}"));
            });

            var rebakeRow = (await store.GetAudit(ship))[^1];
            Assert.Multiple(() =>
            {
                Assert.That(rebakeRow.Action, Is.EqualTo(DrydockAuditAction.Rebake));
                Assert.That(rebakeRow.ActorUserId, Is.Null);
                Assert.That(rebakeRow.Revision, Is.EqualTo(2));
                Assert.That(rebakeRow.SubjectUserId, Is.EqualTo(owner));
                Assert.That(rebakeRow.Reason, Does.Contain("revision 1"), "The timeline names what it was derived from.");
            });

            // A player store lands between the worker's read of revision 2 and its write.
            var stored = await store.FileRevision(Request(ship, owner, "Kestrel"), Doc(3), keepBlobs: 2);
            Assert.That(stored.Revision, Is.EqualTo(3));

            var stale = await store.FileRebakeRevision(Rebake(ship, sourceRevision: 2), rebaked, keepBlobs: 2);
            Assert.That(stale.Outcome, Is.EqualTo(DrydockRebakeResult.StaleSource));

            var (revisions, blobs) = await RevisionShape(db, ship);
            var afterStale = await store.LoadCurrent(ship);
            var rebakeRows = (await store.GetAudit(ship)).Count(a => a.Action == DrydockAuditAction.Rebake);
            Assert.Multiple(() =>
            {
                Assert.That(revisions, Is.EqualTo(new[] { 1, 2, 3 }), "A stale re-bake files no revision.");
                Assert.That(blobs, Is.EqualTo(new[] { 2, 3 }), "Nor a document, nor a prune.");
                Assert.That(afterStale!.Blob, Is.EqualTo(Doc(3)), "The player's store is still what a retrieve reads.");
                Assert.That(rebakeRows, Is.EqualTo(1), "Nor a timeline row.");
            });

            // Control: derived from the revision that is actually current, the same call files.
            var fresh = await store.FileRebakeRevision(Rebake(ship, sourceRevision: 3), rebaked, keepBlobs: 2);
            Assert.Multiple(() =>
            {
                Assert.That(fresh.Outcome, Is.EqualTo(DrydockRebakeResult.Success));
                Assert.That(fresh.Revision, Is.EqualTo(4));
            });

            Assert.That((await store.FileRebakeRevision(Rebake(ship, sourceRevision: 99), rebaked, keepBlobs: 2)).Outcome,
                Is.EqualTo(DrydockRebakeResult.NotFound));
            Assert.That((await store.FileRebakeRevision(Rebake(Guid.NewGuid(), sourceRevision: 1), rebaked, keepBlobs: 2)).Outcome,
                Is.EqualTo(DrydockRebakeResult.NotFound));

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A ship that is out in the world is not re-baked, even from its current revision. Released
        /// back to storage, the same call files: the control that state was the only thing refusing.
        /// </summary>
        [Test]
        public async Task ARebakeRefusesAShipThatIsCheckedOut()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var ship = Guid.NewGuid();
            await store.FileRevision(Request(ship, owner, "Kestrel"), Doc(1), keepBlobs: 2);
            Assert.That(await store.TrySetState(ship, DrydockShipState.Stored, DrydockShipState.CheckedOut, DrydockAuditAction.Retrieve, owner, null, null), Is.True);

            var rebaked = Encoding.UTF8.GetBytes("re-baked document");
            var refused = await store.FileRebakeRevision(Rebake(ship, sourceRevision: 1), rebaked, keepBlobs: 2);
            Assert.That(refused.Outcome, Is.EqualTo(DrydockRebakeResult.WrongState));

            var (revisions, blobs) = await RevisionShape(db, ship);
            var header = await store.GetShipHeader(ship);
            Assert.Multiple(() =>
            {
                Assert.That(revisions, Is.EqualTo(new[] { 1 }), "Refused means nothing filed.");
                Assert.That(blobs, Is.EqualTo(new[] { 1 }));
                Assert.That(header!.CurrentRevision, Is.EqualTo(1));
                Assert.That(header.State, Is.EqualTo(DrydockShipState.CheckedOut), "And the state untouched.");
            });

            Assert.That(await store.TrySetState(ship, DrydockShipState.CheckedOut, DrydockShipState.Stored, DrydockAuditAction.ClaimReleased, null, null, "test"), Is.True);
            Assert.That((await store.FileRebakeRevision(Rebake(ship, sourceRevision: 1), rebaked, keepBlobs: 2)).Outcome,
                Is.EqualTo(DrydockRebakeResult.Success), "Control: stored again, the same re-bake files.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The ordinary filing path refuses a re-bake outright, so the unconditional pointer read it
        /// does cannot be used to file one by mistake.
        /// </summary>
        [Test]
        public async Task TheOrdinaryFilingPathRefusesARebake()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();

            var request = new DrydockRevisionRequest
            {
                ShipGuid = Guid.NewGuid(),
                OwnerUserId = Guid.NewGuid(),
                ShipName = "Kestrel",
                Kind = DrydockRevisionKind.SystemRebake,
                EngineFormatVer = 7,
                ProtoFingerprint = new byte[] { 1 },
                CapturedKeyHash = new byte[] { 1 },
                Checksum = new byte[] { 1 },
                SizeBytes = 1,
                Manifest = "{}",
            };

            Assert.ThrowsAsync<ArgumentException>(async () => await store.FileRevision(request, Doc(1), keepBlobs: 2));

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A promote files a copy with no live grid behind it, so the copy has to carry the source's
        /// appraisal or a sale or impound of the promoted ship quotes nothing.
        /// </summary>
        [Test]
        public async Task APromoteCarriesItsSourcesAppraisal()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var ship = Guid.NewGuid();
            await store.FileRevision(Request(ship, owner, "Kestrel"), Doc(1), keepBlobs: 3);
            await store.FileRevision(Request(ship, owner, "Kestrel", appraisal: 5000), Doc(2), keepBlobs: 3);

            var (outcome, promoted) = await store.TryPromoteRevision(ship, 1, null, null, null, keepBlobs: 3);
            Assert.That(outcome, Is.EqualTo(DrydockBerthResult.Success));

            var current = await store.LoadCurrent(ship);
            Assert.Multiple(() =>
            {
                Assert.That(current!.Revision.Revision, Is.EqualTo(promoted));
                Assert.That(current.Revision.AppraisedValue, Is.EqualTo(24000), "Revision 1's appraisal, not revision 2's and not null.");
            });

            await pair.CleanReturnAsync();
        }

        private static byte[] Doc(int revision) => Encoding.UTF8.GetBytes($"document {revision}");

        private static DrydockRevisionRequest Request(Guid shipId, Guid owner, string name, int appraisal = 24000) => new()
        {
            ShipGuid = shipId,
            OwnerUserId = owner,
            ShipName = name,
            VesselProto = "TestVessel",
            SizeClass = nameof(ShipSizeClass.Cutter),
            Kind = DrydockRevisionKind.PlayerStore,
            MarkStored = true,
            ActorUserId = owner,
            EngineFormatVer = 7,
            ProtoFingerprint = new byte[] { 1, 2, 3 },
            CapturedKeyHash = new byte[] { 4, 5, 6 },
            Checksum = new byte[] { 7, 8, 9 },
            SizeBytes = 23,
            AppraisedValue = appraisal,
            Manifest = "{\"v\":1,\"e\":[]}",
        };

        private static DrydockRebakeRequest Rebake(Guid shipId, int sourceRevision) => new()
        {
            ShipGuid = shipId,
            SourceRevision = sourceRevision,
            RebakeVersion = 1,
            EngineFormatVer = 8,
            ProtoFingerprint = new byte[] { 11, 12 },
            CapturedKeyHash = new byte[] { 13, 14 },
            Checksum = new byte[] { 15, 16 },
            SizeBytes = 17,
            Manifest = "{\"v\":1,\"e\":[\"rebaked\"]}",
        };

        private static async Task<int[]> BlobRevisions(IServerDbManager db, Guid shipId)
        {
            return (await RevisionShape(db, shipId)).Blobs;
        }

        private static Task<(int[] Revisions, int[] Blobs)> RevisionShape(IServerDbManager db, Guid shipId)
        {
            return db.RunTriadDbCommand(async (context, token) =>
            {
                var revisions = await context.DrydockRevision.AsNoTracking()
                    .Where(r => r.ShipGuid == shipId)
                    .Select(r => r.Revision)
                    .OrderBy(r => r)
                    .ToArrayAsync(token);

                var blobs = await context.DrydockBlob.AsNoTracking()
                    .Where(b => b.ShipGuid == shipId)
                    .Select(b => b.Revision)
                    .OrderBy(r => r)
                    .ToArrayAsync(token);

                return (revisions, blobs);
            }, CancellationToken.None);
        }
    }
}
