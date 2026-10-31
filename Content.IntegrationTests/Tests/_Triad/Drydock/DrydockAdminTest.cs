#nullable enable

using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Content.Shared._Triad.ShipSize;
using Content.Shared.Preferences;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The store-layer half of the admin panel: what an admin can do to a hull's record, pinned
    /// against a real database. The live-grid guard on restore needs an entity world and lives in
    /// the round-trip tests.
    /// </summary>
    [TestFixture]
    public sealed class DrydockAdminTest
    {
        [Test]
        public async Task PromotingAnOlderRevisionCopiesItForwardAndNeverRewinds()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var admin = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await DrydockTestHelpers.InsertPlayer(db, admin);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var ship = Guid.NewGuid();
            var good = Encoding.UTF8.GetBytes("the good document");
            var bad = Encoding.UTF8.GetBytes("the bad document");

            await store.FileRevision(Request(ship, owner, "Phoenix"), good, keepBlobs: 3);
            await store.FileRevision(Request(ship, owner, "Phoenix"), bad, keepBlobs: 3);

            var (outcome, promoted) = await store.TryPromoteRevision(ship, 1, admin, null, "revision 2 is corrupt", keepBlobs: 3);
            Assert.Multiple(() =>
            {
                Assert.That(outcome, Is.EqualTo(DrydockBerthResult.Success));
                Assert.That(promoted, Is.EqualTo(3), "A promotion is a new revision, never a rewind of the pointer.");
            });

            var current = await store.LoadCurrent(ship);
            Assert.Multiple(() =>
            {
                Assert.That(current!.Ship.CurrentRevision, Is.EqualTo(3));
                Assert.That(current.Blob, Is.EqualTo(good), "The promoted document is the one the admin chose.");
                Assert.That(current.Revision.Kind, Is.EqualTo(DrydockRevisionKind.AdminRestore));
                Assert.That(current.Revision.DerivedFromRevision, Is.EqualTo(1), "Provenance names what it was copied from.");
                Assert.That(current.Revision.ActorUserId, Is.EqualTo(admin));
            });

            var detail = await store.GetShipDetail(ship);
            Assert.That(detail!.Revisions.Select(r => r.Revision), Is.EqualTo(new[] { 3, 2, 1 }), "History is complete and newest first.");

            var (pruned, _) = await store.TryPromoteRevision(ship, 99, admin, null, "no such revision", keepBlobs: 3);
            Assert.That(pruned, Is.EqualTo(DrydockBerthResult.NotFound));

            var audit = await store.GetAudit(ship);
            var promoted3 = audit.Single(a => a.Action == DrydockAuditAction.RevisionPromoted);
            Assert.Multiple(() =>
            {
                Assert.That(promoted3.Reason, Does.Contain("promoted revision 1"));
                Assert.That(audit.Select(a => a.Action), Does.Not.Contain(DrydockAuditAction.Restore),
                    "A promote changes which document a retrieve reads, never where the hull is, so it is not a restore.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A sold hull comes back only through the sale reversal, which decides about the money
        /// before anything else. The plain restore refuses it, so an admin picking the wrong verb
        /// cannot hand a hull back on top of the credits its owner was paid.
        /// </summary>
        [Test]
        public async Task ASoldShipComesBackOnlyThroughTheSaleReversal()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var admin = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await DrydockTestHelpers.InsertPlayer(db, admin);
            var berth = await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var ship = Guid.NewGuid();
            await store.FileRevision(Request(ship, owner, "Regretted"), Encoding.UTF8.GetBytes("doc"), keepBlobs: 3);

            var (sold, soldName) = await store.TrySellShip(ship, owner, price: 800, appraisal: 1000, roundId: null);
            Assert.Multiple(() =>
            {
                Assert.That(sold, Is.EqualTo(DrydockBerthResult.Success));
                Assert.That(soldName, Is.EqualTo("Regretted"));
            });

            var sale = await store.GetLastSale(ship);
            Assert.That(sale?.Price, Is.EqualTo(800), "The reversal reads the price back off the timeline.");

            Assert.That(await store.TryRestoreShip(ship, berth, admin, null, "wrong verb"), Is.EqualTo(DrydockBerthResult.WrongState),
                "A plain restore refuses a sold hull.");
            Assert.That((await store.LoadCurrent(ship))!.Ship.State, Is.EqualTo(DrydockShipState.Sold), "Refused means untouched.");

            Assert.That(await store.TryRestoreShip(ship, berth, admin, null, "sale reversed", fromSale: true), Is.EqualTo(DrydockBerthResult.Success));

            var back = (await store.LoadCurrent(ship))!.Ship;
            Assert.Multiple(() =>
            {
                Assert.That(back.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(back.BerthId, Is.EqualTo(berth));
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task DeletingAHullKeepsItsTimelineAndFreesItsBerth()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var admin = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await DrydockTestHelpers.InsertPlayer(db, admin);
            var berth = await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var ship = Guid.NewGuid();
            await store.FileRevision(Request(ship, owner, "Doomed"), Encoding.UTF8.GetBytes("doc"), keepBlobs: 3);

            Assert.That(await store.TryDeleteShip(ship, admin, null, "abandoned by owner"), Is.EqualTo(DrydockBerthResult.Success));
            Assert.That(await store.LoadCurrent(ship), Is.Null, "The record, its revisions and its documents are gone.");

            var slots = await store.GetBerths(owner);
            Assert.That(slots.Single(s => s.Berth.BerthId == berth).Occupant, Is.Null, "The berth is left empty, not removed.");

            var audit = await store.GetAudit(ship);
            Assert.Multiple(() =>
            {
                Assert.That(audit.Select(a => a.Action), Does.Contain(DrydockAuditAction.Delete), "The evidence outlives the thing deleted.");
                Assert.That(audit.Single(a => a.Action == DrydockAuditAction.Delete).ShipName, Is.EqualTo("Doomed"),
                    "The name is snapshotted on the row because the ship it names no longer exists.");
                Assert.That(audit.Single(a => a.Action == DrydockAuditAction.Delete).ActorUserId, Is.EqualTo(admin));
            });

            Assert.That(await store.TryDeleteShip(ship, admin, null, "again"), Is.EqualTo(DrydockBerthResult.NotFound));

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task TheListFindsStrandedShips()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var admin = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await DrydockTestHelpers.InsertPlayer(db, admin);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var home = Guid.NewGuid();
            var lost = Guid.NewGuid();
            await store.FileRevision(Request(home, owner, "Home"), Encoding.UTF8.GetBytes("doc"), keepBlobs: 3);
            await store.FileRevision(Request(lost, owner, "Lost"), Encoding.UTF8.GetBytes("doc"), keepBlobs: 3);

            // Out with no round to point at: that is the stranded shape a past round leaves behind.
            await store.TrySetState(lost, DrydockShipState.Stored, DrydockShipState.CheckedOut, DrydockAuditAction.Retrieve, owner, null, null);
            await store.VacateBerth(lost);

            var stranded = await store.QueryShips(new DrydockShipFilter(owner, null, null, null, StrandedOnly: true, CurrentRoundId: 12345), 0, 50);
            Assert.Multiple(() =>
            {
                Assert.That(stranded.Rows.Select(s => s.ShipGuid), Is.EquivalentTo(new[] { lost }), "The adjudication list is exactly the ships out in a round that is over.");
                Assert.That(stranded.Total, Is.EqualTo(1));
            });

            var byName = await store.QueryShips(new DrydockShipFilter(null, null, "hom", null, false, null), 0, 50);
            Assert.That(byName.Rows.Select(s => s.ShipGuid), Does.Contain(home), "Name filtering is case-insensitive on both providers.");

            var detail = await store.GetShipDetail(lost);
            Assert.That(detail!.Timeline.Select(a => a.Action), Is.EqualTo(new[] { DrydockAuditAction.Retrieve, DrydockAuditAction.Store }),
                "The detail's timeline is newest first.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// An admin cancelling a standing offer puts the ship back in the owner's own berth and
        /// says on the timeline who lost the offer. Escrow is the only state that keeps its berth
        /// while refusing every verb, so the berth being there to come back to is the assertion
        /// that matters.
        /// </summary>
        [Test]
        public async Task CancellingAnOfferReturnsTheShipToItsBerth()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            var admin = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await DrydockTestHelpers.InsertPlayer(db, recipient);
            await DrydockTestHelpers.InsertPlayer(db, admin);

            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            // The offer is refused outright unless the recipient has somewhere to put it.
            await store.AddBerth(recipient, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var ship = Guid.NewGuid();
            await store.FileRevision(Request(ship, owner, "Contested"), Encoding.UTF8.GetBytes("doc"), keepBlobs: 3);

            var (offered, transfer) = await store.TryOfferTransfer(ship, owner, recipient, TimeSpan.FromMinutes(30), null);
            Assert.Multiple(() =>
            {
                Assert.That(offered, Is.EqualTo(DrydockBerthResult.Success));
                Assert.That(transfer, Is.Not.Null);
                Assert.That(transfer!.ToUserId, Is.EqualTo(recipient));
            });
            Assert.That((await store.LoadCurrent(ship))!.Ship.State, Is.EqualTo(DrydockShipState.InEscrow));

            var resolved = await store.TryResolveTransfer(transfer!.Id, DrydockTransferResolution.Cancelled, admin, null,
                adminOverride: true, reason: "recipient reported for scamming");
            Assert.That(resolved, Is.Not.Null);

            var standing = await store.GetPendingOfferForShip(ship);
            var after = (await store.LoadCurrent(ship))!.Ship;
            Assert.Multiple(() =>
            {
                Assert.That(standing, Is.Null, "The offer is gone, so the recipient's alert is too.");
                Assert.That(after.State, Is.EqualTo(DrydockShipState.Stored), "Escrow releases back to the owner's own berth.");
                Assert.That(after.BerthId, Is.Not.Null, "A ship in escrow keeps its berth, so there is one to come back to.");
            });

            var audit = await store.GetAudit(ship);
            var cancelled = audit.Single(a => a.Action == DrydockAuditAction.TransferCancelled);
            Assert.Multiple(() =>
            {
                Assert.That(cancelled.Reason, Is.EqualTo("recipient reported for scamming"), "The timeline says why the offer died, not just that it did.");
                Assert.That(cancelled.ActorUserId, Is.EqualTo(admin));
                Assert.That(cancelled.SubjectUserId, Is.EqualTo(recipient), "Subject is who lost the offer.");
            });

            var (again, _) = await store.TryOfferTransfer(ship, owner, recipient, TimeSpan.FromMinutes(30), null);
            Assert.That(again, Is.EqualTo(DrydockBerthResult.Success), "Control: back in its berth, the ship can be offered again.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A rename does not hide a hull. The admin box searches every name a ship has carried,
        /// because the complaint is filed under the name it had at the time.
        /// </summary>
        [Test]
        public async Task APastNameIsStillSearchableAfterARename()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            // Pooled pairs share one database, so the needles carry a token no other test uses.
            var token = Guid.NewGuid().ToString("N")[..8];
            var oldName = $"Wanderer{token}";
            var newName = $"Vagrant{token}";

            var ship = Guid.NewGuid();
            await store.FileRevision(Request(ship, owner, oldName), Encoding.UTF8.GetBytes("doc"), keepBlobs: 3);
            Assert.That(await store.TryRenameShip(ship, owner, newName, null), Is.EqualTo(DrydockBerthResult.Success));

            var byOldName = await store.QueryShips(Search(oldName), 0, 50);
            var byNewName = await store.QueryShips(Search(newName), 0, 50);
            var byNothing = await store.QueryShips(Search($"Nomad{token}"), 0, 50);

            Assert.Multiple(() =>
            {
                Assert.That(byNewName.Rows.Select(s => s.ShipGuid), Does.Contain(ship), "The name it wears now.");
                Assert.That(byOldName.Rows.Select(s => s.ShipGuid), Does.Contain(ship),
                    "The name it wore then, held on the audit snapshot rather than the row.");
                Assert.That(byNothing.Rows, Is.Empty, "A name it never had matches nothing, so the search is not matching everything.");
            });

            // The id boxes are the same box: a guid searches ship and owner, not text.
            var byId = await store.QueryShips(Search(ship.ToString()), 0, 50);
            Assert.That(byId.Rows.Select(s => s.ShipGuid), Is.EquivalentTo(new[] { ship }));

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// An AHelp names a character, not an account. Any of the owner's character names finds
        /// every ship the account owns, and a name nobody wears finds nothing.
        /// </summary>
        [Test]
        public async Task ACharacterNameFindsTheOwnersShips()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var stranger = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await DrydockTestHelpers.InsertPlayer(db, stranger);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            await store.AddBerth(stranger, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            // Pooled pairs share one database, so the needles carry a token no other test uses.
            var token = Guid.NewGuid().ToString("N")[..8];
            await db.InitPrefsAsync(new NetUserId(owner), new HumanoidCharacterProfile { Name = $"Mara Voss{token}" }, CancellationToken.None);
            await db.SaveCharacterSlotAsync(new NetUserId(owner), new HumanoidCharacterProfile { Name = $"Ilse Varga{token}" }, 1);
            await db.InitPrefsAsync(new NetUserId(stranger), new HumanoidCharacterProfile { Name = $"Tomas Reyes{token}" }, CancellationToken.None);

            var ship = Guid.NewGuid();
            var other = Guid.NewGuid();
            await store.FileRevision(Request(ship, owner, "Kestrel"), Encoding.UTF8.GetBytes("doc"), keepBlobs: 3);
            await store.FileRevision(Request(other, stranger, "Harrier"), Encoding.UTF8.GetBytes("doc"), keepBlobs: 3);

            var bySelected = await store.QueryShips(Search($"voss{token}"), 0, 50);
            var byAlt = await store.QueryShips(Search($"VARGA{token}"), 0, 50);
            var byNobody = await store.QueryShips(Search($"Nomad{token}"), 0, 50);

            Assert.Multiple(() =>
            {
                Assert.That(bySelected.Rows.Select(s => s.ShipGuid), Is.EquivalentTo(new[] { ship }),
                    "Part of a character name, any case, finds that account's ships and no one else's.");
                Assert.That(byAlt.Rows.Select(s => s.ShipGuid), Is.EquivalentTo(new[] { ship }),
                    "A character in another slot finds the same account.");
                Assert.That(byNobody.Rows, Is.Empty, "Control: a name nobody wears matches nothing.");
            });

            await pair.CleanReturnAsync();
        }

        private static DrydockShipFilter Search(string needle)
            => new(null, null, null, null, StrandedOnly: false, CurrentRoundId: null, Search: needle);

        private static DrydockRevisionRequest Request(Guid shipId, Guid owner, string name) => new()
        {
            ShipGuid = shipId,
            OwnerUserId = owner,
            ShipName = name,
            VesselProto = "TestVessel",
            SizeClass = nameof(ShipSizeClass.Cutter),
            Kind = DrydockRevisionKind.PlayerStore,
            MarkStored = true,
            ActorUserId = owner,
            CreatedRoundId = null,
            EngineFormatVer = 7,
            ProtoFingerprint = new byte[] { 1, 2, 3 },
            CapturedKeyHash = new byte[] { 4, 5, 6 },
            Checksum = new byte[] { 7, 8, 9 },
            SizeBytes = 3,
            Manifest = "{\"v\":1,\"e\":[]}",
        };

    }
}
