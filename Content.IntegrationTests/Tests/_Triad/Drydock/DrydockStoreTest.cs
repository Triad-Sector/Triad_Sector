#nullable enable

using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Content.Shared._NF.Bank;
using Content.Shared._Triad.ShipSize;
using Microsoft.EntityFrameworkCore;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// Exercises the drydock's persistence against a real database, because the guarantees being
    /// tested are transactional and a unit test with a fake would prove nothing about them: that a
    /// revision is filed with its blob and its audit row together, that pruning takes blobs and
    /// never history, and that the current revision's blob survives pruning whatever the keep count
    /// says.
    ///
    /// <para>Everything is scoped to a freshly minted ship id, so the rows this leaves behind in a
    /// pooled server's database cannot be seen by any other test.</para>
    ///
    /// <para>Round ids are null in the filing tests, which is the between-rounds case. The ones that
    /// check a ship out create a real round, because that column is a foreign key.</para>
    /// </summary>
    [TestFixture]
    public sealed class DrydockStoreTest
    {
        [Test]
        public async Task RevisionsAndPruning()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var store = server.ResolveDependency<DrydockStore>();
            var db = server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var shipId = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var firstBlob = Encoding.UTF8.GetBytes("first revision document");
            var secondBlob = Encoding.UTF8.GetBytes("second revision document");

            // Keep two blobs, so the third store below is what proves pruning happens at all.
            var first = await store.FileRevision(Request(shipId, owner, "Kestrel"), firstBlob, keepBlobs: 2);
            Assert.That(first.Outcome, Is.EqualTo(DrydockBerthResult.Success));
            Assert.That(first.Revision, Is.EqualTo(1), "The first revision of a ship is 1.");

            var loaded = await store.LoadCurrent(shipId);
            Assert.That(loaded, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(loaded!.Ship.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(loaded.Ship.CurrentRevision, Is.EqualTo(1));
                Assert.That(loaded.Ship.OwnerUserId, Is.EqualTo(owner));
                Assert.That(loaded.Revision.Kind, Is.EqualTo(DrydockRevisionKind.PlayerStore));
                Assert.That(loaded.Blob, Is.EqualTo(firstBlob));
            });

            // A second store lands as a new revision on the same hull rather than a second hull.
            var second = await store.FileRevision(Request(shipId, owner, "Kestrel II"), secondBlob, keepBlobs: 2);
            Assert.That(second.Revision, Is.EqualTo(2));

            loaded = await store.LoadCurrent(shipId);
            Assert.Multiple(() =>
            {
                Assert.That(loaded!.Ship.CurrentRevision, Is.EqualTo(2));
                Assert.That(loaded.Blob, Is.EqualTo(secondBlob));
                Assert.That(loaded.Ship.ShipName, Is.EqualTo("Kestrel II"), "The display cache refreshes on every store.");
            });

            // The ownership rule: a store never moves the ship to whoever filed it.
            var otherOwner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, otherOwner);
            await store.FileRevision(Request(shipId, otherOwner, "Kestrel III"), secondBlob, keepBlobs: 2);

            loaded = await store.LoadCurrent(shipId);
            Assert.That(loaded!.Ship.OwnerUserId, Is.EqualTo(owner),
                "A store must not transfer the ship. Ownership moves through a transfer, with its own audit row.");

            // Three revisions filed with keepBlobs 2, so revision 1's blob is gone and its history
            // is not. This is the guarantee that lets the design promise a hull's whole history.
            var (revisionCount, blobRevisions) = await ReadRevisionShape(db, shipId);
            Assert.Multiple(() =>
            {
                Assert.That(revisionCount, Is.EqualTo(3), "Revision history is kept indefinitely.");
                Assert.That(blobRevisions, Is.EquivalentTo(new[] { 2, 3 }), "Pruning takes blobs, oldest first, and never history.");
            });

            // Keep exactly one, which is the tightest setting that prunes: the floor raises it to two,
            // so the one a retrieve is about to read stays along with one step back to fall to. The
            // case where an off-by-one would delete the live document. The floor's own tests, with
            // controls, are in DrydockDurabilityStoreTest.
            await store.FileRevision(Request(shipId, owner, "Kestrel IV"), firstBlob, keepBlobs: 1);
            loaded = await store.LoadCurrent(shipId);
            Assert.That(loaded, Is.Not.Null, "Pruning must never take the blob the current revision points at.");
            Assert.That(loaded!.Blob, Is.EqualTo(firstBlob));

            var (_, afterTightPrune) = await ReadRevisionShape(db, shipId);
            Assert.That(afterTightPrune, Is.EquivalentTo(new[] { 3, 4 }), "Keeping one is floored at two: the current blob and the one before it.");

            // Zero or less means no pruning at all rather than keep nothing, which is the only
            // reading that is safe to misconfigure: the wrong guess costs disk, not ships.
            await store.FileRevision(Request(shipId, owner, "Kestrel V"), secondBlob, keepBlobs: 0);
            var (_, afterNoPrune) = await ReadRevisionShape(db, shipId);
            Assert.That(afterNoPrune, Is.EquivalentTo(new[] { 3, 4, 5 }), "A keep count of zero prunes nothing.");

            var audit = await store.GetAudit(shipId);
            Assert.That(audit.Select(a => a.Action), Is.All.EqualTo(DrydockAuditAction.Store));
            Assert.That(audit, Has.Count.EqualTo(5), "Every store is on the timeline, not just the exceptional ones.");

            var owned = await store.GetShipsByOwner(owner);
            Assert.That(owned.Select(s => s.ShipGuid), Does.Contain(shipId));

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task StateChangesCarryTheirAuditRow()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var store = server.ResolveDependency<DrydockStore>();
            var db = server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var shipId = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            await store.FileRevision(Request(shipId, owner, "Harrier"), Encoding.UTF8.GetBytes("doc"), keepBlobs: 2);

            // The retrieve gate: only a stored ship can be checked out, and the check and the move
            // are one statement so two of them cannot both win.
            var moved = await store.TrySetState(shipId, DrydockShipState.Stored, DrydockShipState.CheckedOut,
                DrydockAuditAction.Retrieve, owner, null, null);
            Assert.That(moved, Is.True);

            var raced = await store.TrySetState(shipId, DrydockShipState.Stored, DrydockShipState.CheckedOut,
                DrydockAuditAction.Retrieve, owner, null, null);
            Assert.That(raced, Is.False, "A second retrieve of a checked-out ship must lose, which is what stops a duplicate.");

            // An admin impound does not care what state the ship was in.
            var impounded = await store.TrySetState(shipId, null, DrydockShipState.Impounded,
                DrydockAuditAction.Impound, null, null, "pending a decision");
            Assert.That(impounded, Is.True);

            var impoundedAgain = await store.TrySetState(shipId, null, DrydockShipState.Impounded,
                DrydockAuditAction.Impound, null, null, "again");
            Assert.That(impoundedAgain, Is.False, "Moving to the state a ship is already in is not a change and must not log one.");

            var audit = await store.GetAudit(shipId);
            Assert.Multiple(() =>
            {
                Assert.That(audit.Select(a => a.Action), Is.EqualTo(new[]
                {
                    DrydockAuditAction.Store,
                    DrydockAuditAction.Retrieve,
                    DrydockAuditAction.Impound,
                }), "The timeline is ordered and holds one row per accepted change.");

                Assert.That(audit[^1].Reason, Is.EqualTo("pending a decision"),
                    "An adjudication's reasoning is the whole reason the row exists.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The impound of a ship sitting in a berth: one conditional move from Stored, with the berth
        /// vacated and remembered, the terms and the fee written, and the audit row naming who took
        /// it from whom, all under one commit. Anything not sitting in a berth is refused, because a
        /// hull that is out is the pipeline's to take and a terminal row is a verdict rather than a
        /// hull. And the only way back out of the lot is into a berth: the one it left by default,
        /// another that fits when that one is taken, and a refusal that leaves the ship in the lot
        /// when nothing is free.
        /// </summary>
        [Test]
        public async Task AnImpoundFromABerthIsOneMoveAndTheWayOutIsIntoABerth()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var store = server.ResolveDependency<DrydockStore>();
            var db = server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var admin = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            for (var i = 0; i < 3; i++)
                await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            // A checkout records the round it left in, and that column is a foreign key, so the
            // round has to exist. In play it always does: an impound happens inside a round.
            var round = await db.AddNewRound(await db.AddOrGetServer("drydock-test"));
            var doc = Encoding.UTF8.GetBytes("doc");

            var resting = Guid.NewGuid();
            var filed = await store.FileRevision(Request(resting, owner, "Kestrel"), doc, keepBlobs: 2);
            Assert.That(filed.Outcome, Is.EqualTo(DrydockBerthResult.Success));
            var seated = filed.BerthId!.Value;

            // A second hull, out flying but still seated, which is how a row reads between a
            // retrieve's claim and its vacate. Filed before the impound so it takes a berth of its
            // own rather than the one the impound is about to vacate, since a store seats into the
            // lowest free berth that fits.
            var flying = Guid.NewGuid();
            await store.FileRevision(Request(flying, owner, "Harrier"), doc, keepBlobs: 2);
            Assert.That(await store.TrySetState(flying, DrydockShipState.Stored, DrydockShipState.CheckedOut, DrydockAuditAction.Retrieve, owner, round, null), Is.True);

            var terms = new DrydockImpound(25, "ticket #94", Redeemable: false, ActorUserId: admin);
            var (outcome, fee) = await store.TryImpoundStored(resting, terms, round);
            Assert.Multiple(() =>
            {
                Assert.That(outcome, Is.EqualTo(DrydockBerthResult.Success));
                Assert.That(fee, Is.EqualTo(6000), "25% of the $24,000 the current revision appraised at.");
            });

            var row = (await store.GetShipsByOwner(owner)).Single(r => r.ShipGuid == resting);
            Assert.Multiple(() =>
            {
                Assert.That(row.State, Is.EqualTo(DrydockShipState.Impounded));
                Assert.That(row.BerthId, Is.Null, "The holding area is not a berth.");
                Assert.That(row.LastBerthId, Is.EqualTo(seated), "Where it came from is the release's default.");
                Assert.That(row.CheckedOutRoundId, Is.Null, "Nothing is out.");
                Assert.That(row.ImpoundFee, Is.EqualTo(6000));
                Assert.That(row.ImpoundRedeemable, Is.False);
                Assert.That(row.ImpoundReason, Is.EqualTo("ticket #94"));
            });

            var taken = (await store.GetAudit(resting))[^1];
            Assert.Multiple(() =>
            {
                Assert.That(taken.Action, Is.EqualTo(DrydockAuditAction.Impound));
                Assert.That(taken.ActorUserId, Is.EqualTo(admin), "The admin took it; the owner is never the actor of their own impound.");
                Assert.That(taken.SubjectUserId, Is.EqualTo(owner));
                Assert.That(taken.BerthId, Is.EqualTo(seated), "The berth vacated.");
                Assert.That(taken.RoundId, Is.EqualTo(round));
                Assert.That(taken.Reason, Does.Contain("ticket #94"));
                Assert.That(taken.Reason, Does.Contain($"fee {BankSystemExtensions.ToSpesoString(6000)}, 25% of {BankSystemExtensions.ToSpesoString(24000)}"),
                    "The timeline keeps the numbers; the row keeps only the latest impound's.");
                Assert.That(taken.Reason, Does.Contain("held for adjudication"));
            });

            Assert.That((await store.TryImpoundStored(resting, terms, round)).Outcome, Is.EqualTo(DrydockBerthResult.WrongState),
                "Already in the lot.");

            // A hull that is out is not taken from a berth. That is the pipeline's job, because the
            // grid has to leave the world before the row may say impounded.
            Assert.That((await store.TryImpoundStored(flying, terms, round)).Outcome, Is.EqualTo(DrydockBerthResult.WrongState));
            Assert.That((await store.GetShipsByOwner(owner)).Single(r => r.ShipGuid == flying).State, Is.EqualTo(DrydockShipState.CheckedOut),
                "Refused means untouched.");

            // The way out: into the berth it left, for nothing, with the terms left on the row.
            var (released, into) = await store.TryReleaseImpound(resting, null, admin, round, "cleared");
            Assert.Multiple(() =>
            {
                Assert.That(released, Is.EqualTo(DrydockBerthResult.Success));
                Assert.That(into, Is.EqualTo(seated), "Its last berth was free, so that is the default.");
            });

            row = (await store.GetShipsByOwner(owner)).Single(r => r.ShipGuid == resting);
            Assert.Multiple(() =>
            {
                Assert.That(row.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(row.BerthId, Is.EqualTo(seated), "A stored ship holds a berth; a release that left it berthless would be invisible to its owner and free capacity for the anti-join.");
                Assert.That(row.ImpoundFee, Is.EqualTo(6000), "Never cleared on the way out; the next impound overwrites it.");
                Assert.That(row.ImpoundReason, Is.EqualTo("ticket #94"));
            });

            var lifted = (await store.GetAudit(resting))[^1];
            Assert.Multiple(() =>
            {
                Assert.That(lifted.Action, Is.EqualTo(DrydockAuditAction.ImpoundReleased));
                Assert.That(lifted.BerthId, Is.EqualTo(seated), "The berth it landed in.");
                Assert.That(lifted.ActorUserId, Is.EqualTo(admin));
            });

            Assert.That((await store.TryReleaseImpound(resting, null, admin, round, "again")).Outcome, Is.EqualTo(DrydockBerthResult.WrongState),
                "Nothing to release twice.");

            // With its last berth taken, the release picks another that fits.
            Assert.That((await store.TryImpoundStored(resting, terms, round)).Outcome, Is.EqualTo(DrydockBerthResult.Success));
            var squatter = Guid.NewGuid();
            var squat = await store.FileRevision(Request(squatter, owner, "Pelican", berthId: seated), doc, keepBlobs: 2);
            Assert.That(squat.BerthId, Is.EqualTo(seated), "Control: the vacated berth was free to take.");

            var (again, elsewhere) = await store.TryReleaseImpound(resting, null, admin, round, "cleared");
            Assert.Multiple(() =>
            {
                Assert.That(again, Is.EqualTo(DrydockBerthResult.Success));
                Assert.That(elsewhere, Is.Not.Null.And.Not.EqualTo(seated), "Its last berth is taken, so the smallest free one that fits.");
            });

            // With nothing free, the release refuses and the ship stays in the lot rather than
            // becoming a stored ship with nowhere to be.
            Assert.That((await store.TryImpoundStored(resting, terms, round)).Outcome, Is.EqualTo(DrydockBerthResult.Success));
            var fourth = Guid.NewGuid();
            var filler = await store.FileRevision(Request(fourth, owner, "Osprey"), doc, keepBlobs: 2);
            Assert.That(filler.BerthId, Is.EqualTo(elsewhere), "Control: the berth the release used is free again and the store takes it.");

            var (refused, nowhere) = await store.TryReleaseImpound(resting, null, admin, round, "cleared");
            row = (await store.GetShipsByOwner(owner)).Single(r => r.ShipGuid == resting);
            Assert.Multiple(() =>
            {
                Assert.That(refused, Is.EqualTo(DrydockBerthResult.NoBerth));
                Assert.That(nowhere, Is.Null);
                Assert.That(row.State, Is.EqualTo(DrydockShipState.Impounded), "Refused means still in the lot.");
                Assert.That(row.BerthId, Is.Null);
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The impound's whole reason for existing at the store layer: it files a hull that has
        /// nowhere to go. The berth is vacated rather than seated, which is what makes the
        /// redemption gate mean something, and the terms survive the way out, which is what makes an
        /// admin reversal have something to restore to.
        /// </summary>
        [Test]
        public async Task AnImpoundVacatesTheBerthAndKeepsItsTerms()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var store = server.ResolveDependency<DrydockStore>();
            var db = server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            // Stored first, so there is a berth to lose.
            var shipId = Guid.NewGuid();
            var filed = await store.FileRevision(Request(shipId, owner, "Kestrel"), Encoding.UTF8.GetBytes("doc"), keepBlobs: 2);
            Assert.That(filed.Outcome, Is.EqualTo(DrydockBerthResult.Success));
            Assert.That(filed.BerthId, Is.Not.Null, "A control: the ordinary store seats the hull.");
            var seated = filed.BerthId!.Value;

            // 50% of the request's $24,000 appraisal. The percent is what travels; the credits are computed
            // against the appraisal, so a fee over the hull's worth cannot be expressed. A null actor is
            // the round-end sweep, which files as the system.
            var impound = new DrydockImpound(50, "left in the world at round end", Redeemable: true, ActorUserId: null);
            var taken = await store.FileRevision(
                Request(shipId, owner, "Kestrel", markStored: false, impound: impound, evicted: 2),
                Encoding.UTF8.GetBytes("doc2"), keepBlobs: 2);

            Assert.That(taken.Outcome, Is.EqualTo(DrydockBerthResult.Success));
            Assert.That(await store.MarkImpounded(shipId), Is.True);

            var row = (await store.GetShipsByOwner(owner)).Single(r => r.ShipGuid == shipId);
            Assert.Multiple(() =>
            {
                Assert.That(row.State, Is.EqualTo(DrydockShipState.Impounded));
                Assert.That(row.BerthId, Is.Null, "The holding area is not a berth, and a hull still holding one redeems for free.");
                Assert.That(row.LastBerthId, Is.EqualTo(seated), "Where it came from is the release's default.");
                Assert.That(row.ImpoundFee, Is.EqualTo(12000), "50% of the $24,000 this revision appraised at.");
                Assert.That(row.ImpoundRedeemable, Is.True);
                Assert.That(row.ImpoundReason, Is.EqualTo("left in the world at round end"));
            });

            var audit = (await store.GetAudit(shipId))[^1];
            Assert.Multiple(() =>
            {
                Assert.That(audit.Action, Is.EqualTo(DrydockAuditAction.Impound),
                    "The filing writes the impound, not a store: a timeline that says stored for a hull nobody put away is a lie.");
                Assert.That(audit.ActorUserId, Is.Null, "The sweep is the system, and the owner is never the actor of their own impound.");
                Assert.That(audit.SubjectUserId, Is.EqualTo(owner));
                Assert.That(audit.BerthId, Is.EqualTo(seated), "The berth vacated.");
                Assert.That(audit.Reason, Does.Contain("left in the world at round end"));
                Assert.That(audit.Reason, Does.Contain($"fee {BankSystemExtensions.ToSpesoString(12000)}, 50% of {BankSystemExtensions.ToSpesoString(24000)}"));
                Assert.That(audit.Reason, Does.Contain("owner can reclaim"));
                Assert.That(audit.Reason, Does.Contain("2 moved off"), "A hull taken with people on it says so on the timeline.");
            });

            // The way out keeps them. A reversal needs something to restore to, and the timeline has
            // to keep saying what the hull was taken for.
            var (released, into) = await store.TryReleaseImpound(shipId, null, null, null, "cleared");
            Assert.Multiple(() =>
            {
                Assert.That(released, Is.EqualTo(DrydockBerthResult.Success));
                Assert.That(into, Is.EqualTo(seated));
            });

            var back = (await store.GetShipsByOwner(owner)).Single(r => r.ShipGuid == shipId);
            Assert.Multiple(() =>
            {
                Assert.That(back.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(back.BerthId, Is.EqualTo(seated));
                Assert.That(back.ImpoundFee, Is.EqualTo(12000), "Never cleared on the way out; the next impound overwrites it.");
                Assert.That(back.ImpoundReason, Is.EqualTo("left in the world at round end"));
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A ship in escrow keeps its berth: every resolution but an accept sets it Stored without
        /// touching the berth, so a vacate or a restore landing under a standing offer would leave a
        /// stored row with nowhere to be, or an offer resolving against a row that already moved.
        /// Both refuse. The control is the same two verbs on the states they are for.
        /// </summary>
        [Test]
        public async Task AnEscrowShipKeepsItsBerthAgainstVacateAndRestore()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var store = server.ResolveDependency<DrydockStore>();
            var db = server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            var admin = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await DrydockTestHelpers.InsertPlayer(db, recipient);
            var home = await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            var spare = await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            await store.AddBerth(recipient, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            var round = await db.AddNewRound(await db.AddOrGetServer("drydock-test"));

            var shipId = Guid.NewGuid();
            var filed = await store.FileRevision(Request(shipId, owner, "Kestrel", berthId: home), Encoding.UTF8.GetBytes("doc"), keepBlobs: 2);
            Assert.That(filed.BerthId, Is.EqualTo(home));

            var (offered, transfer) = await store.TryOfferTransfer(shipId, owner, recipient, TimeSpan.FromMinutes(30), round);
            Assert.That(offered, Is.EqualTo(DrydockBerthResult.Success), "Control: the offer opens, so the row below is in escrow.");

            Assert.That(await store.TryMoveShip(shipId, null, admin, round, "vacate"), Is.EqualTo(DrydockBerthResult.WrongState),
                "Vacating an escrow ship would leave the decline that follows landing on Stored with no berth.");
            Assert.That(await store.TryRestoreShip(shipId, spare, admin, round, "restore"), Is.EqualTo(DrydockBerthResult.WrongState),
                "Restoring an escrow ship seats it under an offer that still resolves against it.");

            var row = (await store.GetShipsByOwner(owner)).Single(r => r.ShipGuid == shipId);
            Assert.Multiple(() =>
            {
                Assert.That(row.State, Is.EqualTo(DrydockShipState.InEscrow), "Refused means untouched.");
                Assert.That(row.BerthId, Is.EqualTo(home));
            });

            // Resolved, the ship is stored in the berth it never left.
            Assert.That(await store.TryResolveTransfer(transfer!.Id, DrydockTransferResolution.Declined, recipient, round), Is.Not.Null);
            row = (await store.GetShipsByOwner(owner)).Single(r => r.ShipGuid == shipId);
            Assert.Multiple(() =>
            {
                Assert.That(row.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(row.BerthId, Is.EqualTo(home), "A decline keeps the berth, which is why a vacate before it had to refuse.");
            });

            // Control: the same verb on the state it is for. Out with a berth still shown, which is
            // the crash between a retrieve's confirm and its vacate, is exactly what vacate repairs.
            Assert.That(await store.TrySetState(shipId, DrydockShipState.Stored, DrydockShipState.CheckedOut, DrydockAuditAction.Retrieve, owner, round, null), Is.True);
            Assert.That(await store.TryMoveShip(shipId, null, admin, round, "vacate"), Is.EqualTo(DrydockBerthResult.Success));
            row = (await store.GetShipsByOwner(owner)).Single(r => r.ShipGuid == shipId);
            Assert.Multiple(() =>
            {
                Assert.That(row.BerthId, Is.Null);
                Assert.That(row.LastBerthId, Is.EqualTo(home));
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The owner's two ways out of the lot. Reclaim pays the fee on the row and seats the hull
        /// in a berth of theirs, refusing the wrong owner, a fee that changed under the press, a
        /// berth that will not take it, and a locked impound; abandon pays nothing and lands on its
        /// own terminal state, refusing a locked impound for the same reason, that an owner must not
        /// end an adjudication from their side. Both leave the terms on the row, and the admin's way
        /// back from an abandon is the restore, which says so on the timeline.
        /// </summary>
        [Test]
        public async Task TheOwnerReclaimsOrAbandonsAnImpoundedHull()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var store = server.ResolveDependency<DrydockStore>();
            var db = server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var stranger = Guid.NewGuid();
            var admin = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await DrydockTestHelpers.InsertPlayer(db, stranger);
            var home = await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            var spare = await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            var round = await db.AddNewRound(await db.AddOrGetServer("drydock-test"));
            var doc = Encoding.UTF8.GetBytes("doc");

            var shipId = Guid.NewGuid();
            var filed = await store.FileRevision(Request(shipId, owner, "Kestrel", berthId: home), doc, keepBlobs: 2);
            Assert.That(filed.BerthId, Is.EqualTo(home));

            // Into the lot at half its $24,000 appraisal, with the owner free to act on it.
            var open = new DrydockImpound(50, "left out", Redeemable: true, ActorUserId: admin);
            var (taken, fee) = await store.TryImpoundStored(shipId, open, round);
            Assert.Multiple(() =>
            {
                Assert.That(taken, Is.EqualTo(DrydockBerthResult.Success));
                Assert.That(fee, Is.EqualTo(12000));
            });

            // The refusals, each leaving the hull in the lot.
            Assert.That(await store.TryRedeemImpound(shipId, stranger, home, 12000, round), Is.EqualTo(DrydockBerthResult.NotFound),
                "Not the owner's to reclaim, whatever they pay.");
            Assert.That(await store.TryRedeemImpound(shipId, owner, home, 999, round), Is.EqualTo(DrydockBerthResult.Conflict),
                "The fee paid has to be the fee on the row: a re-impound on new terms between the read and the press is not honoured at the old price.");

            var squatter = Guid.NewGuid();
            var squat = await store.FileRevision(Request(squatter, owner, "Pelican", berthId: home), doc, keepBlobs: 2);
            Assert.That(squat.BerthId, Is.EqualTo(home), "Control: the vacated berth was free to take.");
            Assert.That(await store.TryRedeemImpound(shipId, owner, home, 12000, round), Is.EqualTo(DrydockBerthResult.BerthOccupied));

            var row = (await store.GetShipsByOwner(owner)).Single(r => r.ShipGuid == shipId);
            Assert.That(row.State, Is.EqualTo(DrydockShipState.Impounded), "Every refusal above left it in the lot.");

            // Paid and seated, with the terms left on the row.
            Assert.That(await store.TryRedeemImpound(shipId, owner, spare, 12000, round), Is.EqualTo(DrydockBerthResult.Success));
            row = (await store.GetShipsByOwner(owner)).Single(r => r.ShipGuid == shipId);
            Assert.Multiple(() =>
            {
                Assert.That(row.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(row.BerthId, Is.EqualTo(spare));
                Assert.That(row.ImpoundFee, Is.EqualTo(12000), "Never cleared on the way out; a reversal needs it.");
            });

            var redeemed = (await store.GetAudit(shipId))[^1];
            Assert.Multiple(() =>
            {
                Assert.That(redeemed.Action, Is.EqualTo(DrydockAuditAction.ImpoundRedeemed));
                Assert.That(redeemed.ActorUserId, Is.EqualTo(owner));
                Assert.That(redeemed.BerthId, Is.EqualTo(spare));
                Assert.That(redeemed.Reason, Is.EqualTo("paid 12000"));
            });

            // Locked: the owner can do neither, whatever they pay.
            var locked = new DrydockImpound(0, "ticket #94", Redeemable: false, ActorUserId: admin);
            Assert.That((await store.TryImpoundStored(shipId, locked, round)).Outcome, Is.EqualTo(DrydockBerthResult.Success));
            Assert.That(await store.TryRedeemImpound(shipId, owner, spare, 0, round), Is.EqualTo(DrydockBerthResult.WrongState),
                "A locked impound is an adjudication; paying nothing does not end it.");
            Assert.That((await store.TryAbandonShip(shipId, owner, round)).Outcome, Is.EqualTo(DrydockBerthResult.WrongState),
                "Nor does walking away: an owner must not end an adjudication from their side.");
            Assert.That((await store.GetShipsByOwner(owner)).Single(r => r.ShipGuid == shipId).State, Is.EqualTo(DrydockShipState.Impounded));

            // Unlocked by the admin, taken again on open terms, then given up rather than paid for.
            Assert.That((await store.TryReleaseImpound(shipId, null, admin, round, "cleared")).Outcome, Is.EqualTo(DrydockBerthResult.Success));
            Assert.That((await store.TryImpoundStored(shipId, open, round)).Outcome, Is.EqualTo(DrydockBerthResult.Success));
            Assert.That((await store.TryAbandonShip(shipId, stranger, round)).Outcome, Is.EqualTo(DrydockBerthResult.NotFound));

            var (abandoned, name) = await store.TryAbandonShip(shipId, owner, round);
            Assert.Multiple(() =>
            {
                Assert.That(abandoned, Is.EqualTo(DrydockBerthResult.Success));
                Assert.That(name, Is.EqualTo("Kestrel"));
            });

            row = (await store.GetShipsByOwner(owner)).Single(r => r.ShipGuid == shipId);
            Assert.Multiple(() =>
            {
                Assert.That(row.State, Is.EqualTo(DrydockShipState.Abandoned));
                Assert.That(row.BerthId, Is.Null, "The lot held no berth and neither does the state after it.");
                Assert.That(row.CheckedOutRoundId, Is.Null);
            });

            var gaveUp = (await store.GetAudit(shipId))[^1];
            Assert.Multiple(() =>
            {
                Assert.That(gaveUp.Action, Is.EqualTo(DrydockAuditAction.ShipAbandoned));
                Assert.That(gaveUp.ActorUserId, Is.EqualTo(owner));
                Assert.That(gaveUp.Reason, Is.EqualTo("gave up rather than pay 12000"), "The timeline says what it would have cost.");
            });

            Assert.That((await store.TryAbandonShip(shipId, owner, round)).Outcome, Is.EqualTo(DrydockBerthResult.WrongState), "Given up once.");
            Assert.That(await store.TryRedeemImpound(shipId, owner, spare, 12000, round), Is.EqualTo(DrydockBerthResult.WrongState), "Not in the lot any more.");

            // The admin's undo is the restore, and the timeline says it reversed an abandon rather
            // than recovering a hull judged lost.
            Assert.That(await store.TryRestoreShip(shipId, spare, admin, round, "on appeal"), Is.EqualTo(DrydockBerthResult.Success));
            row = (await store.GetShipsByOwner(owner)).Single(r => r.ShipGuid == shipId);
            Assert.Multiple(() =>
            {
                Assert.That(row.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(row.BerthId, Is.EqualTo(spare));
            });
            Assert.That((await store.GetAudit(shipId))[^1].Action, Is.EqualTo(DrydockAuditAction.AbandonReversed));

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The shipyard scrapping a hull that is out on a retrieve. The credits move at the console;
        /// this is the row hearing about it, so a scrapped ship never reads as stranded, is never
        /// handed back by a plain restore, and the reversal finds the price on the timeline.
        /// </summary>
        [Test]
        public async Task ALiveSaleAtTheShipyardMovesTheRowToSold()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var store = server.ResolveDependency<DrydockStore>();
            var db = server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            var round = await db.AddNewRound(await db.AddOrGetServer("drydock-test"));

            var shipId = Guid.NewGuid();
            var filed = await store.FileRevision(Request(shipId, owner, "Kestrel"), Encoding.UTF8.GetBytes("doc"), keepBlobs: 2);
            Assert.That(filed.Outcome, Is.EqualTo(DrydockBerthResult.Success));

            // Control: a stored ship is scrapped by the stored sale, never by the live one.
            Assert.That((await store.TrySellLiveShip(shipId, owner, 800, 1000, round)).Outcome, Is.EqualTo(DrydockBerthResult.WrongState));

            // Out on a retrieve: the claim, then the vacate.
            Assert.That(await store.TrySetState(shipId, DrydockShipState.Stored, DrydockShipState.CheckedOut, DrydockAuditAction.Retrieve, owner, round, null), Is.True);
            await store.VacateBerth(shipId);

            var (sold, name) = await store.TrySellLiveShip(shipId, owner, 800, 1000, round);
            Assert.Multiple(() =>
            {
                Assert.That(sold, Is.EqualTo(DrydockBerthResult.Success));
                Assert.That(name, Is.EqualTo("Kestrel"));
            });

            var row = (await store.GetShipsByOwner(owner)).Single(r => r.ShipGuid == shipId);
            Assert.Multiple(() =>
            {
                Assert.That(row.State, Is.EqualTo(DrydockShipState.Sold));
                Assert.That(row.BerthId, Is.Null);
                Assert.That(row.CheckedOutRoundId, Is.Null, "Nothing is out any more, so the stranded query must not find it.");
            });

            var audit = (await store.GetAudit(shipId))[^1];
            Assert.Multiple(() =>
            {
                Assert.That(audit.Action, Is.EqualTo(DrydockAuditAction.ShipSold));
                Assert.That(audit.ActorUserId, Is.EqualTo(owner));
                Assert.That(audit.Reason, Does.Contain("live at the shipyard"));
            });

            var sale = await store.GetLastSale(shipId);
            Assert.That(sale?.Price, Is.EqualTo(800), "The reversal reads the live sale's price the same way it reads a stored one's.");

            Assert.That((await store.TrySellLiveShip(shipId, owner, 800, 1000, round)).Outcome, Is.EqualTo(DrydockBerthResult.WrongState),
                "Sold once.");

            await pair.CleanReturnAsync();
        }

        private static DrydockRevisionRequest Request(
            Guid shipId,
            Guid owner,
            string name,
            bool markStored = true,
            DrydockImpound? impound = null,
            int evicted = 0,
            int? berthId = null) => new()
        {
            Impound = impound,
            Evicted = evicted,
            BerthId = berthId,
            ShipGuid = shipId,
            OwnerUserId = owner,
            ShipName = name,
            VesselProto = "TestVessel",
            SizeClass = nameof(ShipSizeClass.Cutter),
            Kind = DrydockRevisionKind.PlayerStore,
            MarkStored = markStored,
            ActorUserId = impound != null ? impound.ActorUserId : owner,
            CreatedRoundId = null,
            EngineFormatVer = 7,
            ProtoFingerprint = new byte[] { 1, 2, 3 },
            CapturedKeyHash = new byte[] { 4, 5, 6 },
            Checksum = new byte[] { 7, 8, 9 },
            SizeBytes = 23,
            AppraisedValue = 24000,
            Manifest = "{\"v\":1,\"e\":[]}",
        };


        private static Task<(int RevisionCount, int[] BlobRevisions)> ReadRevisionShape(IServerDbManager db, Guid shipId)
        {
            return db.RunTriadDbCommand(async (context, token) =>
            {
                var revisions = await context.DrydockRevision.AsNoTracking()
                    .CountAsync(r => r.ShipGuid == shipId, token);

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
