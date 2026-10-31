#nullable enable

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._NF.Bank;
using Content.Server._NF.Shipyard.Components;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._NF.SectorServices;
using Content.Server._NF.ShuttleRecords;
using Content.Server._NF.Station.Components;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Content.Shared._NF.Bank.Components;
using Content.Server.Maps;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.BUI;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Shipyard.Save;
using Content.Shared._Triad.ShipSize;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Shuttles.Components;
using Content.Shared.Station.Components;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The console tier: the half a player actually touches. Everything else in this folder proves
    /// the pipeline moves a ship correctly; nothing until now proved a person standing at a console
    /// can reach it, or that the console refuses the people it should.
    ///
    /// <para>These need a connected pair, because the console resolves the operator's account from
    /// a real player session and the default pooled pair has none.</para>
    ///
    /// <para>The operator entity is deliberately spawned well off the ship. A session-bearing mob
    /// standing on the grid trips the organics gate, which is that gate working rather than a
    /// problem with the test.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(ShipyardSystem))]
    public sealed class DrydockConsoleTest
    {
        /// <summary>
        /// The whole player-facing loop in one pass: store from the console, see the ship appear in
        /// the retrieve list, retrieve it, and get a working deed back.
        ///
        /// <para>The two deed assertions are the point. A store leaves the card pointing at a grid
        /// that no longer exists, so the deed has to come off; a retrieve produces a ship nobody
        /// holds a claim on, so a fresh one has to be minted. Get either backwards and the ship is
        /// either unflyable or the card is a handle on nothing.</para>
        /// </summary>
        [Test]
        public async Task AnOwnerCanStoreAndRetrieveFromTheConsole()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            using var _ = ExpectDockJointLog(pair);
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var shipyard = server.System<ShipyardSystem>();
            var drydockStore = server.ResolveDependency<DrydockStore>();

            var session = playerMan.Sessions.First();

            // A store needs a berth on the operator's account; the console is not where berths
            // are bought, so grant one.
            await server.ResolveDependency<DrydockStore>().AddBerth(session.UserId.UserId, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            // A helm locked to the ship, the way a purchase leaves every console aboard. Its lock
            // holds the ship's uid as a string, which is the one reference the loader cannot remap.
            var consoleLock = server.System<ShuttleConsoleLockSystem>();
            EntityUid helm = default;
            await server.WaitPost(() =>
            {
                helm = entMan.SpawnEntity(null, new EntityCoordinates(ship, new Vector2(1.5f, 1.5f)));
                var lockComp = entMan.EnsureComponent<ShuttleConsoleLockComponent>(helm);
                consoleLock.SetShuttleId(helm, ship.ToString(), lockComp);
            });

            // The control for the store assertion below: the card has to be carrying a deed before
            // the store, or "the deed came off" proves nothing. Same for the helm: it has to be
            // keyed to the hull that is about to be stored, or "it came back re-keyed" proves nothing.
            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.HasComponent<ShuttleDeedComponent>(card), Is.True,
                    "The console store resolves its ship from this deed; without it the test never reaches the pipeline.");
                Assert.That(entMan.GetComponent<ShuttleConsoleLockComponent>(helm).ShuttleId, Is.EqualTo(ship.ToString()));
            });

            var stored = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));

            Assert.That(stored, Is.Not.Null, "A store by the ship's own owner must reach the pipeline rather than being refused at the console.");
            Assert.That(stored!.Value.Result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.Deleted(ship), Is.True, "A successful store despawns the grid.");
                Assert.That(entMan.HasComponent<ShuttleDeedComponent>(card), Is.False,
                    "The deed points at a grid that no longer exists, so the store has to strip it, as selling does.");
            });

            // Identity, never counts. Pooled pairs share one database, so ships filed by earlier
            // tests in this run are still on this account and any assertion on list length is
            // really an assertion about test ordering.
            var shipId = stored.Value.ShipId!.Value;

            // Separates "the store filed nothing under this account" from "the console dropped it":
            // if this row is here and the tab does not show it, the fault is the console's filter.
            var rows = await DrydockTestHelpers.RunOnServer(pair, () => drydockStore.GetShipsByOwner(session.UserId.UserId));
            var filed = rows.SingleOrDefault(r => r.ShipGuid == shipId);
            Assert.That(filed, Is.Not.Null,
                "The store must file the ship under the operating player's own account, or nothing downstream can find it.");
            Assert.That(filed!.State, Is.EqualTo(DrydockShipState.Stored),
                "A ship that has just been put away has to be in the state the console lists on.");

            // The list the drydock tab renders. It is filled by an awaited database read, so it is
            // the console's own view of what this player may retrieve.
            await DrydockTestHelpers.RunOnServer(pair, async () =>
            {
                await shipyard.RefreshDrydockState(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
                return true;
            });

            await server.WaitAssertion(() =>
            {
                var listed = consoleComp.CachedDrydock.StoredShips.SingleOrDefault(s => s.ShipId == shipId);
                Assert.That(listed, Is.Not.Null,
                    "The ship was just stored by this operator, so it has to be offered back to them.");
                Assert.That(listed!.Name, Is.EqualTo("Kestrel"));
            });

            var retrieved = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));

            Assert.That(retrieved, Is.Not.Null, "The owner must be able to take back the ship they just put away.");

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.HasComponent<ShuttleDeedComponent>(card), Is.True,
                    "A retrieved ship nobody holds a claim on is unflyable; retrieve has to mint a fresh deed onto the card.");

                var deed = entMan.GetComponent<ShuttleDeedComponent>(card);
                Assert.That(deed.ShuttleUid, Is.EqualTo(retrieved!.Value),
                    "The minted deed has to point at the ship that actually came back.");

                // The helm came back with the ship; the lock on it has to name the ship that came
                // back, because the unlock compares the deed's uid with the lock's as strings and a
                // lock still naming the stored hull refuses the captain's own deed.
                ShuttleConsoleLockComponent? retrievedLock = null;
                var locks = entMan.AllEntityQueryEnumerator<ShuttleConsoleLockComponent, TransformComponent>();
                while (locks.MoveNext(out var lockUid, out var lockComp, out var xform))
                {
                    if (xform.GridUid == retrieved.Value)
                        retrievedLock = lockComp;
                }

                Assert.That(retrievedLock, Is.Not.Null, "The locked helm has to come back with the ship.");
                Assert.That(retrievedLock!.ShuttleId, Is.EqualTo(retrieved.Value.ToString()),
                    "A console lock holds the ship's uid as a string; retrieve has to re-key it to the reborn grid or the deed never opens it.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The imported-hull shape: a document that brings no grid-side deed. The ship-save exporter
        /// strips <c>ShuttleDeed</c> from everything it writes, so every legacy import reaches the
        /// drydock deed-less, and the record filing reads the grid deed to build its row. Until
        /// retrieve minted both halves an imported hull filed no shuttle record at all, so the
        /// records console showed no trace of a ship that was flying.
        /// </summary>
        [Test]
        public async Task AnImportedHullComesBackWithAGridDeedAndARecord()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            using var _ = ExpectDockJointLog(pair);
            var server = pair.Server;
            var entMan = server.EntMan;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var shipyard = server.System<ShipyardSystem>();
            var records = server.System<ShuttleRecordsSystem>();

            var session = playerMan.Sessions.First();
            var (_, _, ship, console, consoleComp, _, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            // The import shape, made by hand: the grid-side deed comes off the way the exporter's
            // strip list takes it. The card deed stays, because the console resolves the ship to
            // store from it and an import's own first store is reached through the import path.
            await server.WaitPost(() => entMan.RemoveComponent<ShuttleDeedComponent>(ship));

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.HasComponent<ShuttleDeedComponent>(ship), Is.False,
                    "The control: with a grid deed still on the hull, a deed after the retrieve proves nothing.");
                Assert.That(records.TryGetRecord(entMan.GetNetEntity(ship), out var existing), Is.False,
                    $"Nothing has filed this hull yet, so a record found here ('{existing?.Name}') would belong to another test.");
            });

            var stored = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));

            Assert.That(stored, Is.Not.Null, "A store of the operator's own ship must reach the pipeline.");
            Assert.That(stored!.Value.Result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, stored.Value.ShipId!.Value, ShipyardConsoleUiKey.Shipyard));

            Assert.That(retrieved, Is.Not.Null, "A hull with no grid deed still belongs to the account that filed it.");

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var grid = retrieved!.Value;

                Assert.That(entMan.TryGetComponent<ShuttleDeedComponent>(grid, out var gridDeed), Is.True,
                    "A hull whose document carried no deed has to be minted one, or it files no record and takes no name stamp.");
                Assert.That(gridDeed!.ShuttleUid, Is.EqualTo(grid), "The minted grid deed has to name its own hull.");
                Assert.That(gridDeed.LoadedFromSave, Is.False,
                    "That flag bars a sale at every shipyard console, and a retrieved hull sells like a purchased one.");

                Assert.That(records.TryGetRecord(entMan.GetNetEntity(grid), out var record), Is.True,
                    "The records console reads this list and nothing else, so an unfiled hull is invisible on it.");
                Assert.That(record!.Name, Is.EqualTo("Kestrel"),
                    "The record carries the row's name, which is the name standing on the hull.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Vessel identity, recovered from the grid when the row has none. The store reads the id off
        /// the ship's own station, and a legacy import is staged into the <em>console's</em> station,
        /// which carries no vessel information - so an imported hull filed with no vessel and came
        /// back on a plain station every time, without its class's job slots or priority dock tag.
        /// The grid's own <c>VesselComponent</c> is written by the purchase and is stripped by
        /// neither the exporter nor the store, which is what makes it recoverable at all.
        ///
        /// <para>The fixture ship is a station member of nothing, which is the half of the import
        /// shape that matters here: the station branch of the read has nothing to answer with.</para>
        /// </summary>
        [Test]
        public async Task AVesselIdOnTheGridIsRecoveredWhenTheRowHasNone()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            using var _ = ExpectDockJointLog(pair);
            var server = pair.Server;
            var entMan = server.EntMan;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var drydockStore = server.ResolveDependency<DrydockStore>();
            var shipyard = server.System<ShipyardSystem>();
            var stationSys = server.System<StationSystem>();

            var session = playerMan.Sessions.First();
            var (_, _, ship, console, consoleComp, _, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            // Read out of live data rather than named here: the recovery is only worth anything for a
            // vessel whose gameMap declares a station under its own id, which is the exact shape
            // RecreateStation asks for, and a hardcoded id rots the day that vessel is renamed.
            var vesselId = string.Empty;
            await server.WaitAssertion(() =>
            {
                vesselId = protoMan.EnumeratePrototypes<GameMapPrototype>()
                    .Where(m => m.Stations.ContainsKey(m.ID) && protoMan.HasIndex<VesselPrototype>(m.ID))
                    .OrderBy(m => m.ID)
                    .Select(m => m.ID)
                    .FirstOrDefault() ?? string.Empty;

                Assert.That(vesselId, Is.Not.Empty,
                    "No vessel declares a station under its own id, so there is no vessel station to recover.");
            });

            await server.WaitPost(() => entMan.EnsureComponent<VesselComponent>(ship).VesselId = vesselId);

            await server.WaitAssertion(() =>
                Assert.That(entMan.HasComponent<StationMemberComponent>(ship), Is.False,
                    "The control: a ship that is already a station member would be answered by the station branch."));

            var stored = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));

            Assert.That(stored, Is.Not.Null);
            Assert.That(stored!.Value.Result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var rows = await DrydockTestHelpers.RunOnServer(pair, () => drydockStore.GetShipsByOwner(session.UserId.UserId));
            var filed = rows.SingleOrDefault(r => r.ShipGuid == stored.Value.ShipId!.Value);

            Assert.That(filed, Is.Not.Null);
            Assert.That(filed!.VesselProto, Is.EqualTo(vesselId),
                "With no station to read, the store has to take the vessel id off the grid or the row is filed blank forever.");

            var retrieved = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, stored.Value.ShipId!.Value, ShipyardConsoleUiKey.Shipyard));

            Assert.That(retrieved, Is.Not.Null);

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var shipStation = stationSys.GetOwningStation(retrieved!.Value);
                Assert.That(shipStation, Is.Not.Null, "A retrieved ship is its own station, whether or not its vessel is known.");

                Assert.That(entMan.TryGetComponent<ExtraShuttleInformationComponent>(shipStation!.Value, out var info), Is.True,
                    "The vessel's own station config carries this; the plain fallback station names no vessel.");
                Assert.That(info!.Vessel?.Id, Is.EqualTo(vesselId),
                    "A hull that came back on its vessel's station has to be labelled as that vessel, which is what the late-join tab reads.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The radar half of an imported hull. The ship-save exporter strips <c>IFF</c>, and the
        /// import path used to put back a bare one: the component's factory gold, no flags. That
        /// rode every store after it, so an imported hull drew amber on the mass scanner for good
        /// while a purchased one of the same class drew the white its vessel grants. The retrieve
        /// now applies the vessel's grant, and has to displace that blank to do it, since the grant
        /// is additive and would otherwise skip a component already present.
        /// </summary>
        [Test]
        public async Task ABlankIffIsReplacedByItsVesselsGrant()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            using var _ = ExpectDockJointLog(pair);
            var server = pair.Server;
            var entMan = server.EntMan;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var shipyard = server.System<ShipyardSystem>();

            var session = playerMan.Sessions.First();
            var (_, _, ship, console, consoleComp, _, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            var vesselId = string.Empty;
            IFFComponent? granted = null;
            await server.WaitAssertion(() => (vesselId, granted) = VesselGrantingAnIff(protoMan));

            await server.WaitPost(() =>
            {
                entMan.EnsureComponent<VesselComponent>(ship).VesselId = vesselId;
                entMan.RemoveComponent<IFFComponent>(ship);
                entMan.AddComponent<IFFComponent>(ship);
            });

            await server.WaitAssertion(() =>
            {
                var blank = entMan.GetComponent<IFFComponent>(ship);
                Assert.That(blank.Flags & IFFFlags.IsPlayerShuttle, Is.EqualTo(IFFFlags.None),
                    "The control: the blank the old import minted carries no flags, which is what marks it as not the shipyard's.");
                Assert.That(blank.Color, Is.EqualTo(IFFComponent.IFFColor), "The control: the blank is the factory gold.");
            });

            var stored = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));

            Assert.That(stored, Is.Not.Null);
            Assert.That(stored!.Value.Result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, stored.Value.ShipId!.Value, ShipyardConsoleUiKey.Shipyard));

            Assert.That(retrieved, Is.Not.Null);

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.TryGetComponent<IFFComponent>(retrieved!.Value, out var iff), Is.True,
                    "The vessel grants an IFF, so the hull has to come back carrying one.");
                Assert.That(iff!.Flags & granted!.Flags, Is.EqualTo(granted.Flags),
                    "The vessel's flags have to land, IsPlayerShuttle above all: without it the radar's shuttle filter does not count the hull as a shuttle.");
                Assert.That(iff.Color, Is.EqualTo(granted.Color),
                    "The mass scanner draws this colour, so a blank left in place is the amber hull a player sees.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The other side of the displacement above: a crew's own IFF is not a blank and must not be
        /// treated as one. A player who recoloured their ship at the IFF console carries the
        /// <c>IsPlayerShuttle</c> flag the purchase granted, and a grant that re-applied the vessel's
        /// white over that on every retrieve would undo their work each time they docked.
        /// </summary>
        [Test]
        public async Task ACrewsOwnIffSurvivesTheVesselsGrant()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            using var _ = ExpectDockJointLog(pair);
            var server = pair.Server;
            var entMan = server.EntMan;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var shipyard = server.System<ShipyardSystem>();
            var shuttles = server.System<ShuttleSystem>();

            var session = playerMan.Sessions.First();
            var (_, _, ship, console, consoleComp, _, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            var crewColor = Color.Firebrick;
            var vesselId = string.Empty;
            IFFComponent? granted = null;
            await server.WaitAssertion(() =>
            {
                (vesselId, granted) = VesselGrantingAnIff(protoMan);
                Assert.That(granted!.Color, Is.Not.EqualTo(crewColor),
                    "The control: a crew colour equal to the vessel's cannot tell a kept IFF from an overwritten one.");
            });

            await server.WaitPost(() =>
            {
                entMan.EnsureComponent<VesselComponent>(ship).VesselId = vesselId;
                shuttles.AddIFFFlag(ship, IFFFlags.IsPlayerShuttle);
                shuttles.SetIFFColor(ship, crewColor);
            });

            var stored = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));

            Assert.That(stored, Is.Not.Null);
            Assert.That(stored!.Value.Result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, stored.Value.ShipId!.Value, ShipyardConsoleUiKey.Shipyard));

            Assert.That(retrieved, Is.Not.Null);

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.TryGetComponent<IFFComponent>(retrieved!.Value, out var iff), Is.True);
                Assert.That(iff!.Color, Is.EqualTo(crewColor),
                    "An IFF carrying IsPlayerShuttle came from the shipyard and was then made the crew's own; the grant is additive and leaves it.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A vessel whose composed grant carries an IFF in a colour other than the factory gold, read
        /// out of live data rather than named. The colour filter is what keeps the assertions honest:
        /// the antagonist base grants an IFF at the default gold, and a test picking that vessel
        /// could not tell a replaced blank from one left in place.
        /// </summary>
        private static (string Id, IFFComponent Granted) VesselGrantingAnIff(IPrototypeManager protoMan)
        {
            foreach (var vessel in protoMan.EnumeratePrototypes<VesselPrototype>().OrderBy(v => v.ID))
            {
                foreach (var entry in vessel.AddComponents.Values)
                {
                    if (entry.Component is not IFFComponent iff)
                        continue;

                    // Copied out first: IFFComponent is access-restricted to the shuttle system, and
                    // calling a method on its field in place is an execute the analyzer refuses.
                    var color = iff.Color;
                    if (!color.Equals(IFFComponent.IFFColor))
                        return (vessel.ID, iff);
                }
            }

            Assert.Fail("No vessel grants an IFF in a colour of its own, so there is no grant for a retrieve to apply.");
            return default;
        }

        /// <summary>
        /// The duplicate gate, which is the one thing here that must never come loose.
        ///
        /// <para>A retrieved ship's row moves to <see cref="DrydockShipState.CheckedOut"/> and
        /// nothing returns it, so the console must neither list it nor retrieve it a second time.
        /// The inherited one-ship-per-card rule looks like it covers this and does not: a card deed
        /// is not cleared when a grid dies, so a player who unloads cargo and lets the hull go
        /// would otherwise retrieve the same revision again and have the cargo twice. The row state
        /// is what actually closes that, and this test is what says so.</para>
        ///
        /// <para>Deliberately asked with a <em>clean</em> card. Refusing because the card is full
        /// would pass this test while proving the wrong gate, so the deed is stripped first and the
        /// refusal that remains can only be the row's.</para>
        /// </summary>
        [Test]
        public async Task ACheckedOutShipCannotBeRetrievedTwice()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            using var _ = ExpectDockJointLog(pair);
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var shipyard = server.System<ShipyardSystem>();

            var session = playerMan.Sessions.First();

            // A store needs a berth on the operator's account; the console is not where berths
            // are bought, so grant one.
            await server.ResolveDependency<DrydockStore>().AddBerth(session.UserId.UserId, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            var stored = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(stored?.Result, Is.EqualTo(DrydockStoreResult.Success));

            // By id, not by position: pooled pairs share a database, so this account may already
            // carry ships filed by other tests in the same run.
            var shipId = stored!.Value.ShipId!.Value;

            await pair.RunTicksSync(5);

            await DrydockTestHelpers.RunOnServer(pair, async () =>
            {
                await shipyard.RefreshDrydockState(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
                return true;
            });

            await server.WaitAssertion(() =>
            {
                Assert.That(consoleComp.CachedDrydock.StoredShips.Any(s => s.ShipId == shipId), Is.True,
                    "The control: a stored ship is listed, so its later absence means something.");
            });

            var first = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));
            Assert.That(first, Is.Not.Null, "The control: the first retrieve has to succeed, or the second proves nothing.");

            await pair.RunTicksSync(5);

            // Clear the card, so what refuses below is the row and not card capacity. And take the
            // ownership off the hull that came back, so it is not the one-ship-out rule either.
            await server.WaitPost(() => entMan.RemoveComponent<ShuttleDeedComponent>(card));
            await server.WaitPost(() => entMan.RemoveComponent<ShipOwnershipComponent>(first!.Value));
            await pair.RunTicksSync(2);

            await DrydockTestHelpers.RunOnServer(pair, async () =>
            {
                await shipyard.RefreshDrydockState(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
                return true;
            });

            await server.WaitAssertion(() =>
            {
                // A ship that is out stays on the list so its owner can see why a berth is empty,
                // but it is listed as out, which is what disables its retrieve button. The row
                // state below is what actually refuses; this is the console telling the truth.
                var listed = consoleComp.CachedDrydock.StoredShips.SingleOrDefault(s => s.ShipId == shipId);
                Assert.That(listed, Is.Not.Null, "A ship that is out is still the player's ship and still on their list.");
                Assert.That(listed!.State, Is.EqualTo(nameof(DrydockShipState.CheckedOut)),
                    "The list must say the ship is out, or the tab would offer a retrieve that the row refuses.");
            });

            var second = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));

            Assert.That(second, Is.Null,
                "Retrieving a checked-out ship a second time would duplicate it and everything aboard. The row state is the only thing standing in the way of that, since a card deed is never cleared when a grid dies.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Garage ownership follows the ship's stamped account, never the card in the slot. Cards
        /// get lent and stolen, and a deed is a claim on flying a ship rather than on filing it
        /// away, so a borrowed one must not let somebody put another person's ship into storage.
        /// </summary>
        [Test]
        public async Task ABorrowedDeedCannotStoreSomeoneElsesShip()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var shipyard = server.System<ShipyardSystem>();
            var store = server.ResolveDependency<DrydockStore>();

            var session = playerMan.Sessions.First();

            // A store needs a berth on the operator's account; the console is not where berths
            // are bought, so grant one.
            await store.AddBerth(session.UserId.UserId, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            // The ship belongs to somebody who is not at the console.
            var absentOwner = new Robust.Shared.Network.NetUserId(Guid.NewGuid());
            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, absentOwner);

            var result = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));

            Assert.That(result, Is.Null,
                "The console must refuse before the pipeline is entered: holding the deed is not the same as owning the ship.");

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.Deleted(ship), Is.False, "A refused store must leave the ship flying.");
                Assert.That(entMan.HasComponent<ShuttleDeedComponent>(card), Is.True,
                    "A refused store must not strip the deed off a card it had no business acting on.");

                // What the client draws the lockout from: the card's ship belongs to someone else.
                var state = shipyard.BuildDrydockState(console);
                Assert.That(state.DeedOwnerUserId, Is.EqualTo(absentOwner.UserId),
                    "The console state must name the deed ship's owner, or the client cannot tell this card is not the operator's.");
            });

            // The refusal is on the timeline, filed against the account that sent it. The console
            // never offers this click, so a row here is the signal an admin reads a stolen card by.
            var refusals = await DrydockTestHelpers.RunOnServer(pair, () => store.GetAuditByActor(session.UserId.UserId, 20));
            var refusal = refusals.FirstOrDefault(a => a.Action == DrydockAuditAction.AccessRefused && a.Reason == "store");
            Assert.That(refusal, Is.Not.Null, "A refused store by a non-owner must be written to the timeline.");
            Assert.That(refusal!.SubjectUserId, Is.EqualTo(absentOwner.UserId), "The row names whose ship was asked for.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A transfer is bound to the account, not the card and not the character. The operator
        /// this harness spawns has a session and no mind, which is the control: a mind is exactly
        /// what a dead player loses when they are reprinted into a fresh body, and a ship must
        /// never become untransferable because of it. A session that does not own the row is
        /// refused and the refusal is written down; so is a session answering an offer that was
        /// not addressed to it.
        ///
        /// <para>The harness has one session, so a console-side offer to another live captain
        /// cannot be exercised here: the recipient gate refuses an offline account, which is
        /// checked, and the escrow itself is opened at the store level and then driven from the
        /// console for everything that follows.</para>
        /// </summary>
        [Test]
        public async Task ATransferOfferIsBoundToTheOfferingAccount()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var shipyard = server.System<ShipyardSystem>();
            var mindSys = server.System<Content.Server.Mind.MindSystem>();

            var session = playerMan.Sessions.First();
            var me = session.UserId.UserId;
            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            var stored = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(stored?.Result, Is.EqualTo(DrydockStoreResult.Success));
            var shipId = stored!.Value.ShipId!.Value;
            await pair.RunTicksSync(5);

            // The control: no mind behind the click. If this ever grows one, the test proves less.
            await server.WaitAssertion(() =>
            {
                Assert.That(mindSys.TryGetMind(operatorEnt, out _, out _), Is.False,
                    "The operator must have no mind, or an offer succeeding says nothing about the account check.");
            });

            // Another account with room for two hulls, and a hull of its own already in one.
            var stranger = Guid.NewGuid();
            var theirs = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(server.ResolveDependency<IServerDbManager>(), stranger);
            await store.AddBerth(stranger, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);
            await store.AddBerth(stranger, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);
            await store.FileRevision(new DrydockRevisionRequest
            {
                ShipGuid = theirs,
                OwnerUserId = stranger,
                ShipName = "NotYours",
                SizeClass = nameof(ShipSizeClass.Cutter),
                MarkStored = true,
                Kind = DrydockRevisionKind.PlayerStore,
                EngineFormatVer = 7,
                ProtoFingerprint = new byte[] { 1 },
                CapturedKeyHash = new byte[] { 1 },
                Checksum = new byte[] { 1 },
                SizeBytes = 1,
                Manifest = "{}",
            }, new byte[] { 1 }, 3);

            // The recipient gate: the stranger is not online, so the console refuses before any
            // row is written. The ownership check passed, so nothing is on the timeline for it.
            var offline = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryOfferTransfer(console, consoleComp, operatorEnt, shipId, stranger, ShipyardConsoleUiKey.Shipyard));
            Assert.That(offline, Is.False, "An offer to a captain who is not online is refused.");
            Assert.That((await store.GetShipHeader(shipId))!.State, Is.EqualTo(DrydockShipState.Stored));

            // A ship this account does not own: refused, and the refusal is on the timeline with
            // both accounts named. This is the only way a console ever writes such a row, since
            // the tab never offers the click.
            var forged = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryOfferTransfer(console, consoleComp, operatorEnt, theirs, stranger, ShipyardConsoleUiKey.Shipyard));
            Assert.That(forged, Is.False, "A session that does not own the row cannot offer it, whatever card is in the slot.");

            var refusals = await DrydockTestHelpers.RunOnServer(pair, () => store.GetAuditByActor(me, 20));
            var refusal = refusals.FirstOrDefault(a => a.Action == DrydockAuditAction.AccessRefused && a.ShipGuid == theirs);
            Assert.That(refusal, Is.Not.Null, "A refused offer must be on the timeline: it is the stolen-card signal.");
            Assert.Multiple(() =>
            {
                Assert.That(refusal!.ActorUserId, Is.EqualTo(me));
                Assert.That(refusal.SubjectUserId, Is.EqualTo(stranger));
                Assert.That(refusal.Reason, Is.EqualTo("transfer"));
            });

            // Escrow, opened at the store: the ship keeps its berth and cannot come out.
            var (offered, transfer) = await store.TryOfferTransfer(shipId, me, stranger, TimeSpan.FromMinutes(30), null);
            Assert.That(offered, Is.EqualTo(DrydockBerthResult.Success));
            var inEscrow = (await store.GetShipHeader(shipId))!;
            Assert.Multiple(() =>
            {
                Assert.That(inEscrow.State, Is.EqualTo(DrydockShipState.InEscrow));
                Assert.That(inEscrow.BerthId, Is.Not.Null, "A ship in escrow keeps its berth.");
            });

            var retrieved = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));
            Assert.That(retrieved, Is.Null, "A ship in escrow does not come out.");
            Assert.That((await store.GetShipHeader(shipId))!.State, Is.EqualTo(DrydockShipState.InEscrow));

            // The wrong party answering: the owner cannot decline their own offer, and the
            // attempt is written down against the ship.
            var selfDecline = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDeclineTransfer(console, consoleComp, operatorEnt, transfer!.Id, ShipyardConsoleUiKey.Shipyard));
            Assert.That(selfDecline, Is.False);
            refusals = await DrydockTestHelpers.RunOnServer(pair, () => store.GetAuditByActor(me, 20));
            Assert.That(refusals.Any(a => a.Action == DrydockAuditAction.AccessRefused && a.ShipGuid == shipId && a.Reason == "decline offer"),
                "Declining an offer you made is a forged message and goes on the timeline.");

            // The owner withdraws it from the console: the ship is stored again.
            var cancelled = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryCancelTransfer(console, consoleComp, operatorEnt, transfer!.Id, ShipyardConsoleUiKey.Shipyard));
            Assert.That(cancelled, Is.True);
            Assert.That((await store.GetShipHeader(shipId))!.State, Is.EqualTo(DrydockShipState.Stored));

            // The other direction: an offer addressed to this session, accepted at the console.
            // The ship changes hands into one of this account's free berths.
            var spare = await store.AddBerth(me, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            var (offeredBack, incoming) = await store.TryOfferTransfer(theirs, stranger, me, TimeSpan.FromMinutes(30), null);
            Assert.That(offeredBack, Is.EqualTo(DrydockBerthResult.Success));

            var accepted = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryAcceptTransfer(console, consoleComp, operatorEnt, incoming!.Id, ShipyardConsoleUiKey.Shipyard));
            Assert.That(accepted, Is.True, "The account the offer names accepts it; the character's mind is not consulted.");
            var mine = (await store.GetShipHeader(theirs))!;
            var myBerths = await store.GetBerths(me);
            Assert.Multiple(() =>
            {
                Assert.That(mine.OwnerUserId, Is.EqualTo(me));
                Assert.That(mine.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(myBerths.Select(b => b.Berth.BerthId), Does.Contain(mine.BerthId!.Value), "The accepted ship lands in one of the recipient's own berths.");
            });

            // Expiry: a deadline already past is swept, the ship returns to Stored, and the
            // owner does not change.
            var (offeredStale, stale) = await store.TryOfferTransfer(theirs, me, stranger, TimeSpan.FromSeconds(-1), null);
            Assert.That(offeredStale, Is.EqualTo(DrydockBerthResult.Success));
            var released = await store.ExpireTransfers(DateTime.UtcNow, null);
            Assert.That(released, Does.Contain(theirs), "The sweep releases every offer past its deadline.");
            var afterSweep = (await store.GetShipHeader(theirs))!;
            Assert.Multiple(() =>
            {
                Assert.That(afterSweep.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(afterSweep.OwnerUserId, Is.EqualTo(me));
            });
            Assert.That(await store.GetPendingTransfer(stale!.Id), Is.Null, "An expired offer is no longer pending.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The card at the top of the tab and the berth it names. The console reads the deed ship
        /// from the live grid and offers the berths it fits, and a store that names one of them
        /// lands there rather than wherever the store would have picked.
        /// </summary>
        [Test]
        public async Task AStoreLandsInTheBerthItNames()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var shipyard = server.System<ShipyardSystem>();

            var session = playerMan.Sessions.First();
            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            // A berth the store would not pick on its own: the harness grants SuperCapital ones,
            // and a Cutter is the smallest, so the picker would prefer this one. Name a larger
            // one instead and check the request wins over the preference.
            var named = await store.AddBerth(session.UserId.UserId, ShipSizeClass.Capital, DrydockBerthKind.Granted, 0, null, null);

            await DrydockTestHelpers.RunOnServer(pair, async () =>
            {
                await shipyard.RefreshDrydockState(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
                return true;
            });

            await server.WaitAssertion(() =>
            {
                var deedShip = consoleComp.CachedDrydock.DeedShip;
                Assert.That(deedShip, Is.Not.Null, "With a deed in the slot the tab must describe the ship on it.");
                Assert.That(deedShip!.Name, Is.EqualTo("Kestrel"));
                Assert.That(deedShip.MinutesOut, Is.Null, "A hull that has never been stored has no time out.");
                Assert.That(deedShip.DefaultBerthId, Is.Not.Null, "A free berth that fits must be offered as the default.");
                Assert.That(deedShip.FittingBerthIds, Does.Contain(named), "Every free berth the hull fits is offered, including the one about to be named.");
            });

            var stored = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard, named));
            Assert.That(stored?.Result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var header = await DrydockTestHelpers.RunOnServer(pair, () => store.GetShipHeader(stored!.Value.ShipId!.Value));
            Assert.That(header!.BerthId, Is.EqualTo(named), "A store that names a berth lands in that berth.");

            await DrydockTestHelpers.RunOnServer(pair, async () =>
            {
                await shipyard.RefreshDrydockState(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
                return true;
            });

            await server.WaitAssertion(() =>
            {
                Assert.That(consoleComp.CachedDrydock.DeedShip, Is.Null, "The deed came off with the store, so the card at the top goes with it.");
                var row = consoleComp.CachedDrydock.Berths.Single(b => b.BerthId == named);
                Assert.That(row.OccupantName, Is.EqualTo("Kestrel"));
                Assert.That(row.OccupantState, Is.EqualTo(nameof(DrydockShipState.Stored)));
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Builds a station, a ship stamped to <paramref name="shipOwner"/>, and a console holding a
        /// deed card for it, with the operator standing clear of the grid.
        /// </summary>
        /// <summary>
        /// The three verbs behind the row menu. Rename is a row update that the hull and its deed
        /// take at the next retrieve, so the retrieved grid is read to prove the stamp; move lands
        /// in the berth it names; a sale needs the ship's exact name typed, frees the berth, marks
        /// the row Sold with the price on the timeline, and pays from the appraisal captured at
        /// store. Every verb is refused for a ship the account does not own and written down.
        /// </summary>
        [Test]
        public async Task AStoredShipCanBeRenamedMovedAndSold()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            using var _ = ExpectDockJointLog(pair);
            var server = pair.Server;
            var entMan = server.EntMan;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var shipyard = server.System<ShipyardSystem>();

            var session = playerMan.Sessions.First();
            var me = session.UserId.UserId;
            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            var stored = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(stored?.Result, Is.EqualTo(DrydockStoreResult.Success));
            var shipId = stored!.Value.ShipId!.Value;
            await pair.RunTicksSync(5);

            // Rename: the shape is enforced, then the row changes and nothing else does yet.
            var badName = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryRenameStoredShip(console, consoleComp, operatorEnt, shipId, "Falcon!!", ShipyardConsoleUiKey.Shipyard));
            Assert.That(badName, Is.False, "Punctuation outside the allowed shape is refused.");

            var renamed = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryRenameStoredShip(console, consoleComp, operatorEnt, shipId, "Falcon", ShipyardConsoleUiKey.Shipyard));
            Assert.That(renamed, Is.True);
            Assert.That((await store.GetShipHeader(shipId))!.ShipName, Is.EqualTo("Falcon"));

            var renames = await DrydockTestHelpers.RunOnServer(pair, () => store.GetAuditByActor(me, 20));
            Assert.That(renames.Any(a => a.Action == DrydockAuditAction.Renamed && a.ShipGuid == shipId && a.ShipName == "Kestrel"),
                "The rename row carries the OLD name, so the old name stays searchable.");

            // Move: into a berth the store would not have picked on its own.
            var named = await store.AddBerth(me, ShipSizeClass.Capital, DrydockBerthKind.Granted, 0, null, null);
            var moved = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryMoveStoredShip(console, consoleComp, operatorEnt, shipId, named, ShipyardConsoleUiKey.Shipyard));
            Assert.That(moved, Is.True);
            Assert.That((await store.GetShipHeader(shipId))!.BerthId, Is.EqualTo(named));

            // Retrieve: the hull comes back wearing the row's name.
            var grid = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));
            Assert.That(grid, Is.Not.Null);
            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.GetComponent<MetaDataComponent>(grid!.Value).EntityName, Is.EqualTo("Falcon"),
                    "A rename made while stored is stamped onto the hull at retrieve.");
            });
            await pair.RunTicksSync(5);

            // Store again, which captures the appraisal the sale quotes from.
            var restored = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(restored?.Result, Is.EqualTo(DrydockStoreResult.Success));
            Assert.That(restored!.Value.ShipId, Is.EqualTo(shipId), "The same hull files a new revision, not a new ship.");
            await pair.RunTicksSync(5);

            var appraisals = await store.GetCurrentAppraisals(me);
            Assert.That(appraisals[shipId], Is.Not.Null, "A store captures the appraisal on its revision.");

            await server.WaitPost(() => entMan.EnsureComponent<Content.Shared._NF.Bank.Components.BankAccountComponent>(operatorEnt));

            // Sell: the typed name is the safety. The old name no longer matches.
            var wrongName = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TrySellStoredShip(console, consoleComp, operatorEnt, shipId, "Kestrel", ShipyardConsoleUiKey.Shipyard));
            Assert.That(wrongName.Sold, Is.False, "A sale needs the ship's exact current name typed.");
            Assert.That((await store.GetShipHeader(shipId))!.State, Is.EqualTo(DrydockShipState.Stored));

            var sale = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TrySellStoredShip(console, consoleComp, operatorEnt, shipId, "Falcon", ShipyardConsoleUiKey.Shipyard));
            Assert.That(sale.Sold, Is.True);
            var soldHeader = (await store.GetShipHeader(shipId))!;
            Assert.Multiple(() =>
            {
                Assert.That(soldHeader.State, Is.EqualTo(DrydockShipState.Sold));
                Assert.That(soldHeader.BerthId, Is.Null, "A sold ship leaves its berth.");
                Assert.That(soldHeader.LastBerthId, Is.Not.Null, "But remembers it, for an admin restore.");
            });

            var audit = await DrydockTestHelpers.RunOnServer(pair, () => store.GetAuditByActor(me, 30));
            var soldRow = audit.FirstOrDefault(a => a.Action == DrydockAuditAction.ShipSold && a.ShipGuid == shipId);
            Assert.That(soldRow, Is.Not.Null);
            Assert.That(soldRow!.Reason, Does.Contain($"sold for {sale.Price}"), "The price paid is on the timeline for the reversal to read.");

            // A ship this account does not own: refused and written down, whatever was typed.
            var stranger = Guid.NewGuid();
            var theirs = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(server.ResolveDependency<IServerDbManager>(), stranger);
            await store.AddBerth(stranger, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);
            await store.FileRevision(new DrydockRevisionRequest
            {
                ShipGuid = theirs,
                OwnerUserId = stranger,
                ShipName = "NotYours",
                SizeClass = nameof(ShipSizeClass.Cutter),
                MarkStored = true,
                Kind = DrydockRevisionKind.PlayerStore,
                EngineFormatVer = 7,
                ProtoFingerprint = new byte[] { 1 },
                CapturedKeyHash = new byte[] { 1 },
                Checksum = new byte[] { 1 },
                SizeBytes = 1,
                Manifest = "{}",
                AppraisedValue = 1000,
            }, new byte[] { 1 }, 3);

            var forged = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TrySellStoredShip(console, consoleComp, operatorEnt, theirs, "NotYours", ShipyardConsoleUiKey.Shipyard));
            Assert.That(forged.Sold, Is.False);
            Assert.That((await store.GetShipHeader(theirs))!.State, Is.EqualTo(DrydockShipState.Stored));

            var refusals = await DrydockTestHelpers.RunOnServer(pair, () => store.GetAuditByActor(me, 30));
            Assert.That(refusals.Any(a => a.Action == DrydockAuditAction.AccessRefused && a.ShipGuid == theirs && a.Reason == "sell"),
                "A forged sale is the stolen-card signal and goes on the timeline.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Ghosts and admin ghosts do not use shipyard consoles (user, 2026-09-13): the console refuses
        /// to open for either, and the server drops any message an admin ghost sends it except closing
        /// the window. A living operator opens it as before.
        /// </summary>
        [Test]
        public async Task GhostsCannotUseTheShipyardConsole()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            using var _ = ExpectDockJointLog(pair);
            var server = pair.Server;
            var entMan = server.EntMan;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var session = playerMan.Sessions.First();
            var (_, _, _, console, _, _, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            await server.WaitAssertion(() =>
            {
                var at = entMan.GetComponent<TransformComponent>(console).Coordinates;
                var aghost = entMan.SpawnEntity("AdminObserver", at);
                var ghost = entMan.SpawnEntity("MobObserver", at);

                bool OpenRefused(EntityUid user)
                {
                    var attempt = new Content.Shared.UserInterface.ActivatableUIOpenAttemptEvent(user);
                    entMan.EventBus.RaiseLocalEvent(console, attempt);
                    return attempt.Cancelled;
                }

                bool MessageRefused(EntityUid actor, BoundUserInterfaceMessage message)
                {
                    var attempt = new BoundUserInterfaceMessageAttempt(actor, console, ShipyardConsoleUiKey.Shipyard, message);
                    entMan.EventBus.RaiseEvent(EventSource.Local, attempt);
                    return attempt.Cancelled;
                }

                Assert.Multiple(() =>
                {
                    Assert.That(OpenRefused(operatorEnt), Is.False, "Control: a living operator opens the console.");
                    Assert.That(OpenRefused(aghost), Is.True, "An admin ghost cannot open it.");
                    Assert.That(OpenRefused(ghost), Is.True, "An ordinary ghost cannot open it.");
                    Assert.That(MessageRefused(aghost, new Content.Shared._NF.Shipyard.Events.ShipyardConsoleStoreMessage(null)), Is.True,
                        "A drydock press from an admin ghost is dropped by the server.");
                    Assert.That(MessageRefused(aghost, new CloseBoundInterfaceMessage()), Is.False,
                        "Control: closing the window is let through.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The footer's sale button follows the deed on the card, and the server refuses the one sale
        /// the button never offers: a hull the drydock can hold. Each mode is set by flipping one
        /// signal on the same fixture and cleared again, so the next one is read against a clean card.
        /// </summary>
        [Test]
        public async Task TheFooterSaleFollowsTheDeedAndRefusesAStorableHull()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            using var _ = ExpectDockJointLog(pair);
            var server = pair.Server;
            var entMan = server.EntMan;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var shipyard = server.System<ShipyardSystem>();

            var session = playerMan.Sessions.First();
            var (_, _, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            await server.WaitAssertion(() =>
            {
                Assert.That(shipyard.DeedSaleFor(card), Is.EqualTo(ShipyardDeedSale.StoreFirst), "A civilian hull with the drydock on is stored, then sold.");
                Assert.That(shipyard.DeedSaleFor(null), Is.EqualTo(ShipyardDeedSale.None), "No card, no sale.");
                Assert.That(shipyard.RefuseFooterSale(console, consoleComp, operatorEnt, card), Is.True, "The footer refuses a hull the drydock holds.");
                Assert.That(entMan.Deleted(ship), Is.False, "The refusal left the ship flying.");
                Assert.That(entMan.HasComponent<ShuttleDeedComponent>(card), Is.True, "And left the deed on the card.");
            });

            // A faction hull the drydock refuses keeps the footer's Sell.
            await server.WaitPost(() => entMan.EnsureComponent<ShipSavingBlacklistComponent>(ship));
            await server.WaitAssertion(() =>
            {
                Assert.That(shipyard.DeedSaleFor(card), Is.EqualTo(ShipyardDeedSale.Sell));
                Assert.That(shipyard.RefuseFooterSale(console, consoleComp, operatorEnt, card), Is.False, "Control: nothing else refuses the sale.");
            });
            await server.WaitPost(() => entMan.RemoveComponent<ShipSavingBlacklistComponent>(ship));

            // A voucher hull is returned, whatever else is true of it.
#pragma warning disable RA0002
            await server.WaitPost(() => entMan.GetComponent<ShuttleDeedComponent>(card).PurchasedWithVoucher = true);
#pragma warning restore RA0002
            await server.WaitAssertion(() => Assert.That(shipyard.DeedSaleFor(card), Is.EqualTo(ShipyardDeedSale.Return)));
#pragma warning disable RA0002
            await server.WaitPost(() => entMan.GetComponent<ShuttleDeedComponent>(card).PurchasedWithVoucher = false);
#pragma warning restore RA0002

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Faction crews are issued their vessels on voucher and are not drydock customers, the
        /// same way they are not ship-save customers. Three signals bar a store, each proven alone
        /// against a control that succeeds once it is cleared: the blacklist the job stamps on the
        /// character, the blacklist a faction vessel carries on its grid, and the voucher flag on a
        /// deed. On the way back, the barred character and a voucher in the slot are both refused.
        /// </summary>
        [Test]
        public async Task FactionCrewsAndVoucherShipsAreRefusedTheDrydock()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            using var _ = ExpectDockJointLog(pair);
            var server = pair.Server;
            var entMan = server.EntMan;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var shipyard = server.System<ShipyardSystem>();

            var session = playerMan.Sessions.First();
            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            // The character: the same component the job stamps on TDF and TFA crews.
            await server.WaitPost(() => entMan.EnsureComponent<ShipSavingBlacklistComponent>(operatorEnt));
            var barredOperator = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(barredOperator, Is.Null, "A character the job has blacklisted from ship saving cannot store either.");
            await server.WaitPost(() => entMan.RemoveComponent<ShipSavingBlacklistComponent>(operatorEnt));

            // The vessel: a faction hull carries the blacklist on its grid.
            await server.WaitPost(() => entMan.EnsureComponent<ShipSavingBlacklistComponent>(ship));
            var barredShip = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(barredShip, Is.Null, "A faction vessel cannot be stored, whoever is at the console.");
            await server.WaitPost(() => entMan.RemoveComponent<ShipSavingBlacklistComponent>(ship));

            // The deed: bought on a voucher. The flag is the shipyard's to write, so the test
            // reaches past the access check the way the buckle tests do.
#pragma warning disable RA0002
            await server.WaitPost(() => entMan.GetComponent<ShuttleDeedComponent>(card).PurchasedWithVoucher = true);
#pragma warning restore RA0002
            var voucherShip = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(voucherShip, Is.Null, "A ship issued on a voucher cannot be stored.");
#pragma warning disable RA0002
            await server.WaitPost(() => entMan.GetComponent<ShuttleDeedComponent>(card).PurchasedWithVoucher = false);
#pragma warning restore RA0002

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.Deleted(ship), Is.False, "Every refusal above left the ship flying.");
                Assert.That(entMan.HasComponent<ShuttleDeedComponent>(card), Is.True, "And left the deed on the card.");
            });

            // Control: with all three cleared the same store goes through.
            var stored = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(stored?.Result, Is.EqualTo(DrydockStoreResult.Success), "Control: nothing else about the fixture was refusing.");
            var shipId = stored!.Value.ShipId!.Value;
            await pair.RunTicksSync(5);

            // The way back: the barred character first, then a voucher where the ID card goes.
            await server.WaitPost(() => entMan.EnsureComponent<ShipSavingBlacklistComponent>(operatorEnt));
            var barredRetrieve = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));
            Assert.That(barredRetrieve, Is.Null, "A blacklisted character cannot call a ship in either.");
            await server.WaitPost(() => entMan.RemoveComponent<ShipSavingBlacklistComponent>(operatorEnt));

            await server.WaitPost(() => entMan.EnsureComponent<ShipyardVoucherComponent>(card));
            var voucherRetrieve = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));
            Assert.That(voucherRetrieve, Is.Null, "A voucher is not a card a stored ship can be called in on.");
            await server.WaitPost(() => entMan.RemoveComponent<ShipyardVoucherComponent>(card));

            Assert.That((await store.GetShipHeader(shipId))!.State, Is.EqualTo(DrydockShipState.Stored),
                "Every refused retrieve left the row stored; none of them reached the claim.");

            var retrieved = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));
            Assert.That(retrieved, Is.Not.Null, "Control: the same retrieve succeeds once the gates are clear.");
            await pair.RunTicksSync(5);

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A store is a hand-over at a berth: the ship has to be docked to the station whose
        /// console is filing it, not parked somewhere in the sector. The tab says so before the
        /// press, the server refuses regardless, and docking the same ship to the same station
        /// turns the same request into a success.
        /// </summary>
        [Test]
        public async Task AStoreNeedsTheShipDockedAtThisStation()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var entMan = server.EntMan;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var shipyard = server.System<ShipyardSystem>();

            var session = playerMan.Sessions.First();
            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId, docked: false);

            await DrydockTestHelpers.RunOnServer(pair, async () =>
            {
                await shipyard.RefreshDrydockState(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
                return true;
            });
            await server.WaitAssertion(() =>
            {
                Assert.That(consoleComp.CachedDrydock.DeedShip, Is.Not.Null);
                Assert.That(consoleComp.CachedDrydock.DeedShip!.Docked, Is.False, "The card at the top of the tab says the ship is not docked here, which is what greys Store.");
            });

            var loose = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(loose, Is.Null, "A ship out in space is refused before the pipeline is entered.");
            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.Deleted(ship), Is.False, "A refused store leaves the ship flying.");
                Assert.That(entMan.HasComponent<ShuttleDeedComponent>(card), Is.True, "And the deed on the card.");
            });

            // Bring it alongside and dock it, then the same request goes through.
            await server.WaitPost(() =>
            {
                server.System<SharedTransformSystem>().SetWorldPosition(ship, new Vector2(1f, -1f));
                DockToStation(entMan, server.System<SharedTransformSystem>(), server.System<DockingSystem>(), stationGrid, ship);
            });
            await pair.RunTicksSync(5);

            await DrydockTestHelpers.RunOnServer(pair, async () =>
            {
                await shipyard.RefreshDrydockState(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
                return true;
            });
            await server.WaitAssertion(() =>
            {
                Assert.That(consoleComp.CachedDrydock.DeedShip!.Docked, Is.True, "Docked now, so the tab offers the store.");
            });

            var stored = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(stored?.Result, Is.EqualTo(DrydockStoreResult.Success), "Control: docked, the same store succeeds.");
            await pair.RunTicksSync(5);

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The impound lot at the console. An impounded ship is listed above the berths with its fee
        /// and the berths it fits; Reclaim charges the fee and seats it, and gives the money back on
        /// a refusal; Abandon needs the ship's exact name and pays nothing; a locked impound refuses
        /// both; and somebody else's impounded hull is refused and written down whatever was typed.
        /// </summary>
        [Test]
        public async Task AnImpoundedShipIsReclaimedOrAbandonedFromTheConsole()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var entMan = server.EntMan;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var shipyard = server.System<ShipyardSystem>();
            var bank = server.System<BankSystem>();

            var session = playerMan.Sessions.First();
            var me = session.UserId.UserId;
            var admin = Guid.NewGuid();
            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            var stored = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(stored?.Result, Is.EqualTo(DrydockStoreResult.Success));
            var shipId = stored!.Value.ShipId!.Value;
            await pair.RunTicksSync(5);

            // A revision with a known appraisal, so the fee is a figure this test can hold against
            // the balance; the harness hull appraises at whatever nine floor tiles are worth.
            await store.FileRevision(Revision(shipId, me, "Kestrel", appraisal: 24000), new byte[] { 1 }, 3);

            var (taken, fee) = await store.TryImpoundStored(shipId, new DrydockImpound(50, "ticket #94", Redeemable: true, ActorUserId: admin), null);
            Assert.Multiple(() =>
            {
                Assert.That(taken, Is.EqualTo(DrydockBerthResult.Success));
                Assert.That(fee, Is.EqualTo(12000), "Control: half the appraisal just filed.");
            });

            await RefreshTab(pair, shipyard, console, consoleComp, operatorEnt);
            var defaultBerth = 0;
            await server.WaitAssertion(() =>
            {
                var impounded = consoleComp.CachedDrydock.ImpoundedShips.SingleOrDefault(i => i.ShipId == shipId);
                Assert.That(impounded, Is.Not.Null, "An impounded ship is listed above the berths.");
                Assert.Multiple(() =>
                {
                    Assert.That(impounded!.Fee, Is.EqualTo(12000));
                    Assert.That(impounded.Appraisal, Is.EqualTo(24000));
                    Assert.That(impounded.Redeemable, Is.True);
                    Assert.That(impounded.Reason, Is.EqualTo("ticket #94"));
                    Assert.That(impounded.DefaultBerthId, Is.Not.Null, "A free berth that fits is offered as the default.");
                    Assert.That(impounded.FittingBerthIds, Does.Contain(impounded.DefaultBerthId!.Value));
                });
                defaultBerth = impounded!.DefaultBerthId!.Value;
            });

            // Money to pay with, and the balance before anything moves.
            var deposited = false;
            await server.WaitPost(() =>
            {
                entMan.EnsureComponent<BankAccountComponent>(operatorEnt);
                deposited = bank.TryBankDeposit(operatorEnt, 20000, null);
            });
            Assert.That(deposited, Is.True, "Control: the account takes a deposit, so it can pay.");
            var before = 0;
            await server.WaitAssertion(() => before = entMan.GetComponent<BankAccountComponent>(operatorEnt).Balance);

            // A refused reclaim gives the money back: the berth named has a ship in it.
            var occupied = await store.AddBerth(me, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);
            var squatter = Guid.NewGuid();
            var squat = await store.FileRevision(Revision(squatter, me, "Pelican", appraisal: 1000, berthId: occupied), new byte[] { 1 }, 3);
            Assert.That(squat.BerthId, Is.EqualTo(occupied), "Control: the berth about to be named is taken.");

            var refused = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryRedeemImpound(console, consoleComp, operatorEnt, shipId, occupied, ShipyardConsoleUiKey.Shipyard));
            Assert.That(refused, Is.False);
            await server.WaitAssertion(() =>
                Assert.That(entMan.GetComponent<BankAccountComponent>(operatorEnt).Balance, Is.EqualTo(before), "A refusal gives the fee back."));
            Assert.That((await store.GetShipHeader(shipId))!.State, Is.EqualTo(DrydockShipState.Impounded));

            // Reclaimed into the default berth: the fee leaves the account once and the row is stored there.
            var reclaimed = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryRedeemImpound(console, consoleComp, operatorEnt, shipId, defaultBerth, ShipyardConsoleUiKey.Shipyard));
            Assert.That(reclaimed, Is.True);
            await server.WaitAssertion(() =>
                Assert.That(entMan.GetComponent<BankAccountComponent>(operatorEnt).Balance, Is.EqualTo(before - 12000), "The fee was charged once."));
            var header = (await store.GetShipHeader(shipId))!;
            Assert.Multiple(() =>
            {
                Assert.That(header.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(header.BerthId, Is.EqualTo(defaultBerth));
            });

            // Locked: neither verb works, and the row does not move.
            Assert.That((await store.TryImpoundStored(shipId, new DrydockImpound(0, "held", Redeemable: false, ActorUserId: admin), null)).Outcome,
                Is.EqualTo(DrydockBerthResult.Success));
            await RefreshTab(pair, shipyard, console, consoleComp, operatorEnt);
            await server.WaitAssertion(() =>
                Assert.That(consoleComp.CachedDrydock.ImpoundedShips.Single(i => i.ShipId == shipId).Redeemable, Is.False, "The card says an admin holds it."));
            Assert.That(await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryRedeemImpound(console, consoleComp, operatorEnt, shipId, defaultBerth, ShipyardConsoleUiKey.Shipyard)), Is.False);
            Assert.That(await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryAbandonShip(console, consoleComp, operatorEnt, shipId, "Kestrel", ShipyardConsoleUiKey.Shipyard)), Is.False);
            Assert.That((await store.GetShipHeader(shipId))!.State, Is.EqualTo(DrydockShipState.Impounded));

            // Released by the admin and taken again on open terms, then given up: the name is the safety.
            Assert.That((await store.TryReleaseImpound(shipId, null, admin, null, "cleared")).Outcome, Is.EqualTo(DrydockBerthResult.Success));
            Assert.That((await store.TryImpoundStored(shipId, new DrydockImpound(50, "left out", Redeemable: true, ActorUserId: admin), null)).Outcome,
                Is.EqualTo(DrydockBerthResult.Success));
            Assert.That(await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryAbandonShip(console, consoleComp, operatorEnt, shipId, "Falcon", ShipyardConsoleUiKey.Shipyard)), Is.False, "The wrong name is refused.");
            Assert.That((await store.GetShipHeader(shipId))!.State, Is.EqualTo(DrydockShipState.Impounded));
            Assert.That(await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryAbandonShip(console, consoleComp, operatorEnt, shipId, "Kestrel", ShipyardConsoleUiKey.Shipyard)), Is.True);
            Assert.That((await store.GetShipHeader(shipId))!.State, Is.EqualTo(DrydockShipState.Abandoned));
            await server.WaitAssertion(() =>
                Assert.That(entMan.GetComponent<BankAccountComponent>(operatorEnt).Balance, Is.EqualTo(before - 12000), "Abandoning pays nothing and costs nothing."));

            // Somebody else's impounded hull: refused and written down, whatever was typed.
            var stranger = Guid.NewGuid();
            var theirs = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(server.ResolveDependency<IServerDbManager>(), stranger);
            await store.AddBerth(stranger, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);
            await store.FileRevision(Revision(theirs, stranger, "NotYours", appraisal: 1000), new byte[] { 1 }, 3);
            Assert.That((await store.TryImpoundStored(theirs, new DrydockImpound(50, "left out", Redeemable: true, ActorUserId: admin), null)).Outcome,
                Is.EqualTo(DrydockBerthResult.Success));

            Assert.That(await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryRedeemImpound(console, consoleComp, operatorEnt, theirs, defaultBerth, ShipyardConsoleUiKey.Shipyard)), Is.False);
            Assert.That(await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryAbandonShip(console, consoleComp, operatorEnt, theirs, "NotYours", ShipyardConsoleUiKey.Shipyard)), Is.False);

            var refusals = await DrydockTestHelpers.RunOnServer(pair, () => store.GetAuditByActor(me, 30));
            Assert.Multiple(() =>
            {
                Assert.That(refusals.Any(a => a.Action == DrydockAuditAction.AccessRefused && a.ShipGuid == theirs && a.Reason == "reclaim"),
                    "A forged reclaim is the stolen-card signal and goes on the timeline.");
                Assert.That(refusals.Any(a => a.Action == DrydockAuditAction.AccessRefused && a.ShipGuid == theirs && a.Reason == "abandon"),
                    "So is a forged abandon.");
            });
            Assert.That((await store.GetShipHeader(theirs))!.State, Is.EqualTo(DrydockShipState.Impounded), "Refused means untouched.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The impound lot is on the tab the FIRST time the console opens, which it was not: the
        /// upstream open handler strips a deed pointing at a dead ship and returns before it
        /// publishes anything, and an impound is exactly that state, because the forced store
        /// despawns the hull and never touches the card. The lot appeared on the second open.
        ///
        /// <para>The open is raised as the event rather than driven through the real interface: the
        /// fixture's console is hand-built and carries no interface data, and the handler is what
        /// the defect lives in. <c>SetUiState</c> no-ops on a console with no interface for the key,
        /// so the cache the tab is built from is what this reads.</para>
        /// </summary>
        [Test]
        public async Task TheImpoundLotIsOnTheTabTheFirstTimeTheConsoleOpens()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            using var clientUiKeyLog = ExpectClientUiKeyLog(pair);
            var server = pair.Server;
            var entMan = server.EntMan;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var drydock = server.System<DrydockSystem>();

            var session = playerMan.Sessions.First();
            var me = session.UserId.UserId;
            var admin = Guid.NewGuid();
            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            // A forced store files a revision whose actor is the impounding admin, and
            // drydock_revision.actor_user_id is a foreign key to the player table. An admin with no
            // player row fails that INSERT rather than any assertion here, which is scaffolding
            // rather than a fork problem: a real admin has connected. The berthed impound at the
            // test above needs none of this, because it writes an audit row and no revision.
            await DrydockTestHelpers.InsertPlayer(server.ResolveDependency<IServerDbManager>(), admin);

            // The open handler needs an account to price the deed against, and a key to publish on.
            // The key goes on before the component does: its ComponentStartup logs an error on a
            // null key, and the pool counts that as a failure.
            await server.WaitPost(() =>
            {
                entMan.EnsureComponent<BankAccountComponent>(operatorEnt);
                entMan.AddComponent(console, new Content.Shared.UserInterface.ActivatableUIComponent
                {
                    Key = ShipyardConsoleUiKey.Shipyard,
                });
            });

            // An admin takes the live hull. No console is involved, so nothing cleans up the card.
            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryImpoundShip(
                ship, me, null, new DrydockImpound(50, "left in a traffic lane", Redeemable: true, ActorUserId: admin), inline: true));
            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
                Assert.That(shipId, Is.Not.Null);
            });
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    // The two controls this test turns on. Without the dangling deed the handler
                    // never reaches its early return, and a cache that is already filled would pass
                    // whatever the handler did.
                    Assert.That(entMan.TryGetComponent<ShuttleDeedComponent>(card, out var dangling), Is.True,
                        "Control: the impound left the deed on the card.");
                    Assert.That(dangling!.ShuttleUid, Is.Not.Null);
                    Assert.That(entMan.EntityExists(dangling.ShuttleUid!.Value), Is.False,
                        "Control: and it points at a ship that no longer exists.");
                    Assert.That(consoleComp.CachedDrydock.ImpoundedShips, Is.Empty,
                        "Control: nothing has read the tab yet, so the lot is not in the cache.");
                });
            });

            // The open itself.
            await server.WaitPost(() => entMan.EventBus.RaiseLocalEvent(
                console, new BoundUIOpenedEvent(ShipyardConsoleUiKey.Shipyard, console, operatorEnt)));

            var listed = false;
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!listed && deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                await pair.RunTicksSync(5);
                await server.WaitPost(() => listed = consoleComp.CachedDrydock.ImpoundedShips.Any(i => i.ShipId == shipId));
            }

            Assert.That(listed, Is.True,
                "The first open published the impound lot, rather than returning once the dangling deed was stripped.");
            await server.WaitAssertion(() =>
                Assert.That(entMan.HasComponent<ShuttleDeedComponent>(card), Is.False,
                    "And upstream's cleanup still took the dead deed off the card."));

            await pair.CleanReturnAsync();
        }

        /// <summary>A revision request with only what the impound tests read back: the name, the class, and an appraisal.</summary>
        private static DrydockRevisionRequest Revision(Guid shipId, Guid owner, string name, int appraisal, int? berthId = null) => new()
        {
            ShipGuid = shipId,
            OwnerUserId = owner,
            ShipName = name,
            SizeClass = nameof(ShipSizeClass.Cutter),
            BerthId = berthId,
            MarkStored = true,
            Kind = DrydockRevisionKind.PlayerStore,
            ActorUserId = owner,
            EngineFormatVer = 7,
            ProtoFingerprint = new byte[] { 1 },
            CapturedKeyHash = new byte[] { 1 },
            Checksum = new byte[] { 1 },
            SizeBytes = 1,
            Manifest = "{}",
            AppraisedValue = appraisal,
        };

        /// <summary>Re-reads the drydock tab the way a console handler does after acting.</summary>
        private static Task RefreshTab(TestPair pair, ShipyardSystem shipyard, EntityUid console, ShipyardConsoleComponent consoleComp, EntityUid operatorEnt)
        {
            return DrydockTestHelpers.RunOnServer(pair, async () =>
            {
                await shipyard.RefreshDrydockState(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
                return true;
            });
        }

        private const string ShuttleAirlockProtoId = "AirlockShuttle";

        /// <summary>
        /// The engine logs a client-side error ("the joint already existed for the connected
        /// entity") when a grid arrives already welded to a grid whose joint component is new in
        /// the same state, which is exactly what the instant dock at retrieve produces, as a ship
        /// purchase does. It is benign in play and recorded as such; here it would fail the pool.
        /// The pool's per-message judge needs a type this project does not reference, so a test
        /// that retrieves raises the client's failure level for its duration instead, the way the
        /// abort test does for the server, and puts it back when it is done. The server side keeps
        /// its full sensitivity throughout.
        /// </summary>
        private static IDisposable ExpectDockJointLog(TestPair pair)
        {
            var level = pair.ClientLogHandler.FailureLevel;
            pair.ClientLogHandler.FailureLevel = LogLevel.Fatal;
            return new RestoreScope(() => pair.ClientLogHandler.FailureLevel = level);
        }

        /// <summary>
        /// This fixture's console is hand-built, so an <c>ActivatableUI</c> added to it at runtime
        /// replicates to the client without the key it was given here: the client's copy starts on a
        /// null key and its startup logs an error. A console spawned from a prototype reads the key
        /// from the prototype on both sides and never does this, so it is harness noise rather than a
        /// fork defect, and the client's failure level is raised for the test the way the dock-joint
        /// log does it. The server keeps its full sensitivity, which is where the drydock lives.
        /// </summary>
        private static IDisposable ExpectClientUiKeyLog(TestPair pair)
        {
            var level = pair.ClientLogHandler.FailureLevel;
            pair.ClientLogHandler.FailureLevel = LogLevel.Fatal;
            return new RestoreScope(() => pair.ClientLogHandler.FailureLevel = level);
        }

        private sealed class RestoreScope(Action restore) : IDisposable
        {
            public void Dispose() => restore();
        }

        /// <summary>
        /// Docks the ship to the station grid the way two airlocks meeting does: a shuttle airlock
        /// on the station's tile facing east, one on the ship's west edge facing back, and the
        /// docking system's own weld between them. The ship has to already be alongside, its west
        /// edge against the station tile, or the weld has nothing consistent to hold.
        /// </summary>
        private static void DockToStation(IEntityManager entMan, SharedTransformSystem transform, DockingSystem docking, EntityUid stationGrid, EntityUid ship)
        {
            var stationDock = entMan.SpawnEntity(ShuttleAirlockProtoId, new EntityCoordinates(stationGrid, new Vector2(0.5f, 0.5f)));
            transform.SetLocalRotation(stationDock, Direction.East.ToAngle());

            var shipDock = entMan.SpawnEntity(ShuttleAirlockProtoId, new EntityCoordinates(ship, new Vector2(0.5f, 1.5f)));
            transform.SetLocalRotation(shipDock, Direction.West.ToAngle());

            docking.Dock(
                (stationDock, entMan.GetComponent<DockingComponent>(stationDock)),
                (shipDock, entMan.GetComponent<DockingComponent>(shipDock)));
        }

        /// <summary>
        /// A store used to be one blocked tick, so a second press landing inside one was a race
        /// nobody could hit on purpose. Sliced, a store lives for as long as the hull needs, and
        /// pressing Store again while the bar is still moving is the ordinary thing a player does.
        /// The refusal and the progress indicator have to read as one story, so this pins the answer
        /// the console actually gives.
        ///
        /// <para>Both presses go out inside a single game tick, which makes the overlap a fact
        /// rather than a hope. Every console gate ahead of the pipeline is synchronous, and the
        /// re-entrancy marker is stamped before the store's first await, so by the time the first
        /// call returns its task the marker is already on the grid and the second call walks
        /// straight into it.</para>
        ///
        /// <para>What is deliberately not asserted: pressing again in the same tick as a
        /// <em>refusal</em>. The marker comes off with <c>RemCompDeferred</c>, so it stays visible to
        /// <c>HasComp</c> until the component cull at the end of the tick, and a press inside that
        /// window answers InProgress for a store that has already given up. That is a harness-level
        /// artefact of the deferred removal rather than a rule about the console, and asserting it
        /// would pin an implementation detail. What matters, and what the tail of this test proves,
        /// is that the sentinel does not leak: once the store is over the same hull stores again.</para>
        /// </summary>
        [Test]
        public async Task ASecondStorePressDuringAStoreIsRefusedAsInProgress()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            using var _ = ExpectDockJointLog(pair);
            var server = pair.Server;
            var entMan = server.EntMan;

            var playerMan = server.ResolveDependency<IPlayerManager>();
            var shipyard = server.System<ShipyardSystem>();
            var store = server.ResolveDependency<DrydockStore>();

            var session = playerMan.Sessions.First();
            var (station, stationGrid, ship, console, consoleComp, card, operatorEnt) = await BuildConsoleAndShip(pair, session.UserId);

            Task<(DrydockStoreResult Result, Guid? ShipId)?>? firstPress = null;
            Task<(DrydockStoreResult Result, Guid? ShipId)?>? secondPress = null;

            await server.WaitPost(() =>
            {
                firstPress = shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
                secondPress = shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard);
            });

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.HasComponent<DrydockInProgressComponent>(ship), Is.True,
                    "The control on the overlap: the sentinel is stamped before the store's first await, so both presses really were in flight together.");
            });

            // Wall clock, not ticks: with the budget off the store's remaining suspensions are
            // thread-pool hops, and a tick ceiling drains long before they land.
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!(firstPress!.IsCompleted && secondPress!.IsCompleted) && deadline.Elapsed < TimeSpan.FromSeconds(60))
            {
                await pair.RunTicksSync(1);
            }

            Assert.Multiple(() =>
            {
                Assert.That(firstPress!.IsCompleted, Is.True, "The first store never completed.");
                Assert.That(secondPress!.IsCompleted, Is.True, "The second press never completed.");
            });

            var first = await firstPress!;
            var second = await secondPress!;

            Assert.Multiple(() =>
            {
                Assert.That(first?.Result, Is.EqualTo(DrydockStoreResult.Success),
                    "Control: the press that got there first is the one that stores the ship.");
                Assert.That(second?.Result, Is.EqualTo(DrydockStoreResult.InProgress),
                    "A press landing on a store already in flight is refused by the pipeline, not swallowed by the console: the player gets told, and nothing is filed twice.");
                Assert.That(second?.ShipId, Is.Null, "A refusal names no hull.");
            });

            var shipId = first!.Value.ShipId!.Value;
            await pair.RunTicksSync(5);

            Assert.That((await store.GetShipHeader(shipId))!.State, Is.EqualTo(DrydockShipState.Stored),
                "One press, one filing. The second must not have written a revision of its own.");

            // The sentinel did not leak. This is what the marker exists for now that it no longer
            // blocks container insertion, and it is the assertion that fails if a store forgets to
            // take it back off: the ship would be storable exactly once, ever.
            var retrieved = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockRetrieve(console, consoleComp, operatorEnt, shipId, ShipyardConsoleUiKey.Shipyard));
            Assert.That(retrieved, Is.Not.Null, "Control: the ship comes back, or there is nothing to store again.");
            await pair.RunTicksSync(5);

            var again = await DrydockTestHelpers.RunOnServer(pair,
                () => shipyard.TryDrydockStore(console, consoleComp, operatorEnt, ShipyardConsoleUiKey.Shipyard));
            Assert.That(again?.Result, Is.EqualTo(DrydockStoreResult.Success),
                "The same hull stores again once the first store is over; an InProgress here would mean the sentinel was never removed.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Builds a station, a ship stamped to <paramref name="shipOwner"/>, and a console holding a
        /// deed card for it, with the operator standing clear of the grid. The ship is docked to the
        /// station unless <paramref name="docked"/> says otherwise, since a store needs it to be.
        /// </summary>
        private static async Task<(EntityUid Station, EntityUid StationGrid, EntityUid Ship, EntityUid Console, ShipyardConsoleComponent Comp, EntityUid Card, EntityUid Operator)>
            BuildConsoleAndShip(TestPair pair, Robust.Shared.Network.NetUserId shipOwner, bool docked = true)
        {
            var server = pair.Server;
            var entMan = server.EntMan;

            var cfg = server.ResolveDependency<IConfigurationManager>();
            var playerMan = server.ResolveDependency<IPlayerManager>();
            var shipyard = server.System<ShipyardSystem>();
            var stationSys = server.System<StationSystem>();
            var mapSys = server.System<SharedMapSystem>();
            var itemSlots = server.System<ItemSlotsSystem>();
            var metaData = server.System<MetaDataSystem>();
            var transform = server.System<SharedTransformSystem>();
            var docking = server.System<DockingSystem>();

            var map = await pair.CreateTestMap();
            var session = playerMan.Sessions.First();

            // A store needs a berth on the operator's account; the console is not where berths
            // are bought, so grant one.
            await server.ResolveDependency<DrydockStore>().AddBerth(session.UserId.UserId, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            EntityUid station = default;
            EntityUid ship = default;
            EntityUid console = default;
            EntityUid card = default;
            EntityUid operatorEnt = default;
            ShipyardConsoleComponent comp = default!;

            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);

                // Slicing off, which for this cvar means no job and no queue at all rather than a
                // job with a zero budget. These tests are about what a person at a console can and
                // cannot reach, and the wall-clock pump in DrydockTestHelpers.RunOnServer is sized for
                // a pipeline that only ever waits on the database. What slicing costs is a different fixture's
                // question; the one thing the console cares about, that a second press during a
                // store is refused, holds at any budget and is asserted below.
                cfg.SetCVar(TriadCCVars.DrydockTickBudgetMs, 0);

                shipyard.SetupShipyardIfNeeded();

                station = entMan.Spawn();
                entMan.AddComponent<StationDataComponent>(station);

                // What BaseStation carries in the prototypes, and the console epilogue needs it: the
                // sector service entity is spawned by this component's init, and the shuttle record
                // filed on every retrieve is EnsureComp'd onto that entity. Without it the filing
                // throws "Entity 0 is not valid" partway through the epilogue, taking the steps after
                // it down with it. A round would have built this; a pooled pair never starts one.
                entMan.AddComponent<StationSectorServiceHostComponent>(station);

                stationSys.AddGridToStation(station, map.Grid.Owner);

                var shipGrid = mapSys.CreateGridEntity(map.MapId);
                ship = shipGrid.Owner;
                var tile = new Tile(1);
                for (var x = 0; x < 3; x++)
                {
                    for (var y = 0; y < 3; y++)
                    {
                        mapSys.SetTile(ship, shipGrid.Comp, new Vector2i(x, y), tile);
                    }
                }

                // Clear of the station's tile either way, and it has to be done by moving the ship
                // rather than by placing the console carefully. Both grids are created at the
                // origin, and grid traversal reparents an entity to whichever grid it is physically
                // over, so a console placed on the station grid inside the ship's footprint silently
                // becomes part of the ship and is despawned along with it by the very store being
                // tested. Docked, the ship sits with its west edge against the station tile; loose,
                // it is far out in the map.
                transform.SetWorldPosition(ship, docked ? new Vector2(1f, -1f) : new Vector2(100f, 100f));

                entMan.EnsureComponent<ShuttleComponent>(ship);
                metaData.SetEntityName(ship, "Kestrel");
                entMan.EnsureComponent<ShipOwnershipComponent>(ship).OwnerUserId = shipOwner;

                // Well clear of both grids. An operator standing aboard the ship would trip the
                // organics gate, and the card would go into storage with it.
                operatorEnt = entMan.SpawnEntity(null, new MapCoordinates(new Vector2(64f, 64f), map.MapId));
                playerMan.SetAttachedEntity(session, operatorEnt);

                // On the station's own grid, not loose on the map. Retrieve resolves where to put
                // the ship from the console's owning station, and a console parented to nothing
                // belongs to no station, so an off-grid console refuses every retrieve.
                console = entMan.SpawnEntity(null, new EntityCoordinates(map.Grid.Owner, new Vector2(0.5f, 0.5f)));
                comp = entMan.EnsureComponent<ShipyardConsoleComponent>(console);

                card = entMan.SpawnEntity(null, new MapCoordinates(new Vector2(64f, 64f), map.MapId));
                shipyard.MintDeeds(card, ship, operatorEnt);
                itemSlots.TryInsert(console, comp.TargetIdSlot, card, user: null);
            });

            // The station and the console both stand on the test grid, which the fork's janitors are
            // built to delete.
            await pair.MakeCleanupImmune(map.Grid.Owner);

            await pair.RunTicksSync(5);

            // Docked only after the client has seen both grids. A grid that arrives at the client
            // already welded to a grid whose joint component is new in the same state logs an
            // engine-side error the pool counts as a failure; it is benign in play and avoided here
            // by giving the weld its own tick.
            if (docked)
            {
                await server.WaitPost(() => DockToStation(entMan, transform, docking, map.Grid.Owner, ship));
                await pair.RunTicksSync(5);
            }

            return (station, map.Grid.Owner, ship, console, comp, card, operatorEnt);
        }
    }
}
