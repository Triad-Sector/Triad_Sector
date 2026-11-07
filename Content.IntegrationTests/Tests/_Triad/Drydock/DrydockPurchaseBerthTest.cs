#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._NF.Bank;
using Content.Server._NF.SectorServices;
using Content.Server._NF.Shipyard.Components;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Content.Server.Maps;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Server.StationRecords;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Drydock;
using Content.Shared.Containers.ItemSlots;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The berth that comes with a ship, through the real purchase message rather than by calling
    /// the handler: a cash purchase is quoted and charged the vessel price plus a berth of the
    /// vessel's <c>drydockVesselClass</c> row and files exactly one purchased berth at that price,
    /// and a voucher purchase files none and charges nothing for one.
    ///
    /// <para>Connected pairs, because the purchase stamps ownership from the operator's session.
    /// Pooled pairs share one database and a connected pair always has the same account, so every
    /// berth assertion is a difference against the account's own berths read before the press,
    /// never a count.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(ShipyardSystem))]
    public sealed class DrydockPurchaseBerthTest
    {
        /// <summary>
        /// A cheap cash hull with a game map (the purchase handler requires one to set up the ship's
        /// station), no access requirement, and no save blacklist. The controls below say so if any
        /// of that stops being true, rather than failing somewhere inside the purchase.
        /// </summary>
        private const string VesselId = "Framework";

        [Test]
        public async Task ACashPurchaseFilesOnePurchasedBerthAtTheTablePrice()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            using var _ = ExpectClientDockLog(pair);
            var server = pair.Server;
            var entMan = server.EntMan;

            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var shipyard = server.System<ShipyardSystem>();
            var bank = server.System<BankSystem>();

            var vessel = protoMan.Index<VesselPrototype>(VesselId);
            Assert.That(DrydockVesselBerths.TryGetClass(protoMan, VesselId, out var tableClass), Is.True,
                $"Control: {VesselId} has a drydockVesselClass row, so the price below is the table's and not the grid fallback.");
            var berthPrice = DrydockVesselBerths.BerthPrice(protoMan, tableClass);
            AssertCashHull(protoMan, vessel, berthPrice);

            var (console, consoleComp, operatorEnt, owner) = await BuildShipyard(pair, vessel.ID);

            EntityUid card = default;
            var deposited = false;
            await server.WaitPost(() =>
            {
                card = entMan.SpawnEntity("PassengerIDCard", new MapCoordinates(new Vector2(64f, 64f), entMan.GetComponent<TransformComponent>(operatorEnt).MapID));
                server.System<ItemSlotsSystem>().TryInsert(console, consoleComp.TargetIdSlot, card, user: null);

                // Enough for the vessel and its berth with room to spare: the handler refuses a
                // balance that only exactly covers the total.
                entMan.EnsureComponent<BankAccountComponent>(operatorEnt);
                deposited = bank.TryBankDeposit(operatorEnt, vessel.Price + berthPrice + 1000, null);
            });
            Assert.That(deposited, Is.True, "Control: the account takes a deposit, so it can pay.");

            var before = 0;
            await server.WaitAssertion(() =>
            {
                Assert.That(shipyard.HasShipOut(owner), Is.False, "Control: the account has no ship out, so the purchase is not refused for one.");
                before = entMan.GetComponent<BankAccountComponent>(operatorEnt).Balance;
            });
            var berthsBefore = (await store.GetBerths(owner)).Select(s => s.Berth.BerthId).ToHashSet();

            await Press(pair, console, operatorEnt, vessel.ID);

            EntityUid? shuttle = null;
            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.TryGetComponent<ShuttleDeedComponent>(card, out var deed), Is.True, "The purchase went through and deeded the card.");
                Assert.That(deed!.PurchasedWithVoucher, Is.False);
                shuttle = deed.ShuttleUid;
                Assert.That(entMan.GetComponent<BankAccountComponent>(operatorEnt).Balance, Is.EqualTo(before - vessel.Price - berthPrice),
                    "The vessel and its berth were each charged once, at the vessel price and the table price.");
            });

            var added = await WaitForNewBerths(pair, store, owner, berthsBefore, TimeSpan.FromSeconds(30));
            Assert.That(added, Has.Count.EqualTo(1), "Exactly one berth comes with the ship.");
            Assert.Multiple(() =>
            {
                Assert.That(added[0].Kind, Is.EqualTo(DrydockBerthKind.Purchased));
                Assert.That(added[0].PricePaid, Is.EqualTo(berthPrice), "The berth records the price the table quoted.");
                Assert.That(added[0].MaxSizeClass, Is.EqualTo(tableClass.ToString()), "The berth is of the table's class.");
            });

            await DeleteShuttle(pair, shuttle);
            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task AVoucherPurchaseFilesNoBerthAndChargesNothingForOne()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            using var _ = ExpectClientDockLog(pair);
            var server = pair.Server;
            var entMan = server.EntMan;

            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var bank = server.System<BankSystem>();

            var vessel = protoMan.Index<VesselPrototype>(VesselId);
            Assert.That(DrydockVesselBerths.TryGetBerthPrice(protoMan, VesselId, out var berthPrice), Is.True);
            AssertCashHull(protoMan, vessel, berthPrice);

            var (console, consoleComp, operatorEnt, owner) = await BuildShipyard(pair, vessel.ID);

            EntityUid card = default;
            var deposited = false;
            await server.WaitPost(() =>
            {
                card = entMan.SpawnEntity(null, new MapCoordinates(new Vector2(64f, 64f), entMan.GetComponent<TransformComponent>(operatorEnt).MapID));
                var voucher = entMan.EnsureComponent<ShipyardVoucherComponent>(card);
                voucher.Vessels.Add(vessel.ID);
                voucher.ConsoleTypes.Add(ShipyardConsoleUiKey.Shipyard);
                server.System<ItemSlotsSystem>().TryInsert(console, consoleComp.TargetIdSlot, card, user: null);

                // Money in the account, so "charged nothing" is a balance that could have moved.
                entMan.EnsureComponent<BankAccountComponent>(operatorEnt);
                deposited = bank.TryBankDeposit(operatorEnt, vessel.Price + berthPrice + 1000, null);
            });
            Assert.That(deposited, Is.True, "Control: the account takes a deposit, so a charge would show.");

            var before = 0;
            await server.WaitAssertion(() => before = entMan.GetComponent<BankAccountComponent>(operatorEnt).Balance);
            var berthsBefore = (await store.GetBerths(owner)).Select(s => s.Berth.BerthId).ToHashSet();

            await Press(pair, console, operatorEnt, vessel.ID);

            EntityUid? shuttle = null;
            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.TryGetComponent<ShuttleDeedComponent>(card, out var deed), Is.True, "Control: the voucher purchase went through.");
                Assert.That(deed!.PurchasedWithVoucher, Is.True);
                shuttle = deed.ShuttleUid;
                Assert.That(entMan.GetComponent<BankAccountComponent>(operatorEnt).Balance, Is.EqualTo(before),
                    "A voucher hull costs nothing, the berth included.");
            });

            // The handler decides synchronously and files nothing for a voucher hull, so there is no
            // write to wait for. The window only gives a wrongly filed berth time to land.
            var added = await WaitForNewBerths(pair, store, owner, berthsBefore, TimeSpan.FromSeconds(3));
            Assert.That(added, Is.Empty, "A voucher purchase brings no berth with it.");

            await DeleteShuttle(pair, shuttle);
            await pair.CleanReturnAsync();
        }

        /// <summary>The fixture's assumptions about the hull, asserted so a content change fails here by name.</summary>
        private static void AssertCashHull(IPrototypeManager protoMan, VesselPrototype vessel, int berthPrice)
        {
            Assert.Multiple(() =>
            {
                Assert.That(vessel.Purchasable, Is.True, $"Control: {vessel.ID} is sold for cash.");
                Assert.That(vessel.Price, Is.GreaterThan(0), $"Control: {vessel.ID} has a price; the handler returns early on a free one.");
                Assert.That(vessel.Access, Is.Empty, $"Control: {vessel.ID} needs no access to buy.");
                Assert.That(vessel.AddComponents.ContainsKey("ShipSavingBlacklist"), Is.False, $"Control: {vessel.ID} is not save-blacklisted, so it is quoted a berth.");
                Assert.That(protoMan.HasIndex<GameMapPrototype>(vessel.ID), Is.True, $"Control: {vessel.ID} has the game map the purchase handler sets its station up from.");
                Assert.That(berthPrice, Is.GreaterThan(0), "Control: the berth ladder prices the table class, so the charge is visible.");
            });
        }

        /// <summary>
        /// A station on a test grid and a shipyard console on it that lists <paramref name="vesselId"/>,
        /// with the connected session's operator standing clear of both. The drydock is switched on.
        /// </summary>
        private static async Task<(EntityUid Console, ShipyardConsoleComponent Comp, EntityUid Operator, Guid Owner)> BuildShipyard(TestPair pair, string vesselId)
        {
            var server = pair.Server;
            var entMan = server.EntMan;

            var cfg = server.ResolveDependency<IConfigurationManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var shipyard = server.System<ShipyardSystem>();
            var stationSys = server.System<StationSystem>();

            var map = await pair.CreateTestMap();
            var session = playerMan.Sessions.First();

            EntityUid console = default;
            EntityUid operatorEnt = default;
            ShipyardConsoleComponent comp = default!;

            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);

                shipyard.SetupShipyardIfNeeded();

                var station = entMan.Spawn();
                entMan.AddComponent<StationDataComponent>(station);
                // The sector service host a round's BaseStation carries: the purchase files a shuttle
                // record onto the entity its init spawns.
                entMan.AddComponent<StationSectorServiceHostComponent>(station);
                // And its records: the purchase synchronizes the selling station's records after
                // copying the captain's record onto the new hull's station.
                entMan.AddComponent<StationRecordsComponent>(station);
                stationSys.AddGridToStation(station, map.Grid.Owner);

                operatorEnt = entMan.SpawnEntity(null, new MapCoordinates(new Vector2(64f, 64f), map.MapId));
                playerMan.SetAttachedEntity(session, operatorEnt);

                // On the station's grid, so the console's owning station is the one the hull docks to.
                console = entMan.SpawnEntity(null, new EntityCoordinates(map.Grid.Owner, new Vector2(0.5f, 0.5f)));
                comp = entMan.EnsureComponent<ShipyardConsoleComponent>(console);

                // The purchase checks the vessel against what the console offers; with no open
                // interface to read a key from, the listing is what offers it.
                entMan.EnsureComponent<ShipyardListingComponent>(console).Shuttles.Add(vesselId);
            });

            await pair.MakeCleanupImmune(map.Grid.Owner);
            await pair.RunTicksSync(5);

            return (console, comp, operatorEnt, session.UserId.UserId);
        }

        /// <summary>Raises the purchase message on the console as the interface would deliver it, then lets the ship settle.</summary>
        private static async Task Press(TestPair pair, EntityUid console, EntityUid operatorEnt, string vesselId)
        {
            await pair.Server.WaitPost(() =>
            {
                var message = new ShipyardConsolePurchaseMessage(vesselId)
                {
                    Actor = operatorEnt,
                    UiKey = ShipyardConsoleUiKey.Shipyard,
                };
                pair.Server.EntMan.EventBus.RaiseLocalEvent(console, message);
            });

            await pair.RunTicksSync(5);
        }

        /// <summary>
        /// The account's berths that were not there before the press, re-read while pumping ticks
        /// until one appears or the wall-clock window closes. The purchased berth is written by a
        /// fire-and-forget database call, so a tick count does not measure how long it takes.
        /// </summary>
        private static async Task<List<DrydockBerth>> WaitForNewBerths(TestPair pair, DrydockStore store, Guid owner, HashSet<int> before, TimeSpan window)
        {
            var clock = Stopwatch.StartNew();
            List<DrydockBerth> added;
            do
            {
                await pair.RunTicksSync(1);
                added = (await store.GetBerths(owner)).Select(s => s.Berth).Where(b => !before.Contains(b.BerthId)).ToList();
            }
            while (added.Count == 0 && clock.Elapsed < window);

            // A second read after the first berth lands, so a duplicate written a moment later is
            // counted rather than missed.
            if (added.Count > 0)
            {
                await pair.RunTicksSync(5);
                added = (await store.GetBerths(owner)).Select(s => s.Berth).Where(b => !before.Contains(b.BerthId)).ToList();
            }

            return added;
        }

        /// <summary>
        /// The bought hull out of the world, so the one-ship-out rule does not follow the account into
        /// the next test on this pair.
        /// </summary>
        private static async Task DeleteShuttle(TestPair pair, EntityUid? shuttle)
        {
            if (shuttle is not { } grid)
                return;

            await pair.Server.WaitPost(() =>
            {
                if (!pair.Server.EntMan.Deleted(grid))
                    pair.Server.EntMan.DeleteEntity(grid);
            });

            await pair.RunTicksSync(3);
        }

        /// <summary>
        /// A purchase docks the new hull to the station at once, which logs a benign client-side
        /// joint error the pool would count as a failure (see DrydockConsoleTest's note on its dock
        /// joint log). The client's failure level is raised for the test and put back after; the
        /// server keeps its full sensitivity.
        /// </summary>
        private static IDisposable ExpectClientDockLog(TestPair pair)
        {
            var level = pair.ClientLogHandler.FailureLevel;
            pair.ClientLogHandler.FailureLevel = LogLevel.Fatal;
            return new RestoreScope(() => pair.ClientLogHandler.FailureLevel = level);
        }

        private sealed class RestoreScope(Action restore) : IDisposable
        {
            public void Dispose() => restore();
        }
    }
}
