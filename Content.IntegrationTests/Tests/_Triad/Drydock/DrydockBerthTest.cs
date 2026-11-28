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
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Log;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The berth model, pinned at the store layer against a real database. The invariants worth
    /// having here are the ones the schema promises rather than the code: one hull per berth and
    /// a ship never sitting in another owner's berth are both asserted by provoking the database
    /// directly, so a future refactor of the store that forgets either still fails.
    ///
    /// <para>Everything is scoped to freshly minted owners, so the rows this leaves in a pooled
    /// server's database cannot be seen by any other test, and no assertion here counts rows.</para>
    /// </summary>
    [TestFixture]
    public sealed class DrydockBerthTest
    {
        [Test]
        public async Task AStoreTakesTheSmallestFittingBerthAndPrefersItsOwnOldSlot()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);

            // Two cutter slots and one frigate slot. Smallest-first means a cutter never takes the
            // frigate slot while a cutter slot is free.
            var cutterA = await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            var cutterB = await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            var frigate = await store.AddBerth(owner, ShipSizeClass.Frigate, DrydockBerthKind.Granted, 0, null, null);

            var first = Guid.NewGuid();
            var second = Guid.NewGuid();

            var filedFirst = await store.FileRevision(Request(first, owner, "First", ShipSizeClass.Cutter), Blob(), keepBlobs: 2);
            var filedSecond = await store.FileRevision(Request(second, owner, "Second", ShipSizeClass.Cutter), Blob(), keepBlobs: 2);

            Assert.Multiple(() =>
            {
                Assert.That(filedFirst.Outcome, Is.EqualTo(DrydockBerthResult.Success));
                Assert.That(filedFirst.BerthId, Is.EqualTo(cutterA), "The first cutter takes the lowest-numbered cutter slot.");
                Assert.That(filedSecond.BerthId, Is.EqualTo(cutterB), "The second cutter takes the other cutter slot, not the frigate slot.");
            });

            // Both go out. Bringing the second one back first is what separates "prefers its own
            // old slot" from "takes the lowest-numbered free slot", because those disagree here.
            await store.VacateBerth(first);
            await store.VacateBerth(second);

            var slotsAfterVacate = await store.GetBerths(owner);
            Assert.That(slotsAfterVacate.Select(s => s.Occupant), Is.All.Null, "A vacated berth is empty as far as the owner can see.");

            var secondAgain = await store.FileRevision(Request(second, owner, "Second", ShipSizeClass.Cutter), Blob(), keepBlobs: 2);
            Assert.That(secondAgain.BerthId, Is.EqualTo(cutterB), "A ship goes back to the slot it came out of when that slot is still free.");

            // A cruiser fits nothing here, but a corvette fits the frigate slot and nothing else.
            var corvette = Guid.NewGuid();
            var filedCorvette = await store.FileRevision(Request(corvette, owner, "Corvette", ShipSizeClass.Corvette), Blob(), keepBlobs: 2);
            Assert.That(filedCorvette.BerthId, Is.EqualTo(frigate));

            var slots = await store.GetBerths(owner);
            Assert.That(slots.Single(s => s.Berth.BerthId == frigate).Occupant?.ShipGuid, Is.EqualTo(corvette));

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task AStoreWithNowhereToGoRefusesAndFilesNothing()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var occupant = Guid.NewGuid();
            await store.FileRevision(Request(occupant, owner, "Occupant", ShipSizeClass.Cutter), Blob(), keepBlobs: 2);

            // The only slot is taken: no free berth at all.
            var crowded = Guid.NewGuid();
            var refusedFull = await store.FileRevision(Request(crowded, owner, "Crowded", ShipSizeClass.Cutter), Blob(), keepBlobs: 2);
            Assert.That(refusedFull.Outcome, Is.EqualTo(DrydockBerthResult.NoBerth));
            Assert.That(await store.LoadCurrent(crowded), Is.Null, "A refused store files no hull row and no revision.");

            // Free the slot, then bring a hull it cannot hold: a free berth exists, none fits.
            await store.VacateBerth(occupant);

            var big = Guid.NewGuid();
            var refusedSize = await store.FileRevision(Request(big, owner, "Big", ShipSizeClass.Frigate), Blob(), keepBlobs: 2);
            Assert.That(refusedSize.Outcome, Is.EqualTo(DrydockBerthResult.BerthTooSmall),
                "Too-small is its own answer because its fix is different from no-berth.");
            Assert.That(await store.LoadCurrent(big), Is.Null);

            // The advisory pre-check the pipeline runs before touching the grid agrees.
            Assert.That(await store.CheckBerthForStore(big, owner, nameof(ShipSizeClass.Frigate)), Is.EqualTo(DrydockBerthResult.BerthTooSmall));
            Assert.That(await store.CheckBerthForStore(Guid.NewGuid(), owner, nameof(ShipSizeClass.Cutter)), Is.EqualTo(DrydockBerthResult.Success));

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task OneHullPerBerthAndOwnerMatchingAreDatabaseFacts()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var stranger = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await DrydockTestHelpers.InsertPlayer(db, stranger);

            var ownBerth = await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            var spare = await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            var strangersBerth = await store.AddBerth(stranger, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var seated = Guid.NewGuid();
            var other = Guid.NewGuid();
            var filed = await store.FileRevision(Request(seated, owner, "Seated", ShipSizeClass.Cutter), Blob(), keepBlobs: 2);
            Assert.That(filed.BerthId, Is.EqualTo(ownBerth));
            await store.FileRevision(Request(other, owner, "Other", ShipSizeClass.Cutter), Blob(), keepBlobs: 2);

            // Both faults below are provoked by writing the row directly, past every check in the
            // store, so what refuses can only be the schema. The database logs the failed command
            // at error level, which a pooled pair treats as a test failure; lift the bar for the
            // two statements that are supposed to fail and put it straight back.
            var failureLevel = pair.ServerLogHandler.FailureLevel;
            pair.ServerLogHandler.FailureLevel = LogLevel.Fatal;

            Assert.ThrowsAsync<DbUpdateException>(() => SetBerthDirectly(db, other, ownBerth),
                "Two hulls in one berth must be refused by the unique index, whatever the code above it does.");

            Assert.ThrowsAsync<DbUpdateException>(() => SetBerthDirectly(db, other, strangersBerth),
                "A ship in another owner's berth must be refused by the composite foreign key.");

            pair.ServerLogHandler.FailureLevel = failureLevel;

            // And the same two through the store, which answers rather than throws.
            var occupied = await store.TryMoveShip(other, ownBerth, null, null, "test");
            Assert.That(occupied, Is.EqualTo(DrydockBerthResult.BerthOccupied));

            var crossOwner = await store.TryMoveShip(other, strangersBerth, null, null, "test");
            Assert.That(crossOwner, Is.EqualTo(DrydockBerthResult.NotFound), "Another owner's berth is not a berth this ship can see.");

            // The spare went to the second ship above, so a genuinely free slot is needed here.
            var third = await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            var reseated = await store.TryMoveShip(seated, third, null, null, "test");
            Assert.That(reseated, Is.EqualTo(DrydockBerthResult.Success));
            Assert.That((await store.LoadCurrent(seated))!.Ship.BerthId, Is.EqualTo(third));

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task ATransferMovesOwnerAndBerthTogether()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var seller = Guid.NewGuid();
            var buyer = Guid.NewGuid();
            var pauper = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, seller);
            await DrydockTestHelpers.InsertPlayer(db, buyer);
            await DrydockTestHelpers.InsertPlayer(db, pauper);

            var sellersBerth = await store.AddBerth(seller, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            var buyersBerth = await store.AddBerth(buyer, ShipSizeClass.Corvette, DrydockBerthKind.Granted, 0, null, null);

            var ship = Guid.NewGuid();
            await store.FileRevision(Request(ship, seller, "Sold", ShipSizeClass.Cutter), Blob(), keepBlobs: 2);

            // Nowhere to put it on the recipient's side is the same refusal a store gives, and it
            // comes at the offer, before anyone has waited thirty minutes to hear it.
            var (noRoom, _) = await store.TryOfferTransfer(ship, seller, pauper, TimeSpan.FromMinutes(30), null);
            Assert.That(noRoom, Is.EqualTo(DrydockBerthResult.NoBerth));
            Assert.That((await store.LoadCurrent(ship))!.Ship.OwnerUserId, Is.EqualTo(seller), "A refused offer changes nothing.");

            // Only the owner may give it away.
            var (notYours, _) = await store.TryOfferTransfer(ship, buyer, pauper, TimeSpan.FromMinutes(30), null);
            Assert.That(notYours, Is.EqualTo(DrydockBerthResult.NotFound));

            var (offered, offer) = await store.TryOfferTransfer(ship, seller, buyer, TimeSpan.FromMinutes(30), null);
            Assert.That(offered, Is.EqualTo(DrydockBerthResult.Success));

            var (moved, berth, name) = await store.TryAcceptTransfer(offer!.Id, buyer, null);
            Assert.Multiple(() =>
            {
                Assert.That(moved, Is.EqualTo(DrydockBerthResult.Success));
                Assert.That(berth, Is.EqualTo(buyersBerth));
                Assert.That(name, Is.EqualTo("Sold"));
            });

            var after = (await store.LoadCurrent(ship))!.Ship;
            Assert.Multiple(() =>
            {
                Assert.That(after.OwnerUserId, Is.EqualTo(buyer));
                Assert.That(after.BerthId, Is.EqualTo(buyersBerth), "The ship sits in the buyer's berth, never the seller's.");
                Assert.That(after.State, Is.EqualTo(DrydockShipState.Stored));
            });

            var sellersSlots = await store.GetBerths(seller);
            Assert.That(sellersSlots.Single(s => s.Berth.BerthId == sellersBerth).Occupant, Is.Null, "The seller keeps an empty berth.");

            // The timeline reads the direction the ship went: the giver did it to the recipient,
            // whoever pressed the button.
            var audit = await store.GetAudit(ship);
            var transfer = audit.Single(a => a.Action == DrydockAuditAction.Transfer);
            Assert.Multiple(() =>
            {
                Assert.That(transfer.ActorUserId, Is.EqualTo(seller));
                Assert.That(transfer.SubjectUserId, Is.EqualTo(buyer));
                Assert.That(transfer.BerthId, Is.EqualTo(buyersBerth));
            });

            // An offer resolves once. A second accept of the same offer, which is what a decline or
            // an expiry landing in the same instant looks like from the other side, finds nothing.
            var (again, _, _) = await store.TryAcceptTransfer(offer.Id, buyer, null);
            Assert.That(again, Is.EqualTo(DrydockBerthResult.NotFound));

            // A ship that is out cannot change hands: there is nothing in the drydock to hand over.
            await store.TrySetState(ship, DrydockShipState.Stored, DrydockShipState.CheckedOut, DrydockAuditAction.Retrieve, buyer, null, null);
            var (outNow, _) = await store.TryOfferTransfer(ship, buyer, seller, TimeSpan.FromMinutes(30), null);
            Assert.That(outNow, Is.EqualTo(DrydockBerthResult.WrongState));

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task SellingRefundsWhatWasPaidAndNeverAnOccupiedBerth()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);

            // A grant records nothing paid whatever it is told, so it can never be sold for credits.
            var granted = await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 9999, null, null);
            var bought = await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Purchased, 500, owner, null);

            var ship = Guid.NewGuid();
            var filed = await store.FileRevision(Request(ship, owner, "Parked", ShipSizeClass.Cutter), Blob(), keepBlobs: 2);
            Assert.That(filed.BerthId, Is.EqualTo(granted), "Lowest-numbered fitting slot first.");

            var (occupied, _) = await store.TryRemoveBerth(granted, owner, DrydockAuditAction.BerthSale, owner, null);
            Assert.That(occupied, Is.EqualTo(DrydockBerthResult.BerthOccupied), "A berth with a ship in it does not sell.");

            var (notMine, _) = await store.TryRemoveBerth(bought, Guid.NewGuid(), DrydockAuditAction.BerthSale, null, null);
            Assert.That(notMine, Is.EqualTo(DrydockBerthResult.NotFound), "Somebody else cannot sell my berth.");

            var (sold, soldRow) = await store.TryRemoveBerth(bought, owner, DrydockAuditAction.BerthSale, owner, null);
            Assert.That(sold, Is.EqualTo(DrydockBerthResult.Success));
            Assert.That(soldRow!.PricePaid, Is.EqualTo(500), "The refund basis is what was actually paid.");

            await store.VacateBerth(ship);
            var (soldGrant, grantRow) = await store.TryRemoveBerth(granted, owner, DrydockAuditAction.BerthSale, owner, null);
            Assert.That(soldGrant, Is.EqualTo(DrydockBerthResult.Success));
            Assert.That(grantRow!.PricePaid, Is.Zero, "A grant refunds nothing.");

            // Upgrading is a real payment on top of whatever the base was.
            var small = await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            Assert.That(await store.TryUpgradeBerth(small, owner, ShipSizeClass.Cutter, 100, owner, null), Is.EqualTo(DrydockBerthResult.WrongState),
                "An upgrade has to go up.");
            Assert.That(await store.TryUpgradeBerth(small, owner, ShipSizeClass.Cruiser, 300, owner, null), Is.EqualTo(DrydockBerthResult.Success));

            var upgraded = (await store.GetBerths(owner)).Single(s => s.Berth.BerthId == small).Berth;
            Assert.Multiple(() =>
            {
                Assert.That(upgraded.MaxSizeClass, Is.EqualTo(nameof(ShipSizeClass.Cruiser)));
                Assert.That(upgraded.PricePaid, Is.EqualTo(300), "The delta paid for the upgrade is refundable; the free base is not.");
                Assert.That(upgraded.Kind, Is.EqualTo(DrydockBerthKind.Purchased));
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task AnAdminRestoresAShipThatIsOutIntoABerthThatFits()
        {
            await using var pair = await PoolManager.GetServerClient();
            var store = pair.Server.ResolveDependency<DrydockStore>();
            var db = pair.Server.ResolveDependency<IServerDbManager>();

            var owner = Guid.NewGuid();
            var admin = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await DrydockTestHelpers.InsertPlayer(db, admin);

            var corvetteSlot = await store.AddBerth(owner, ShipSizeClass.Corvette, DrydockBerthKind.Granted, 0, null, null);
            var cutterSlot = await store.AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            var ship = Guid.NewGuid();
            var filed = await store.FileRevision(Request(ship, owner, "Lost", ShipSizeClass.Corvette), Blob(), keepBlobs: 2);
            Assert.That(filed.BerthId, Is.EqualTo(corvetteSlot));

            Assert.That(await store.TryRestoreShip(ship, corvetteSlot, admin, null, "already home"), Is.EqualTo(DrydockBerthResult.WrongState),
                "A ship that is stored has nothing to restore.");
            Assert.That(await store.TryMoveShip(ship, null, admin, null, "vacate"), Is.EqualTo(DrydockBerthResult.WrongState),
                "A stored ship lives in its berth: vacating it would make a stored ship with nowhere to be.");

            // Out, and then lost: the retrieve claim with no store ever coming. The vacate is the
            // admin's, which is the repair for a hull that went out and was left shown in its slot.
            await store.TrySetState(ship, DrydockShipState.Stored, DrydockShipState.CheckedOut, DrydockAuditAction.Retrieve, owner, null, null);
            Assert.That(await store.TryMoveShip(ship, null, admin, null, "still shown in its slot"), Is.EqualTo(DrydockBerthResult.Success));
            Assert.That((await store.LoadCurrent(ship))!.Ship.BerthId, Is.Null);

            Assert.That(await store.TryRestoreShip(ship, cutterSlot, admin, null, "wrong slot"), Is.EqualTo(DrydockBerthResult.BerthTooSmall),
                "Fit is enforced for admins too; grant a fitting berth instead.");

            Assert.That(await store.TryRestoreShip(ship, corvetteSlot, admin, null, "hull lost to a bug"), Is.EqualTo(DrydockBerthResult.Success));

            var restored = (await store.LoadCurrent(ship))!.Ship;
            Assert.Multiple(() =>
            {
                Assert.That(restored.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(restored.BerthId, Is.EqualTo(corvetteSlot));
                Assert.That(restored.CheckedOutRoundId, Is.Null);
            });

            var audit = await store.GetAudit(ship);
            var restore = audit.Single(a => a.Action == DrydockAuditAction.Restore);
            Assert.Multiple(() =>
            {
                Assert.That(restore.ActorUserId, Is.EqualTo(admin));
                Assert.That(restore.Reason, Is.EqualTo("hull lost to a bug"), "The adjudication's reasoning is the row's whole point.");
                Assert.That(restore.BerthId, Is.EqualTo(corvetteSlot));
            });

            await pair.CleanReturnAsync();
        }

        private static byte[] Blob() => Encoding.UTF8.GetBytes("document");

        private static DrydockRevisionRequest Request(Guid shipId, Guid owner, string name, ShipSizeClass sizeClass) => new()
        {
            ShipGuid = shipId,
            OwnerUserId = owner,
            ShipName = name,
            VesselProto = "TestVessel",
            SizeClass = sizeClass.ToString(),
            Kind = DrydockRevisionKind.PlayerStore,
            MarkStored = true,
            ActorUserId = owner,
            CreatedRoundId = null,
            EngineFormatVer = 7,
            ProtoFingerprint = new byte[] { 1, 2, 3 },
            CapturedKeyHash = new byte[] { 4, 5, 6 },
            Checksum = new byte[] { 7, 8, 9 },
            SizeBytes = 8,
            Manifest = "{\"v\":1,\"e\":[]}",
        };

        /// <summary>Writes the berth column past every check in the store, so only the schema can refuse it.</summary>
        private static Task SetBerthDirectly(IServerDbManager db, Guid shipId, int berthId)
        {
            return db.RunTriadDbCommand(async (context, token) =>
            {
                var ship = await context.DrydockShip.SingleAsync(s => s.ShipGuid == shipId, token);
                ship.BerthId = berthId;
                await context.SaveChangesAsync(token);
            }, CancellationToken.None);
        }

    }
}
