#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Funkystation.Atmos.Components;
using Content.Server._Mono.FireControl;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._Triad.ContrabandPermit;
using Content.Server._Triad.Drydock;
using Content.Server._NF.Market.Components;
using Content.Server.Atmos.Piping.Binary.Components;
using Content.Server.Atmos.Piping.Trinary.Components;
using Content.Server.Database;
using Content.Server.DeviceLinking.Systems;
using Content.Server.DeviceNetwork.Systems;
using Content.Server.Lathe.Components;
using Content.Server.Mind;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Power.Components;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Server.Shuttles.Components;
using Content.Server.Wires;
using Content.Shared._Crescent.ShipShields;
using Content.Shared._FarHorizons.Power.Generation.FissionGenerator;
using Content.Shared._Goobstation.Factory;
using Content.Shared._Mono.FireControl;
using Content.Shared._Mono.SpaceArtillery;
using Content.Shared._NF.Market;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.ContrabandPermit;
using Content.Shared._Triad.Shipyard.Save.Contraband;
using Content.Shared._Triad.ShipSize;
using Content.Shared.ActionBlocker;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.Components;
using Content.Shared.Lathe;
using Content.Shared.NodeContainer;
using Content.Shared.Pinpointer;
using Content.Shared.Power.Generator;
using Content.Shared.Research.Components;
using Content.Shared.Research.Prototypes;
using Content.Shared.SmartFridge;
using Content.Shared.Storage;
using Content.Shared.Timing;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Xenoarchaeology.Artifact.Components;
using Content.Shared.Xenoarchaeology.Equipment.Components;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The first thing that ever moves a ship through the drydock. Everything either pipeline half
    /// does is unproven until this runs: the six Revive steps, both sidecars, the validation
    /// backstop, the manifest, and the claim.
    ///
    /// <para>It builds a ship rather than loading a roster vessel on purpose. A hand-built grid
    /// fails for one reason at a time, which is what you want from the test that establishes the
    /// round trip at all. The roster sweep is the separate test that answers whether real content
    /// survives, and breadth is its job rather than this one's.</para>
    ///
    /// <para>The airlock is not decoration. Its wire layout is the sharpest available probe of the
    /// map-init boundary: <c>WiresComponent.WiresList</c> is not a data field and the only thing
    /// that ever builds it is the map-init handler, which never fires again for a restored entity.
    /// Without the Revive step every panel on a retrieved ship opens empty, and nothing else about
    /// the ship looks wrong.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockRoundTripTest
    {
        private const string AirlockProtoId = "Airlock";
        private const string ResearchServerProtoId = "ResearchAndDevelopmentServer";
        private const string LatheProtoId = "Protolathe";
        private const string LatheRecipeId = "SheetSteel";
        private const string PipeProtoId = "GasPipeStraight";
        private const string MarketItemProtoId = "SheetSteel1";
        private const string SmartFridgeProtoId = "SmartFridge";
        private const string InteractorProtoId = "Interactor";
        private const string ArtifactProtoId = "ComplexXenoArtifactItem";
        private const string AudioProtoId = "Audio";
        private static readonly ProtoId<DamageTypePrototype> BluntDamage = "Blunt";
        private const string ShieldGeneratorProtoId = "ShieldGenerator";
        private const string GunneryServerProtoId = "GunneryServerUltra";
        private const string GunneryConsoleProtoId = "ComputerGunneryConsole";
        private const string TurretProtoId = "WeaponTurretFang";
        private const string ApcProtoId = "APCBasic";
        private const string PressurePumpProtoId = "GasPressurePump";
        private const string VolumePumpProtoId = "GasVolumePump";
        private const string ShuttleGeneratorProtoId = "PortableGeneratorSuperPacmanShuttle";
        private const string FilterProtoId = "GasFilter";
        private const string MixerProtoId = "GasMixer";
        private const string AnalysisConsoleProtoId = "ComputerAnalysisConsole";
        private const string ArtifactAnalyzerProtoId = "MachineArtifactAnalyzer";
        private const string CrystallizerProtoId = "Crystallizer";
        private const string ReactorProtoId = "NuclearReactorNormal";
        private const string TurbineProtoId = "GasTurbineNormal";
        private const float MarkedRpm = 450f;
        private const int MarkedBladeHealth = 3;

        // Values no prefab produces, so a reactor that re-laid its prefab is distinguishable from
        // one that restored its parts rather than merely being miscounted.
        private const float MarkedPartTemperature = 777f;
        private const float MarkedReactorTemperature = 911f;
        private const float MarkedControlRodInsertion = 1.25f;

        [Test]
        public async Task AShipStoredComesBackWithItsContentsAndItsWires()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var cfg = server.ResolveDependency<IConfigurationManager>();
            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var shipyard = server.System<ShipyardSystem>();
            var stationSys = server.System<StationSystem>();
            var mapSys = server.System<SharedMapSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, airlock) = await BuildShipAndStation(pair);

            // What the ship is made of, before it goes anywhere. Compared against the same census
            // afterwards this catches a drop, a duplicate, and a substitution that keeps the count
            // the same.
            var before = await CensusGrid(pair, shipGrid);
            Assert.That(before.Values.Sum(), Is.GreaterThan(0), "The test ship has to actually carry something.");

            var wiresBefore = await ReadWireCount(pair, airlock);
            Assert.That(wiresBefore, Is.GreaterThan(0),
                "A live airlock must have a populated wire layout, or this test cannot prove Revive rebuilt one.");

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
                Assert.That(shipId, Is.Not.Null, "A successful store names the hull it filed.");
            });

            await pair.RunTicksSync(5);
            Assert.That(entMan.Deleted(shipGrid), Is.True,
                "The grid is despawned only after the document is filed, so a live grid here means a half-committed store.");

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success), "The ship went in, so it has to come out.");

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.TryGetComponent<DrydockIdentityComponent>(retrieved.Grid!.Value, out var identity), Is.True,
                    "Identity is the one piece of state nothing else on the grid can reconstruct.");
                Assert.That(identity!.ShipId, Is.EqualTo(shipId!.Value),
                    "A retrieve must return the same hull, not a new one that looks similar.");
            });

            var after = await CensusGrid(pair, retrieved.Grid!.Value);
            Assert.That(after, Is.EqualTo(before),
                "Every prototype aboard comes back, exactly once each. A difference is a drop, a duplicate or a substitution.");

            // The map-init boundary, made concrete. This is the assertion the whole census on the
            // wiki exists to justify.
            var retrievedAirlock = await FindChildWithComponent<WiresComponent>(pair, retrieved.Grid!.Value);
            Assert.That(retrievedAirlock, Is.Not.Null, "The airlock came back, or the census above would have failed.");

            var wiresAfter = await ReadWireCount(pair, retrievedAirlock!.Value);
            Assert.That(wiresAfter, Is.EqualTo(wiresBefore),
                "WiresList is not a data field, so this passes only because Revive rebuilt the layout by hand.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A retrieve loads onto a private paused map of its own, so the shipyard's shared map being
        /// gone must not refuse it. Deleting that map first is the shape round-end cleanup leaves.
        ///
        /// <para>What this locks in is the decoupling: the ship is presented anyway, the drydock does
        /// not quietly go back to the shared map, and the private map it made does not outlive the
        /// pipeline. Asking whether <c>ShipyardMap</c> is null would answer nothing, because it is
        /// written only by <c>SetupShipyardIfNeeded</c> and <c>CleanupShipyard</c> and a raw
        /// <c>DeleteMap</c> leaves a stale id in it. Map ids are never recycled, so the question that
        /// does mean something is whether a live map is behind that id.</para>
        /// </summary>
        [Test]
        public async Task ARetrieveNeedsNoShipyardMap()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var shipyard = server.System<ShipyardSystem>();
            var mapSys = server.System<SharedMapSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            // A delta rather than an absolute count: SweepOrphanStagingMaps deliberately leaves a
            // Stranded map alone, so a pooled pair that inherited one from an earlier fixture would
            // fail a zero for reasons that have nothing to do with this retrieve.
            var stagingBefore = 0;
            await server.WaitPost(() => stagingBefore = CountStagingMaps(entMan));

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var staged = shipyard.ShipyardMap;
            Assert.That(staged, Is.Not.Null, "The fixture staged a shipyard map, or the control below proves nothing.");
            await server.WaitPost(() => mapSys.DeleteMap(staged!.Value));
            await pair.RunTicksSync(1);
            Assert.That(mapSys.MapExists(staged!.Value), Is.False, "Control: the shipyard map is gone before the retrieve.");

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                // Resolved before the multiple, because a failed TryGetComponent inside one does not
                // stop the block and the next line would then throw over the real report.
                EntityUid? shipMap = null;
                var onStagingMap = false;
                if (retrieved.Grid is { } grid && entMan.TryGetComponent<TransformComponent>(grid, out var xform))
                {
                    shipMap = xform.MapUid;
                    onStagingMap = xform.MapUid is { } map && entMan.HasComponent<DrydockStagingMapComponent>(map);
                }

                Assert.Multiple(() =>
                {
                    Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success),
                        "Retrieve stages onto a private map, so a missing shipyard map cannot refuse it.");

                    Assert.That(shipyard.ShipyardMap is not { } live || !mapSys.MapExists(live), Is.True,
                        "A retrieve must not re-stage the shipyard; it owns a private map instead.");

                    Assert.That(CountStagingMaps(entMan), Is.EqualTo(stagingBefore),
                        "Store and retrieve each scrap their own staging map, so neither may outlive the pipeline.");

                    Assert.That(shipMap, Is.Not.Null, "A presented ship is on a live map.");
                    Assert.That(onStagingMap, Is.False, "The ship left the staging map for the station's.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>How many drydock staging maps exist right now, of every kind.</summary>
        internal static int CountStagingMaps(IEntityManager entMan)
        {
            var count = 0;
            var query = entMan.AllEntityQueryEnumerator<DrydockStagingMapComponent>();
            while (query.MoveNext(out _, out _))
                count++;

            return count;
        }

        /// <summary>
        /// A refused retrieve names its reason, so each state the row can be in gets its own answer
        /// here. The success at the end is the control that the fixture was never the reason.
        /// </summary>
        [Test]
        public async Task ARefusedRetrieveNamesItsReason()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            var unknown = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(Guid.NewGuid(), owner, station, null));
            Assert.That(unknown.Result, Is.EqualTo(DrydockRetrieveResult.NotFound));

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            var again = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(again.Result, Is.EqualTo(DrydockRetrieveResult.AlreadyOut), "A ship that is out says so.");

            var (back, _) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(retrieved.Grid!.Value, owner, null));
            Assert.That(back, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            Assert.That(await store.TrySetState(shipId!.Value, DrydockShipState.Stored, DrydockShipState.Impounded, DrydockAuditAction.Impound, null, null, "test"), Is.True);
            var impounded = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(impounded.Result, Is.EqualTo(DrydockRetrieveResult.Impounded));

            Assert.That(await store.TrySetState(shipId!.Value, DrydockShipState.Impounded, DrydockShipState.Stored, DrydockAuditAction.ImpoundReleased, null, null, "test"), Is.True);
            var cleared = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(cleared.Result, Is.EqualTo(DrydockRetrieveResult.Success), "Control: with every reason cleared the same call succeeds.");

            await pair.RunTicksSync(5);
            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A ship shield is derived state: the emitter raises it whenever it has power and the grid
        /// carries a marker pointing at it, so neither may ride the document. A marker written
        /// without its fields reloads pointing at nothing, the emitter's "already shielded" check
        /// then never raises another, and the old shield reloads as a ghost with a hard bullet
        /// fixture and no emitter. Proven on the document and on what comes back.
        /// </summary>
        [Test]
        public async Task AShieldedShipComesBackWithAFreshShield()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            await server.WaitPost(() =>
            {
                var emitter = entMan.SpawnEntity(ShieldGeneratorProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 2.5f)));
                // A test grid has no power net; a receiver that needs no power reads as powered.
                entMan.GetComponent<ApcPowerReceiverComponent>(emitter).NeedsPower = false;
            });

            // The emitter evaluates every 1.5 s; this covers two evaluations at 30 ticks a second.
            await pair.RunTicksSync(100);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.TryGetComponent<ShipShieldedComponent>(shipGrid, out var shielded), Is.True,
                    "Control: the emitter raised a shield on the live ship.");
                Assert.That(entMan.EntityExists(shielded!.Shield), Is.True);
                Assert.That(entMan.GetComponent<TransformComponent>(shielded.Shield).ParentUid, Is.EqualTo(shipGrid),
                    "The shield is a child of the grid, which is what put it into the document before.");
            });

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var document = Encoding.UTF8.GetString(DrydockSystem.DecompressZstd(await ReadBlobs(db, shipId!.Value)));
            Assert.Multiple(() =>
            {
                Assert.That(document, Does.Contain("type: ShipShieldEmitter"), "Control: the generator itself is in the document.");
                Assert.That(document, Does.Not.Contain("proto: ShipShield\n").And.Not.Contain("proto: ShipShield\r"), "The shield entity opts out of saving.");
                Assert.That(document, Does.Not.Contain("ShipShielded"), "The grid's marker is an unsaved component.");
            });

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            var grid = retrieved.Grid!.Value;

            await server.WaitPost(() =>
            {
                foreach (var receiver in ChildrenWith<ApcPowerReceiverComponent>(entMan, grid))
                    entMan.GetComponent<ApcPowerReceiverComponent>(receiver).NeedsPower = false;
            });
            await pair.RunTicksSync(100);

            await server.WaitAssertion(() =>
            {
                var shields = ChildrenWith<ShipShieldComponent>(entMan, grid).ToList();
                Assert.That(shields, Has.Count.EqualTo(1), "Exactly one shield came up: a fresh one, no ghost.");
                var shield = shields[0];
                var newEmitter = ChildrenWith<ShipShieldEmitterComponent>(entMan, grid).Single();

                Assert.That(entMan.TryGetComponent<ShipShieldedComponent>(grid, out var marker), Is.True);
                Assert.That(marker!.Shield, Is.EqualTo(shield), "The marker points at the live shield.");
                Assert.That(entMan.GetComponent<ShipShieldComponent>(shield).Source, Is.EqualTo(newEmitter), "The shield knows its emitter.");
                Assert.That(entMan.GetComponent<ShipShieldEmitterComponent>(newEmitter).Shield, Is.EqualTo(shield), "The emitter knows its shield.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Ship guns fire only while registered with a gunnery server, and the server only links
        /// its grid, its guns and its console on a power edge. A retrieved ship arrives with the
        /// receivers unpowered and the net comes up a tick later, so the edge should fire; players
        /// reported the guns dead after a retrieve all the same. This spells out every
        /// link before the store as the control and demands the same links on what comes back.
        /// </summary>
        [Test]
        public async Task AGunneryServerComesBackWithItsGunsRegistered()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            // Real power, not a forced flag: a basic APC ships a full battery and feeds every
            // receiver within cable range on its own, so the edge the fire-control system needs
            // comes from the power net exactly as it does aboard a ship.
            //
            // The console and the turret come up BEFORE the server, on purpose. A console registers
            // only on its own power edge, so with no server yet that edge lands on nothing; the
            // server connecting afterwards has to pick it up, or a ship whose net comes alive in one
            // tick (a purchase, a retrieve) is left with a console that says it has no server
            // whenever the order falls that way. The harness makes the losing order certain.
            await server.WaitPost(() =>
            {
                entMan.SpawnEntity(ApcProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 2.5f)));
                entMan.SpawnEntity(GunneryConsoleProtoId, new EntityCoordinates(shipGrid, new Vector2(2.5f, 0.5f)));
                entMan.SpawnEntity(TurretProtoId, new EntityCoordinates(shipGrid, new Vector2(2.5f, 2.5f)));
            });
            await pair.RunTicksSync(30);

            await server.WaitAssertion(() =>
            {
                var consoleUid = ChildrenWith<FireControlConsoleComponent>(entMan, shipGrid).Single();
                Assert.That(entMan.GetComponent<ApcPowerReceiverComponent>(consoleUid).Powered, Is.True, "Control: the console is powered before any server exists.");
                Assert.That(entMan.GetComponent<FireControlConsoleComponent>(consoleUid).ConnectedServer, Is.Null, "Control: with no server yet, the console's power edge registered it against nothing.");
            });

            await server.WaitPost(() => entMan.SpawnEntity(GunneryServerProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f))));
            await pair.RunTicksSync(30);

            await server.WaitAssertion(() => AssertGunneryLinked(entMan, shipGrid, "before the store"));
            Assert.That(await FireOnceAndCountProjectiles(pair, shipGrid, "Control, before the store"), Is.GreaterThan(0), "Control: a shot through the server spawns a projectile before the store.");

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            var grid = retrieved.Grid!.Value;

            // Nothing is touched after the retrieve: the ship has to come back armed by itself.
            await pair.RunTicksSync(60);

            await server.WaitAssertion(() => AssertGunneryLinked(entMan, grid, "after the retrieve"));

            // Each gate on the per-shot path, named, so a refusal says which one.
            await server.WaitAssertion(() =>
            {
                var fireControl = server.System<FireControlSystem>();
                var timing = server.ResolveDependency<IGameTiming>();
                var turret = ChildrenWith<FireControllableComponent>(entMan, grid).Single();
                var controllable = entMan.GetComponent<FireControllableComponent>(turret);
                var gun = entMan.GetComponent<GunComponent>(turret);
                var ammo = entMan.GetComponent<ProjectileBatteryAmmoProviderComponent>(turret);
                var battery = entMan.GetComponent<BatteryComponent>(turret);
                var xform = entMan.GetComponent<TransformComponent>(turret);
                Assert.Multiple(() =>
                {
                    Assert.That(fireControl.CanFireWeapons(grid), Is.True, "The grid-level gate (FTL, disabled marker, expedition map).");
                    Assert.That(xform.Anchored, Is.True, "The turret is anchored.");
                    Assert.That(xform.GridUid, Is.EqualTo(grid), "The turret is on the retrieved grid.");
                    Assert.That(controllable.NextFire, Is.LessThanOrEqualTo(timing.CurTime), $"Controllable cooldown {controllable.NextFire} against now {timing.CurTime}.");
                    Assert.That(gun.NextFire, Is.LessThanOrEqualTo(timing.CurTime), $"Gun cooldown {gun.NextFire} against now {timing.CurTime}.");
                    Assert.That(battery.CurrentCharge, Is.GreaterThan(0), "The turret battery has charge.");
                    Assert.That(ammo.Shots, Is.GreaterThan(0), "The battery ammo provider counts shots.");
                });
            });

            Assert.That(await FireOnceAndCountProjectiles(pair, grid, "After the retrieve"), Is.GreaterThan(0), "After the retrieve a shot through the server still spawns a projectile.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The most catastrophic-if-wrong assertion in the drydock. A store now freezes the ship
        /// onto a private paused map before it serializes anything, so every document filed from
        /// that point on carries <c>paused: true</c> on every entity aboard, and a retrieve loads it
        /// onto a paused map on purpose so it arrives frozen for free. The thaw is therefore load
        /// bearing in a way it never was before: miss it, or terminate the walk early, and the
        /// player gets back a ship that looks perfectly intact and does nothing at all. No door
        /// opens, no gun fires, no atmos moves, and no error is logged anywhere.
        ///
        /// <para>Three independent checks, because each one alone can pass while the ship is still
        /// dead. The per-entity flag catches a walk that stopped short. The map's own pause state
        /// catches a ship parked correctly but left on the staging map, which would freeze it again
        /// on the next tick regardless of what the entities say. And the turret catches the case no
        /// flag can: the firing loop runs on <c>EntityQueryEnumerator</c>, which skips paused
        /// entities, so a shot that produces a projectile is the only proof that the engine's own
        /// enumerators agree with the flag we just read.</para>
        ///
        /// <para>The document control is what stops this passing vacuously. If someone drops the
        /// freeze, the filed document carries no paused flags, the retrieved ship is trivially
        /// unpaused, and every assertion below still passes while testing nothing. Reading the blob
        /// back and counting the flags is what ties the assertions to the thing they exist to
        /// guard.</para>
        /// </summary>
        [Test]
        public async Task ARetrievedShipComesBackUnpausedOnAnUnpausedMap()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var mapSys = server.System<SharedMapSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            // The same rig the gunnery test builds, for the same reason it builds it: a powered
            // turret registered with a server is the one probe here that reads the engine's
            // enumerators rather than the flag they are derived from.
            await server.WaitPost(() =>
            {
                entMan.SpawnEntity(ApcProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 2.5f)));
                entMan.SpawnEntity(GunneryConsoleProtoId, new EntityCoordinates(shipGrid, new Vector2(2.5f, 0.5f)));
                entMan.SpawnEntity(TurretProtoId, new EntityCoordinates(shipGrid, new Vector2(2.5f, 2.5f)));
                entMan.SpawnEntity(GunneryServerProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f)));
            });
            await pair.RunTicksSync(60);

            Assert.That(await FireOnceAndCountProjectiles(pair, shipGrid, "Control, before the store"), Is.GreaterThan(0),
                "Control: the turret fires before the ship goes anywhere, so a silent gun afterwards is the round trip's doing.");

            // Counted after the control shot has settled, so the gunshot audio entity the shot
            // spawns has despawned and cannot inflate the figure the document is measured against.
            await pair.RunTicksSync(30);
            var savableBefore = await CountSavableTree(pair, shipGrid);
            Assert.That(savableBefore, Is.GreaterThan(4),
                "The control on the count: the fixture carries an airlock, an APC, a console, a turret and a gunnery server, so anything smaller means the walk is not seeing the ship.");

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            // The document control. A freeze that silently stopped happening would leave every
            // assertion below passing on a ship that was never frozen in the first place.
            var document = Encoding.UTF8.GetString(DrydockSystem.DecompressZstd(await ReadBlobs(db, shipId!.Value)));
            var pausedLines = document
                .Split('\n')
                .Count(line => line.Trim().Equals("paused: true", StringComparison.OrdinalIgnoreCase));

            Assert.That(pausedLines, Is.GreaterThanOrEqualTo(savableBefore),
                $"The filed document carries {pausedLines} paused flags against {savableBefore} savable entities aboard. "
                + "The freeze pauses the grid and every descendant before the serializer walks the tree, so a shortfall means the store filed a ship that was never frozen.");

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            var grid = retrieved.Grid!.Value;

            // Nothing is nudged after the retrieve. The ship has to thaw itself.
            await pair.RunTicksSync(30);

            await server.WaitAssertion(() =>
            {
                // Materialised rather than asserted inside the walk, so a failure names every frozen
                // entity instead of stopping at whichever one the stack happened to reach first.
                var frozen = new List<string>();
                var visited = 0;
                var savableAfter = 0;

                var stack = new Stack<EntityUid>();
                stack.Push(grid);

                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    var meta = entMan.GetComponent<MetaDataComponent>(current);

                    visited++;
                    if (current != grid && meta.EntityPrototype?.MapSavable != false)
                        savableAfter++;

                    if (meta.EntityPaused)
                        frozen.Add($"{meta.EntityPrototype?.ID ?? "<no prototype>"} ({current})");

                    var children = entMan.GetComponent<TransformComponent>(current).ChildEnumerator;
                    while (children.MoveNext(out var child))
                        stack.Push(child);
                }

                Assert.Multiple(() =>
                {
                    Assert.That(visited, Is.GreaterThan(savableBefore),
                        "The control on the walk: it has to reach the grid and everything under it, or an empty walk would report every entity unpaused.");

                    Assert.That(savableAfter, Is.EqualTo(savableBefore),
                        "The ship came back whole; a short walk here would weaken the frozen check above rather than fail on its own.");

                    Assert.That(frozen, Is.Empty,
                        $"{frozen.Count} entities came back still paused: {string.Join(", ", frozen.Take(10))}. "
                        + "The thaw is a per-entity walk, so a partial one leaves exactly this: a ship that looks intact and cannot act.");

                    var mapUid = entMan.GetComponent<TransformComponent>(grid).MapUid!.Value;

                    // IsPaused is MapPaused or not-yet-map-initialised, and both of those freeze
                    // everything on the map regardless of what the entities themselves say. Either
                    // one here means the ship never left the drydock's private map.
                    Assert.That(mapSys.IsPaused(mapUid), Is.False,
                        "The retrieved ship is on a live, map-initialised map. A paused or pre-init map re-freezes the whole ship whatever the per-entity flags read.");
                });
            });

            Assert.That(await FireOnceAndCountProjectiles(pair, grid, "After the retrieve"), Is.GreaterThan(0),
                "The engine's own enumerators agree the ship is awake: the firing loop skips paused entities, so a projectile is proof the flag is not merely clear on paper.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Everything under the grid that a document would carry, counted. Entities whose prototype
        /// is not map savable are skipped for the same reason the roster sweep skips them: a gunshot
        /// sound is a real child of the grid and is never written, so counting it would set a floor
        /// the document can never meet.
        /// </summary>
        private static async Task<int> CountSavableTree(TestPair pair, EntityUid grid)
        {
            var count = 0;
            var entMan = pair.Server.EntMan;

            await pair.Server.WaitPost(() =>
            {
                var stack = new Stack<EntityUid>();
                stack.Push(grid);

                while (stack.Count > 0)
                {
                    var children = entMan.GetComponent<TransformComponent>(stack.Pop()).ChildEnumerator;
                    while (children.MoveNext(out var child))
                    {
                        if (entMan.GetComponent<MetaDataComponent>(child).EntityPrototype?.MapSavable != false)
                            count++;

                        stack.Push(child);
                    }
                }
            });

            return count;
        }

        /// <summary>
        /// Fires the grid's one turret through its gunnery server at a point well clear of the hull,
        /// exactly as the console does, and returns how many new projectiles exist a few ticks later.
        /// </summary>
        private static async Task<int> FireOnceAndCountProjectiles(TestPair pair, EntityUid grid, string when)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var fireControl = server.System<FireControlSystem>();
            var xform = server.System<SharedTransformSystem>();

            // The gun brings its own cooldown to this probe. WeaponTurretFang fires in bursts and
            // parks NextFire two seconds out at the end of one (Gun.burstCooldown 2), and the round
            // trip carries what is left of that: NextFire is a TimeOffsetSerializer field, so the
            // store writes the residue and the retrieve rebases it onto load time. A fixed settle
            // after a retrieve is therefore a race against the control shot this same helper fired
            // earlier, and a gun that loses it looks exactly like a gun that is still frozen.
            var cooldownTicks = 0;
            await server.WaitPost(() =>
            {
                var timing = server.ResolveDependency<IGameTiming>();
                var turretUid = ChildrenWith<FireControllableComponent>(entMan, grid).Single();
                var gun = entMan.GetComponent<GunComponent>(turretUid);
                var remaining = gun.NextFire - timing.CurTime;
                cooldownTicks = remaining > TimeSpan.Zero
                    ? (int) Math.Ceiling(remaining.TotalSeconds * timing.TickRate) + 1
                    : 0;
            });

            if (cooldownTicks > 0)
                await pair.RunTicksSync(cooldownTicks);

            var before = 0;
            var attempted = false;
            await server.WaitPost(() =>
            {
                before = entMan.Count<ShipWeaponProjectileComponent>();
                var turretUid = ChildrenWith<FireControllableComponent>(entMan, grid).Single();
                var mapUid = entMan.GetComponent<TransformComponent>(grid).MapUid!.Value;
                // Aim outward from the hull's corner, in whatever direction the ship happens to
                // face: the line-of-sight check refuses a shot through the ship's own machines,
                // and a retrieved ship comes back at the angle proximity placement chose.
                var turretPos = xform.GetWorldPosition(turretUid);
                var outward = Vector2.Normalize(turretPos - xform.GetWorldPosition(grid));
                var target = new EntityCoordinates(mapUid, turretPos + outward * 40f);
                // The console path minus the console: AttemptFire is what FireWeapons calls per
                // weapon, and its return says whether the gun itself was reached.
                attempted = fireControl.AttemptFire(turretUid, turretUid, target);
            });
            Assert.That(attempted, Is.True, $"{when}: AttemptFire reached the gun: power, server link, cooldown and line of sight all passed.");

            // The gun itself fires from the auto-shoot loop on the next tick, not inside
            // AttemptFire, so the state one tick later says what that loop did with it.
            await pair.RunTicksSync(1);
            await server.WaitAssertion(() =>
            {
                var timing = server.ResolveDependency<IGameTiming>();
                var turretUid = ChildrenWith<FireControllableComponent>(entMan, grid).Single();
                var gun = entMan.GetComponent<GunComponent>(turretUid);
                var auto = entMan.GetComponent<AutoShootGunComponent>(turretUid);
                var ammo = entMan.GetComponent<ProjectileBatteryAmmoProviderComponent>(turretUid);
                var battery = entMan.GetComponent<BatteryComponent>(turretUid);
                var meta = entMan.GetComponent<MetaDataComponent>(turretUid);
                var blocker = server.System<ActionBlockerSystem>();
                Assert.Multiple(() =>
                {
                    Assert.That(meta.EntityPaused, Is.False, "The turret is not paused; the firing loop skips paused entities.");
                    Assert.That(entMan.GetComponent<MetaDataComponent>(grid).EntityPaused, Is.False, "The grid is not paused.");
                    Assert.That(auto.RemainingTime, Is.GreaterThan(TimeSpan.Zero).Or.EqualTo(TimeSpan.Zero), $"Auto-shoot remaining {auto.RemainingTime}, enabled {auto.Enabled}, on {auto.On}, can fire {auto.CanFire}, user {auto.User}.");
                    Assert.That(blocker.CanAttack(turretUid), Is.True, "The action blocker lets the turret attack.");
                    Assert.That(gun.ShootCoordinates, Is.Not.Null, "The gun holds its shoot coordinates.");
                    Assert.That(gun.FireRateModified, Is.GreaterThan(0f), $"Fire rate modified {gun.FireRateModified} against base {gun.FireRate}.");
                    // Deliberately a loose bound, and it is measuring a jump rather than a deadline.
                    // A gun that just fired is always a little ahead of now, by one inter-shot step
                    // (measured 0.17 s here). The empty-shot branch instead adds the whole two-second
                    // burst cooldown, so anything under a second says the shot was real.
                    Assert.That(gun.NextFire, Is.LessThan(timing.CurTime + TimeSpan.FromSeconds(1)), $"Gun NextFire {gun.NextFire} against now {timing.CurTime}: a jump of the burst cooldown means the empty-shot branch ran.");
                    Assert.That(ammo.Shots, Is.LessThan(800), $"The ammo provider gave up a shot (shots {ammo.Shots}, charge {battery.CurrentCharge}).");
                    Assert.That(entMan.Count<ShipWeaponProjectileComponent>() - before, Is.GreaterThan(0), "One tick after the shot a projectile exists.");
                });
            });
            await pair.RunTicksSync(4);

            var after = 0;
            await server.WaitPost(() => after = entMan.Count<ShipWeaponProjectileComponent>());
            return after - before;
        }

        private static void AssertGunneryLinked(IEntityManager entMan, EntityUid grid, string when)
        {
            var serverUid = ChildrenWith<FireControlServerComponent>(entMan, grid).Single();
            var consoleUid = ChildrenWith<FireControlConsoleComponent>(entMan, grid).Single();
            var turretUid = ChildrenWith<FireControllableComponent>(entMan, grid).Single();

            var serverComp = entMan.GetComponent<FireControlServerComponent>(serverUid);
            Assert.Multiple(() =>
            {
                Assert.That(entMan.GetComponent<ApcPowerReceiverComponent>(serverUid).Powered, Is.True, $"Control {when}: the server reads as powered.");
                Assert.That(serverComp.ConnectedGrid, Is.EqualTo(grid), $"{when}: the server connected to its grid.");
                Assert.That(entMan.TryGetComponent<FireControlGridComponent>(grid, out var gridComp) && gridComp.ControllingServer == serverUid, Is.True, $"{when}: the grid names the server.");
                Assert.That(serverComp.Controlled, Does.Contain(turretUid), $"{when}: the server controls the turret.");
                Assert.That(entMan.GetComponent<FireControllableComponent>(turretUid).ControllingServer, Is.EqualTo(serverUid), $"{when}: the turret knows its server.");
                Assert.That(entMan.GetComponent<FireControlConsoleComponent>(consoleUid).ConnectedServer, Is.EqualTo(serverUid), $"{when}: the console is linked to the server.");
                Assert.That(serverComp.Consoles, Does.Contain(consoleUid), $"{when}: the server lists the console.");
            });
        }

        /// <summary>
        /// The berth is a parking spot: a successful retrieve empties it, and only once the ship is
        /// really out. A retrieve that fails after the claim leaves the berth exactly as it was, so
        /// the release never has to re-seat a berth another store may have taken in the meantime.
        /// The failure is induced by corrupting the stored document in place, which the ladder
        /// catches after the claim and before anything is materialized.
        /// </summary>
        [Test]
        public async Task ARetrieveVacatesTheBerthOnlyWhenItSucceeds()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            var berth = await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            var seated = (await store.LoadCurrent(shipId!.Value))!.Ship;
            Assert.That(seated.BerthId, Is.EqualTo(berth), "A stored ship sits in the berth the store found for it.");

            // Break the only document, so the retrieve claims the row, finds nothing that verifies,
            // and releases. The two error lines that produces are the ladder doing its job.
            var original = await ReadBlobs(db, shipId.Value);
            await WriteBlobs(db, shipId.Value, new byte[] { 1, 2, 3 });

            var failureLevel = pair.ServerLogHandler.FailureLevel;
            pair.ServerLogHandler.FailureLevel = LogLevel.Fatal;
            var refused = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId.Value, owner, station, null));
            pair.ServerLogHandler.FailureLevel = failureLevel;

            Assert.That(refused.Result, Is.EqualTo(DrydockRetrieveResult.NoReadableRevision), "A document that fails its checksum must not come back as a ship.");

            var afterRefusal = (await store.LoadCurrent(shipId.Value))!.Ship;
            Assert.Multiple(() =>
            {
                Assert.That(afterRefusal.State, Is.EqualTo(DrydockShipState.Stored), "A failed retrieve releases the claim.");
                Assert.That(afterRefusal.BerthId, Is.EqualTo(berth), "A failed retrieve leaves the berth exactly as it was.");
            });

            // Mend it and bring it out for real.
            await WriteBlobs(db, shipId.Value, original);
            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            var afterRetrieve = (await store.LoadCurrent(shipId.Value))!.Ship;
            Assert.Multiple(() =>
            {
                Assert.That(afterRetrieve.State, Is.EqualTo(DrydockShipState.CheckedOut));
                Assert.That(afterRetrieve.BerthId, Is.Null, "The ship is out, so its slot is empty as far as the player can see.");
                Assert.That(afterRetrieve.LastBerthId, Is.EqualTo(berth), "The slot it came out of is remembered, so it goes back there.");
            });

            var slots = await store.GetBerths(owner);
            Assert.That(slots.Single(s => s.Berth.BerthId == berth).Occupant, Is.Null);

            // And back in, to the same slot.
            var (again, sameShip) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(retrieved.Grid!.Value, owner, null));
            Assert.Multiple(() =>
            {
                Assert.That(again, Is.EqualTo(DrydockStoreResult.Success));
                Assert.That(sameShip, Is.EqualTo(shipId), "A re-store files against the same hull.");
            });

            var reseated = (await store.LoadCurrent(shipId.Value))!.Ship;
            Assert.That(reseated.BerthId, Is.EqualTo(berth));

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The row is authoritative for ownership and the grid learns it at retrieve. Without the
        /// re-stamp a transferred ship comes back carrying its previous owner, the console refuses
        /// the new owner's store as "not yours", and the old owner could file it back under
        /// themselves. This is the round trip that proves the loop is closed.
        /// </summary>
        [Test]
        public async Task ATransferredShipComesBackStampedForItsNewOwner()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();

            var seller = Guid.NewGuid();
            var buyer = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, seller);
            await DrydockTestHelpers.InsertPlayer(db, buyer);
            await store.AddBerth(seller, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);
            await store.AddBerth(buyer, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);
            await server.WaitPost(() => entMan.EnsureComponent<ShipOwnershipComponent>(shipGrid).OwnerUserId = new NetUserId(seller));

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, seller, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            var (offered, offer) = await store.TryOfferTransfer(shipId!.Value, seller, buyer, TimeSpan.FromMinutes(30), null);
            Assert.That(offered, Is.EqualTo(DrydockBerthResult.Success));
            var (moved, _, _) = await store.TryAcceptTransfer(offer!.Id, buyer, null);
            Assert.That(moved, Is.EqualTo(DrydockBerthResult.Success));

            // The previous owner can no longer bring it out; the new one can.
            var refused = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId.Value, seller, station, null));
            Assert.That(refused.Result, Is.EqualTo(DrydockRetrieveResult.NotOwned), "A ship that changed hands is not the previous owner's to retrieve.");

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId.Value, buyer, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var ownership = entMan.GetComponent<ShipOwnershipComponent>(retrieved.Grid!.Value);
                Assert.That(ownership.OwnerUserId.UserId, Is.EqualTo(buyer),
                    "The grid must carry the row's owner, or the console refuses the buyer's store and the seller's store files it back under them.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A ship whose grid is still in the world is not lost, and restoring its row would let it
        /// be retrieved into a second copy while the first flies. The guard is the system's, since
        /// only the entity world knows; the store cannot. Deleting the grid is what makes the same
        /// restore legitimate.
        /// </summary>
        [Test]
        public async Task AnAdminCannotRestoreAShipThatIsStillInTheWorld()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            var admin = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await DrydockTestHelpers.InsertPlayer(db, admin);
            var berth = await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(drydock.IsShipLive(shipId!.Value), Is.True, "The control: the retrieved grid carries the hull's id.");
            });

            var refused = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryAdminRestore(shipId!.Value, berth, admin, null, "player says it vanished"));
            Assert.That(refused, Is.EqualTo(DrydockBerthResult.WrongState), "A hull that is in the world cannot be restored: that would be a duplicate.");
            Assert.That((await store.LoadCurrent(shipId!.Value))!.Ship.State, Is.EqualTo(DrydockShipState.CheckedOut));

            // Now it really is gone.
            await server.WaitPost(() => entMan.DeleteEntity(retrieved.Grid!.Value));
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(drydock.IsShipLive(shipId!.Value), Is.False);
            });

            var restored = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryAdminRestore(shipId!.Value, berth, admin, null, "hull lost to a bug"));
            Assert.That(restored, Is.EqualTo(DrydockBerthResult.Success));

            var row = (await store.LoadCurrent(shipId!.Value))!.Ship;
            Assert.Multiple(() =>
            {
                Assert.That(row.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(row.BerthId, Is.EqualTo(berth));
            });

            await pair.CleanReturnAsync();
        }

        private static IEnumerable<EntityUid> ChildrenWith<T>(IEntityManager entMan, EntityUid grid) where T : IComponent
        {
            var children = entMan.GetComponent<TransformComponent>(grid).ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                if (entMan.HasComponent<T>(child))
                    yield return child;
            }
        }

        private static Task<byte[]> ReadBlobs(IServerDbManager db, Guid shipId)
        {
            return db.RunTriadDbCommand(async (context, token) =>
            {
                var row = await context.DrydockBlob.AsNoTracking().SingleAsync(b => b.ShipGuid == shipId, token);
                return row.Blob;
            }, CancellationToken.None);
        }

        private static Task WriteBlobs(IServerDbManager db, Guid shipId, byte[] bytes)
        {
            return db.RunTriadDbCommand(async (context, token) =>
            {
                var rows = await context.DrydockBlob.Where(b => b.ShipGuid == shipId).ToListAsync(token);
                foreach (var row in rows)
                    row.Blob = bytes;

                await context.SaveChangesAsync(token);
            }, CancellationToken.None);
        }

        /// <summary>
        /// Damage is the second reason a ship needs a fidelity layer at all, and it is a different
        /// reason from the first. <c>DamageableComponent.Damage</c> is not unserializable, it is
        /// declared read-only to the serializer, so it is never written and a shot-up hull comes
        /// back pristine. That is a free repair on every combat vessel in a fork whose ships get
        /// shot at, which is why the sidecar carries the raw damage dictionary across.
        /// </summary>
        [Test]
        public async Task DamageSurvivesTheRoundTrip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var damageSys = server.System<DamageableSystem>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, airlock) = await BuildShipAndStation(pair);

            FixedPoint2 damageBefore = default;

            await server.WaitPost(() =>
            {
                var blunt = protoMan.Index(BluntDamage);
                var specifier = new DamageSpecifier(blunt, FixedPoint2.New(37));
                damageSys.TryChangeDamage(airlock, specifier, ignoreResistances: true);
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                damageBefore = entMan.GetComponent<DamageableComponent>(airlock).TotalDamage;
                Assert.That(damageBefore, Is.GreaterThan(FixedPoint2.Zero),
                    "The control: the airlock has to actually be damaged, or the comparison after the round trip proves nothing.");
            });

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            await pair.RunTicksSync(5);

            var retrievedAirlock = await FindChildWithComponent<WiresComponent>(pair, retrieved.Grid!.Value);
            Assert.That(retrievedAirlock, Is.Not.Null);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.GetComponent<DamageableComponent>(retrievedAirlock!.Value).TotalDamage,
                    Is.EqualTo(damageBefore),
                    "Damage is read-only to the serializer, so this passes only because the sidecar carried it and the rehydrate pass applied it.");

                Assert.That(entMan.HasComponent<DrydockDamageSidecarComponent>(retrievedAirlock.Value), Is.False,
                    "The sidecar is scaffolding for the crossing. Leaving it aboard would re-apply the same damage on the next retrieve.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The second of the three Revive steps the map-init census added. Device network
        /// membership is registration held by the network system rather than state on the device,
        /// and joining happens on map init, which never fires again for a restored entity. Without
        /// the step a retrieved ship's alarms, sensors and consoles come back present, powered, and
        /// deaf: nothing about them looks wrong from the outside.
        /// </summary>
        [Test]
        public async Task DeviceNetworkMembershipComesBack()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var deviceNet = server.System<DeviceNetworkSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            // The airlock the wires assertion uses carries DeviceNetwork too, so one entity covers
            // both steps.
            var (station, shipGrid, airlock) = await BuildShipAndStation(pair);

            await server.WaitAssertion(() =>
            {
                var device = entMan.GetComponent<DeviceNetworkComponent>(airlock);
                Assert.That(deviceNet.IsDeviceConnected(airlock, device), Is.True,
                    "The control: a live airlock has to be on its network, or the check after the round trip means nothing.");
            });

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            await pair.RunTicksSync(5);

            var retrievedAirlock = await FindChildWithComponent<WiresComponent>(pair, retrieved.Grid!.Value);
            Assert.That(retrievedAirlock, Is.Not.Null);

            await server.WaitAssertion(() =>
            {
                var device = entMan.GetComponent<DeviceNetworkComponent>(retrievedAirlock!.Value);
                Assert.That(deviceNet.IsDeviceConnected(retrievedAirlock.Value, device), Is.True,
                    "Membership is not serialized state, so this passes only because Revive re-ran the join by hand.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Mechs stay with the round (user, 2026-09-13): a retrieve deletes every mech, mech part and
        /// frame, mech board and mech tech disk aboard, drops what a mech held that is not mech kit
        /// itself onto the deck, and leaves everything else alone.
        /// </summary>
        [Test]
        public async Task MechsAndMechKitDoNotRideAShip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var containers = server.System<SharedContainerSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, airlock) = await BuildShipAndStation(pair);

            // A mech itself is not here on purpose: BaseMech is save: false, so the store already leaves
            // it out. The grabber is the holder that does reach the retrieve, carrying a wrench.
            var stripped = new[] { "MechEquipmentGrabber", "RipleyLArm", "RipleyCentralElectronics", "TechDiskMechCiv" };

            await server.WaitPost(() =>
            {
                var at = entMan.GetComponent<TransformComponent>(airlock).Coordinates;
                foreach (var id in stripped)
                {
                    var spawned = entMan.SpawnEntity(id, at);
                    if (id == "MechEquipmentGrabber")
                    {
                        var load = containers.EnsureContainer<Container>(spawned, "item-container");
                        containers.Insert(entMan.SpawnEntity("Wrench", at), load);
                    }
                }

                entMan.SpawnEntity("Crowbar", at);
            });
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var protos = drydock.StripPrototypeIds();
                Assert.Multiple(() =>
                {
                    foreach (var id in new[] { "MechRipley", "RipleyChassis", "MechEquipmentGrabber", "GygaxArmorPlate", "RipleyCentralElectronics", "TechDiskMechCiv" })
                        Assert.That(protos, Does.Contain(id), $"The strip rule covers {id}.");
                    Assert.That(protos, Does.Not.Contain("Crowbar"), "Control: an ordinary tool is not mech kit.");
                    Assert.That(protos, Does.Not.Contain("Wrench"), "Control: the grabber's load is not mech kit.");

                    // The mech-sized thruster, air tank and IFF module are stripped; the ship-sized ones
                    // they share a word with are ship equipment and must never be.
                    foreach (var id in new[] { "MechThruster", "MechAirTank", "MechIFFTSF" })
                        Assert.That(protos, Does.Contain(id), $"The strip rule covers the mech-sized {id}.");
                    foreach (var id in new[] { "Thruster", "AirCanister", "ComputerIFF" })
                        Assert.That(protos, Does.Not.Contain(id), $"Ship equipment {id} is not mech kit.");
                });
            });

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var grid = retrieved.Grid!.Value;
                var protos = drydock.StripPrototypeIds();
                var aboard = new List<string>();
                foreach (var uid in server.System<DrydockFidelitySystem>().GridTreeList(grid))
                {
                    if (entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype is { } p)
                        aboard.Add(p.ID);
                }

                Assert.Multiple(() =>
                {
                    Assert.That(aboard.Where(protos.Contains), Is.Empty, "No mech kit came back aboard.");
                    Assert.That(aboard, Does.Contain("Crowbar"), "Control: ordinary cargo rides the ship as before.");
                    Assert.That(aboard, Does.Contain("Wrench"), "What the grabber held was dropped on the deck, not deleted with it.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// An impound puts each occupant down by role (user, 2026-09-13): someone whose mind holds a
        /// job lands on that job's spawn point even when a plain one is nearer, someone with no job
        /// lands on the nearest, and an admin ghost aboard is lifted off rather than deleted with the
        /// hull.
        /// </summary>
        [Test]
        public async Task AnImpoundPutsOccupantsDownByRoleAndLiftsGhosts()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var drydock = server.System<DrydockSystem>();
            var minds = server.System<MindSystem>();
            var roles = server.System<Content.Server.Roles.RoleSystem>();
            var stationSys = server.System<StationSystem>();
            var mapSys = server.System<SharedMapSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);
            var job = protoMan.EnumeratePrototypes<Content.Shared.Roles.JobPrototype>().First().ID;

            EntityUid stationGrid = default, farGrid = default, crew = default, drifter = default, ghost = default;

            await server.WaitPost(() =>
            {
                stationGrid = stationSys.GetLargestGrid(entMan.GetComponent<StationDataComponent>(station))!.Value;
                var map = entMan.GetComponent<TransformComponent>(shipGrid).MapUid!.Value;

                // The plain spawn point sits on the station grid, near; the job's sits far out on a
                // grid of its own, so only the role can explain choosing it.
                var plain = entMan.SpawnEntity(null, new EntityCoordinates(stationGrid, new Vector2(0.5f, 0.5f)));
                entMan.EnsureComponent<Content.Server.Spawners.Components.SpawnPointComponent>(plain).SpawnType =
                    Content.Server.Spawners.Components.SpawnPointType.LateJoin;

                var far = mapSys.CreateGridEntity(entMan.GetComponent<MapComponent>(map).MapId);
                farGrid = far.Owner;
                entMan.GetComponent<TransformComponent>(farGrid).LocalPosition = new Vector2(200f, 200f);
                mapSys.SetTile(far, Vector2i.Zero, new Tile(1));
                var jobPoint = entMan.EnsureComponent<Content.Server.Spawners.Components.SpawnPointComponent>(
                    entMan.SpawnEntity(null, new EntityCoordinates(farGrid, new Vector2(0.5f, 0.5f))));
                jobPoint.SpawnType = Content.Server.Spawners.Components.SpawnPointType.Job;
                jobPoint.Job = job;

                crew = entMan.SpawnEntity("MobHuman", new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f)));
                var crewMind = minds.CreateMind(null, "Crew");
                minds.TransferTo(crewMind, crew);
                roles.MindAddJobRole(crewMind, crewMind.Comp, silent: true, jobPrototype: job);

                drifter = entMan.SpawnEntity("MobHuman", new EntityCoordinates(shipGrid, new Vector2(1.5f, 0.5f)));
                minds.TransferTo(minds.CreateMind(null, "Drifter"), drifter);

                ghost = entMan.SpawnEntity("AdminObserver", new EntityCoordinates(shipGrid, new Vector2(2.5f, 0.5f)));
            });

            await pair.MakeCleanupImmune(farGrid);
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.GetComponent<TransformComponent>(ghost).GridUid, Is.EqualTo(shipGrid), "The control: the ghost starts aboard.");
                Assert.That(entMan.GetComponent<TransformComponent>(crew).GridUid, Is.EqualTo(shipGrid), "The control: the crew starts aboard.");
            });

            var (result, _) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryImpoundShip(
                shipGrid, owner, null, new DrydockImpound(0, "test", Redeemable: true, ActorUserId: null), inline: true));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(entMan.Deleted(shipGrid), Is.True, "The control: the hull went into the lot.");
                    Assert.That(entMan.GetComponent<TransformComponent>(crew).GridUid, Is.EqualTo(farGrid),
                        "Someone with a job is put down at that job's spawn point, far as it is.");
                    Assert.That(entMan.GetComponent<TransformComponent>(drifter).GridUid, Is.EqualTo(stationGrid),
                        "Someone with no job is put down at the nearest spawn point.");
                    Assert.That(entMan.Deleted(ghost), Is.False, "The admin ghost was lifted off, not deleted with the hull.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>A ghost aboard an ordinary store is lifted off too, not frozen and deleted with the hull.</summary>
        [Test]
        public async Task AStoreLiftsAGhostOffTheHull()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (_, shipGrid, _) = await BuildShipAndStation(pair);

            EntityUid ghost = default;
            await server.WaitPost(() => ghost = entMan.SpawnEntity("AdminObserver", new EntityCoordinates(shipGrid, new Vector2(1.5f, 1.5f))));
            await pair.RunTicksSync(5);

            var (result, _) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success), "A ghost never blocks a store.");

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.Deleted(shipGrid), Is.True, "The control: the hull was stored and left the world.");
                Assert.That(entMan.Deleted(ghost), Is.False, "The ghost survived the store.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A station beacon aboard comes back on the ship's nav map. The beacon list is rebuilt, not
        /// serialized, when the grid joins its station, so the join has to see the beacons live: the
        /// retrieve joins the station only after the dock's move has thawed the ship
        /// (<c>DrydockSystem.Retrieve.cs</c>, the pipeline's tail), and a beacon aboard is listed
        /// without anyone re-anchoring it.
        /// </summary>
        [Test]
        public async Task AStationBeaconAboardComesBackOnTheNavMap()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, airlock) = await BuildShipAndStation(pair);

            await server.WaitPost(() => entMan.SpawnEntity("DefaultStationBeaconBar", entMan.GetComponent<TransformComponent>(airlock).Coordinates));
            await pair.RunTicksSync(5);

            var beaconBefore = await FindChildWithComponent<NavMapBeaconComponent>(pair, shipGrid);
            Assert.That(beaconBefore, Is.Not.Null, "The control: the beacon has to sit on the ship before it is stored.");

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            await pair.RunTicksSync(5);

            var grid = retrieved.Grid!.Value;
            var beacon = await FindChildWithComponent<NavMapBeaconComponent>(pair, grid);
            Assert.That(beacon, Is.Not.Null, "The beacon entity rode the document.");

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.TryGetComponent<NavMapComponent>(grid, out var navMap), Is.True, "Joining a station gives the grid its nav map.");
                Assert.That(navMap!.Beacons.ContainsKey(entMan.GetNetEntity(beacon!.Value)), Is.True,
                    "The nav map lists the beacon without anyone re-anchoring it.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The third. A research client's link to its server is a plain property on one side and a
        /// view-variables list on the other, so neither end serializes, and the only thing that
        /// ever sets it is a map-init scan of the client's own grid. A retrieved ship carrying its
        /// own R&amp;D server would have every lathe disconnected from it until somebody opened the
        /// server-selection menu by hand.
        /// </summary>
        [Test]
        public async Task ResearchClientsComeBackRegistered()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            EntityUid lathe = default;

            await server.WaitPost(() =>
            {
                entMan.SpawnEntity(ResearchServerProtoId, new EntityCoordinates(shipGrid, new Vector2(0f, 0f)));
                lathe = entMan.SpawnEntity(LatheProtoId, new EntityCoordinates(shipGrid, new Vector2(2f, 2f)));
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.GetComponent<ResearchClientComponent>(lathe).Server, Is.Not.Null,
                    "The control: the lathe has to find its server while the ship is live, which is the map-init scan doing its job.");
            });

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            await pair.RunTicksSync(5);

            var retrievedLathe = await FindChildWithComponent<ResearchClientComponent>(pair, retrieved.Grid!.Value);
            Assert.That(retrievedLathe, Is.Not.Null, "The lathe came back with the ship.");

            await server.WaitAssertion(() =>
            {
                var client = entMan.GetComponent<ResearchClientComponent>(retrievedLathe!.Value);
                Assert.That(client.Server, Is.Not.Null,
                    "Neither end of the registration is a data field, so this passes only because Revive re-ran the grid scan.");

                Assert.That(entMan.HasComponent<ResearchServerComponent>(client.Server!.Value), Is.True,
                    "And it has to be pointed at a real server, not merely non-null.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The capture manifest, finally carrying something. Everything above tests state the
        /// serializer could write and something forgot to re-run; this tests state the serializer
        /// cannot write at all.
        ///
        /// <para>A lathe queue is a <c>[DataField]</c> whose element type has no serializer
        /// anywhere, which is the exact failure the fidelity probe exists to find. The field is
        /// captured into a sidecar, cleared so the map serializer does not choke on it, and put
        /// back on arrival. Without that the store does not merely lose the queue: the serializer
        /// aborts the whole grid, so this is also the difference between a ship that stores and one
        /// that refuses.</para>
        ///
        /// <para>The other manifest entry is market data, which needs a market to be meaningful.
        /// This covers the mechanism; that entry rides the same code path.</para>
        /// </summary>
        [Test]
        public async Task AQueuedLatheRecipeSurvivesTheRoundTrip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            await server.WaitPost(() =>
            {
                var lathe = entMan.SpawnEntity(LatheProtoId, new EntityCoordinates(shipGrid, new Vector2(2f, 2f)));
                var recipe = protoMan.Index<LatheRecipePrototype>(LatheRecipeId);

                entMan.GetComponent<LatheComponent>(lathe).Queue.Add(
                    new LatheRecipeBatch(recipe, itemsPrinted: 1, itemsRequested: 5, actor: null));

                // Mid-print. The marker has no data fields, so saved it reloaded empty, and a lathe
                // marked producing with no recipe is one the lathe loop never finishes and the
                // reboot pass never restarts, so it sticks in its running animation for good. It
                // opts out of saving now; this is the check that it stays out.
                entMan.EnsureComponent<LatheProducingComponent>(lathe);
            });

            await pair.RunTicksSync(5);

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success),
                "A populated queue must not fail the store. If the probe stopped recognising the gap this would come back SerializeFailed.");

            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            await pair.RunTicksSync(5);

            var retrievedLathe = await FindChildWithComponent<LatheComponent>(pair, retrieved.Grid!.Value);
            Assert.That(retrievedLathe, Is.Not.Null, "The lathe came back with the ship.");

            await server.WaitAssertion(() =>
            {
                var queue = entMan.GetComponent<LatheComponent>(retrievedLathe!.Value).Queue;

                Assert.That(queue, Has.Count.EqualTo(1),
                    "The queue is carried by the capture sidecar, so an empty one here means it was stripped rather than captured, or never restored.");

                Assert.Multiple(() =>
                {
                    Assert.That(queue[0].Recipe.ID, Is.EqualTo(LatheRecipeId));
                    Assert.That(queue[0].ItemsRequested, Is.EqualTo(5));
                    Assert.That(queue[0].ItemsPrinted, Is.EqualTo(1),
                        "Progress through a batch is part of what a player would notice losing.");
                });

                // Either it is not producing, or it is producing something. Never the marker alone,
                // which is the stuck state.
                var producing = entMan.HasComponent<LatheProducingComponent>(retrievedLathe.Value);
                var recipe = entMan.GetComponent<LatheComponent>(retrievedLathe.Value).CurrentRecipe;
                Assert.That(!producing || recipe != null, Is.True,
                    "A lathe must never come back marked producing with no recipe behind it: that lathe never finishes and never restarts.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Three things only map init ever sets up, in one round trip: a smart fridge's stock index,
        /// a robotic arm's declared hand, and the marker that stops the roundstart variation passes
        /// re-littering a ship on every retrieve. Each is a Revive step, and each one missing is a
        /// machine that looks fine and does nothing.
        /// </summary>
        [Test]
        public async Task MapInitDerivedMachineStateComesBack()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var containers = server.System<SharedContainerSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            await server.WaitPost(() =>
            {
                var fridge = entMan.SpawnEntity(SmartFridgeProtoId, new EntityCoordinates(shipGrid, new Vector2(2f, 2f)));
                var stock = entMan.SpawnEntity(MarketItemProtoId, new EntityCoordinates(shipGrid, new Vector2(2f, 2f)));
                var inventory = containers.GetContainer(fridge, entMan.GetComponent<SmartFridgeComponent>(fridge).Container);
                Assert.That(containers.Insert(stock, inventory), Is.True, "The control: the fridge has to hold something before the store.");

                entMan.SpawnEntity(InteractorProtoId, new EntityCoordinates(shipGrid, new Vector2(1f, 2f)));
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var arm = FindChildWithComponentSync<InteractorComponent>(entMan, shipGrid);
                Assert.That(arm, Is.Not.Null);
                Assert.That(entMan.GetComponent<HandsComponent>(arm!.Value).Hands, Is.Not.Empty,
                    "The control: hand-fill gives the arm its hand on map init, so a fresh arm has one.");
            });

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            await pair.RunTicksSync(5);

            var grid = retrieved.Grid!.Value;
            var retrievedFridge = await FindChildWithComponent<SmartFridgeComponent>(pair, grid);
            var retrievedArm = await FindChildWithComponent<InteractorComponent>(pair, grid);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(retrievedFridge, Is.Not.Null, "The fridge came back with the ship.");
                    var entries = entMan.GetComponent<SmartFridgeComponent>(retrievedFridge!.Value).ContainedEntries;
                    Assert.That(entries.Values.Sum(v => v.Count), Is.EqualTo(1),
                        "The stock index is rebuilt only on map init, which a retrieve never fires; without the Revive step the fridge reports itself empty over a full container.");

                    Assert.That(retrievedArm, Is.Not.Null, "The arm came back with the ship.");
                    Assert.That(entMan.GetComponent<HandsComponent>(retrievedArm!.Value).Hands, Is.Not.Empty,
                        "Hands are not data fields and hand-fill only runs on map init; without the Revive step the arm has nothing to hold a tool with.");

                    Assert.That(entMan.HasComponent<StationVariationHasRunComponent>(grid), Is.True,
                        "The variation marker has to ride the grid, or the recreated station is varied again on every retrieve.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The roster sweep can never cover a reactor: no map places one, so every reactor that has
        /// been stored got there because a player installed it. Its layout, its condition and the
        /// grids it ticks against are all built on map init, which a retrieve never fires.
        /// </summary>
        [Test]
        public async Task AReactorComesBackWithItsPartsAndItsHeat()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            await server.WaitPost(() =>
                entMan.SpawnEntity(ReactorProtoId, new EntityCoordinates(shipGrid, new Vector2(2f, 2f))));

            await pair.RunTicksSync(5);

            var savedCells = new Dictionary<Vector2i, EntityUid>();
            var markedCell = default(Vector2i);

            await server.WaitAssertion(() =>
            {
                var reactor = FindChildWithComponentSync<NuclearReactorComponent>(entMan, shipGrid);
                Assert.That(reactor, Is.Not.Null, "The control: the reactor has to be aboard before the store.");

                var comp = entMan.GetComponent<NuclearReactorComponent>(reactor!.Value);
                Assert.That(comp.ComponentGrid, Is.Not.Null,
                    "The control: a freshly placed reactor allocates its grid.");

                savedCells = OccupiedCells(comp);
                Assert.That(savedCells, Is.Not.Empty,
                    "The control: the prefab has to lay parts, or there is no layout for the round trip to lose.");

                markedCell = savedCells.Keys.OrderBy(c => (c.X, c.Y)).First();
                entMan.GetComponent<ReactorPartComponent>(savedCells[markedCell]).Temperature = MarkedPartTemperature;

                comp.Temperature = MarkedReactorTemperature;
                comp.ControlRodInsertion = MarkedControlRodInsertion;
                comp.Melted = true;
            });

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            await pair.RunTicksSync(5);

            var retrievedReactor = await FindChildWithComponent<NuclearReactorComponent>(pair, retrieved.Grid!.Value);

            await server.WaitAssertion(() =>
            {
                Assert.That(retrievedReactor, Is.Not.Null, "The reactor came back with the ship.");

                var comp = entMan.GetComponent<NuclearReactorComponent>(retrievedReactor!.Value);

                Assert.That(comp.ComponentGrid, Is.Not.Null,
                    "The grid is allocated on ComponentStartup. Null here means the reactor is still building itself on map init, which a retrieve never fires.");
                Assert.That(comp.ApplyPrefab, Is.False,
                    "A restored reactor must never be armed to re-lay its prefab: that calls CleanContainer on the storage it just loaded.");

                var loadedCells = OccupiedCells(comp);

                Assert.Multiple(() =>
                {
                    Assert.That(comp.PartStorage.ContainedEntities, Has.Count.EqualTo(savedCells.Count),
                        "Every saved part should be back in the reactor's storage.");
                    Assert.That(loadedCells.Keys, Is.EquivalentTo(savedCells.Keys),
                        "Every part should be back in the cell it was saved in.");

                    Assert.That(comp.Temperature, Is.EqualTo(MarkedReactorTemperature),
                        "Casing temperature should survive; a fresh reactor reads room temperature.");
                    Assert.That(comp.ControlRodInsertion, Is.EqualTo(MarkedControlRodInsertion),
                        "The control rod setting should survive rather than snapping back to its default.");
                    Assert.That(comp.Melted, Is.True,
                        "A melted reactor must come back melted, not repaired by the round trip.");
                });

                Assert.That(loadedCells, Does.ContainKey(markedCell));
                Assert.That(entMan.GetComponent<ReactorPartComponent>(loadedCells[markedCell]).Temperature,
                    Is.EqualTo(MarkedPartTemperature),
                    "The part in the marked cell should be the part that was stored. Reading the prototype's temperature means the prefab was re-laid over the restored parts.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The turbine half of the same fault. Its blade and stator ride item slots and come back on
        /// their own, but the references to them are rebuilt rather than saved, and the method that
        /// rebuilt them also set BladeHealth to full: a damaged turbine would have come back repaired.
        /// </summary>
        [Test]
        public async Task ATurbineComesBackSpinningAndStillDamaged()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            await server.WaitPost(() =>
                entMan.SpawnEntity(TurbineProtoId, new EntityCoordinates(shipGrid, new Vector2(2f, 2f))));

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var turbine = FindChildWithComponentSync<GasTurbineComponent>(entMan, shipGrid);
                Assert.That(turbine, Is.Not.Null, "The control: the turbine has to be aboard before the store.");

                var comp = entMan.GetComponent<GasTurbineComponent>(turbine!.Value);

                Assert.Multiple(() =>
                {
                    Assert.That(comp.CurrentBlade, Is.Not.Null,
                        "The control: the blade slot's starting item fills on map init, so a fresh turbine is fitted.");
                    Assert.That(comp.CurrentStator, Is.Not.Null, "The control: same for the stator.");
                });

                comp.RPM = MarkedRpm;
                comp.BladeHealth = MarkedBladeHealth;
            });

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            await pair.RunTicksSync(5);

            var retrievedTurbine = await FindChildWithComponent<GasTurbineComponent>(pair, retrieved.Grid!.Value);

            await server.WaitAssertion(() =>
            {
                Assert.That(retrievedTurbine, Is.Not.Null, "The turbine came back with the ship.");

                var comp = entMan.GetComponent<GasTurbineComponent>(retrievedTurbine!.Value);

                Assert.Multiple(() =>
                {
                    Assert.That(comp.BladeHealth, Is.EqualTo(MarkedBladeHealth),
                        "Blade damage must survive. Reading BladeHealthMax here means UpdatePartValues ran on the load path and repaired the turbine.");
                    Assert.That(comp.RPM, Is.GreaterThan(0f),
                        $"A turbine stored while spinning at {MarkedRpm} must not come back stopped: RPM is integrator state, not something a tick re-derives.");

                    Assert.That(comp.CurrentBlade, Is.Not.Null,
                        "The blade rides an item slot and comes back on its own, but the reference to it is rebuilt on startup. Null here means that rebuild still hangs off map init.");
                    Assert.That(comp.CurrentStator, Is.Not.Null, "The stator reference, same rebuild.");
                });

                Assert.That(entMan.EntityExists(comp.CurrentBlade!.Value), Is.True,
                    "The blade reference has to point at an entity that came back, not a stale uid.");
            });

            await pair.CleanReturnAsync();
        }

        private static Dictionary<Vector2i, EntityUid> OccupiedCells(NuclearReactorComponent comp)
        {
            var occupied = new Dictionary<Vector2i, EntityUid>();

            for (var x = 0; x < comp.ReactorGridWidth; x++)
                for (var y = 0; y < comp.ReactorGridHeight; y++)
                    if (comp.ComponentGrid[x, y] is { } part)
                        occupied[new Vector2i(x, y)] = part.Owner;

            return occupied;
        }

        /// <summary>
        /// A xenoartifact is the one entity aboard whose whole structure is a NetEntity graph. The
        /// fidelity probe had no NetEntity writer, so it judged every such field unserializable and
        /// blanked it before the save: the vertex array went to null and the serializer refused the
        /// entire ship. The map serializer remaps NetEntity like EntityUid, so the probe must leave
        /// those fields alone; this proves the store goes through and the graph comes back pointing
        /// at real nodes.
        /// </summary>
        [Test]
        public async Task AnArtifactSurvivesTheRoundTrip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            await server.WaitPost(() =>
            {
                entMan.SpawnEntity(ArtifactProtoId, new EntityCoordinates(shipGrid, new Vector2(2f, 2f)));
            });

            await pair.RunTicksSync(5);

            var nodesBefore = 0;
            await server.WaitAssertion(() =>
            {
                var artifact = FindChildWithComponentSync<XenoArtifactComponent>(entMan, shipGrid);
                Assert.That(artifact, Is.Not.Null);
                nodesBefore = entMan.GetComponent<XenoArtifactComponent>(artifact!.Value).NodeVertices.Count(v => v != null);
                Assert.That(nodesBefore, Is.GreaterThan(0), "The control: generation on map init has to have produced a graph to lose.");
            });

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success),
                "A ship carrying an artifact must store. SerializeFailed here means the probe blanked a NetEntity field and the writer refused the null.");

            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            await pair.RunTicksSync(5);

            var retrievedArtifact = await FindChildWithComponent<XenoArtifactComponent>(pair, retrieved.Grid!.Value);

            await server.WaitAssertion(() =>
            {
                Assert.That(retrievedArtifact, Is.Not.Null, "The artifact came back with the ship.");
                var comp = entMan.GetComponent<XenoArtifactComponent>(retrievedArtifact!.Value);

                Assert.That(comp.NodeVertices, Is.Not.Null);
                var resolved = comp.NodeVertices.Count(v => v != null && entMan.TryGetEntity(v.Value, out var node) && entMan.HasComponent<XenoArtifactNodeComponent>(node.Value));
                Assert.That(resolved, Is.EqualTo(nodesBefore),
                    "Every vertex has to be remapped to the reborn node entity; a stripped graph comes back empty and a stale one points at nothing.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Every pump, filter and mixer switches itself off when it leaves an atmosphere, and the
        /// engine raises a parent-changed message on every entity at startup that made the atmos
        /// device leave and rejoin the grid it had already joined on init, so a loaded ship came back
        /// with its whole distro off. The atmos device system now skips the rejoin for a device
        /// already in the atmosphere it sits in; this proves the switches hold, and it failed on all
        /// four before that change.
        /// </summary>
        [Test]
        public async Task AtmosSwitchesStayOnThroughTheRoundTrip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            await server.WaitPost(() =>
            {
                var pump = entMan.SpawnEntity(PressurePumpProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f)));
                var volumePump = entMan.SpawnEntity(VolumePumpProtoId, new EntityCoordinates(shipGrid, new Vector2(1.5f, 0.5f)));
                var filter = entMan.SpawnEntity(FilterProtoId, new EntityCoordinates(shipGrid, new Vector2(2.5f, 0.5f)));
                var mixer = entMan.SpawnEntity(MixerProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 2.5f)));

                // The switches are access-locked to their systems, whose only setters are UI
                // message handlers; the test throws them by hand.
#pragma warning disable RA0002
                entMan.GetComponent<GasPressurePumpComponent>(pump).Enabled = true;
                entMan.GetComponent<GasVolumePumpComponent>(volumePump).Enabled = true;
                entMan.GetComponent<GasFilterComponent>(filter).Enabled = true;
                entMan.GetComponent<GasMixerComponent>(mixer).Enabled = true;
#pragma warning restore RA0002
            });

            await pair.RunTicksSync(10);

            await server.WaitAssertion(() => AssertAtmosSwitches(entMan, shipGrid, "Control, before the store"));

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(10);

            await server.WaitAssertion(() => AssertAtmosSwitches(entMan, retrieved.Grid!.Value, "After the retrieve"));

            await pair.CleanReturnAsync();
        }

        private static void AssertAtmosSwitches(IEntityManager entMan, EntityUid grid, string when)
        {
            var pump = FindChildWithComponentSync<GasPressurePumpComponent>(entMan, grid);
            var volumePump = FindChildWithComponentSync<GasVolumePumpComponent>(entMan, grid);
            var filter = FindChildWithComponentSync<GasFilterComponent>(entMan, grid);
            var mixer = FindChildWithComponentSync<GasMixerComponent>(entMan, grid);
            Assert.Multiple(() =>
            {
                Assert.That(pump, Is.Not.Null, $"{when}: the pressure pump is aboard.");
                Assert.That(volumePump, Is.Not.Null, $"{when}: the volume pump is aboard.");
                Assert.That(filter, Is.Not.Null, $"{when}: the filter is aboard.");
                Assert.That(mixer, Is.Not.Null, $"{when}: the mixer is aboard.");
            });
            Assert.Multiple(() =>
            {
                Assert.That(entMan.GetComponent<GasPressurePumpComponent>(pump!.Value).Enabled, Is.True, $"{when}: the pressure pump is on.");
                Assert.That(entMan.GetComponent<GasVolumePumpComponent>(volumePump!.Value).Enabled, Is.True, $"{when}: the volume pump is on.");
                Assert.That(entMan.GetComponent<GasFilterComponent>(filter!.Value).Enabled, Is.True, $"{when}: the filter is on.");
                Assert.That(entMan.GetComponent<GasMixerComponent>(mixer!.Value).Enabled, Is.True, $"{when}: the mixer is on.");
            });
        }

        /// <summary>
        /// The analysis console holds its analyzer as a NetEntity and the analyzer holds its console
        /// as a view-variables field, so the pair is re-resolved from the device-link wire on the
        /// analyzer's map init and nowhere else, so a retrieved pair comes back linked on the wire
        /// and dead on the console. Both ends are asserted after the round trip.
        /// </summary>
        [Test]
        public async Task AnAnalysisConsoleStaysLinkedToItsAnalyzer()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var deviceLink = server.System<DeviceLinkSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            await server.WaitPost(() =>
            {
                var console = entMan.SpawnEntity(AnalysisConsoleProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f)));
                var analyzer = entMan.SpawnEntity(ArtifactAnalyzerProtoId, new EntityCoordinates(shipGrid, new Vector2(1.5f, 0.5f)));
                deviceLink.LinkDefaults(null, console, analyzer);
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() => AssertAnalyzerLinked(entMan, shipGrid, "Control, before the store"));

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() => AssertAnalyzerLinked(entMan, retrieved.Grid!.Value, "After the retrieve"));

            await pair.CleanReturnAsync();
        }

        private static void AssertAnalyzerLinked(IEntityManager entMan, EntityUid grid, string when)
        {
            var console = FindChildWithComponentSync<AnalysisConsoleComponent>(entMan, grid);
            var analyzer = FindChildWithComponentSync<ArtifactAnalyzerComponent>(entMan, grid);
            Assert.That(console, Is.Not.Null, $"{when}: the console is aboard.");
            Assert.That(analyzer, Is.Not.Null, $"{when}: the analyzer is aboard.");

            var consoleComp = entMan.GetComponent<AnalysisConsoleComponent>(console!.Value);
            var analyzerComp = entMan.GetComponent<ArtifactAnalyzerComponent>(analyzer!.Value);
            Assert.Multiple(() =>
            {
                Assert.That(entMan.GetEntity(consoleComp.AnalyzerEntity), Is.EqualTo(analyzer.Value), $"{when}: the console names the analyzer.");
                Assert.That(analyzerComp.Console, Is.EqualTo(console.Value), $"{when}: the analyzer names the console.");
            });
        }

        /// <summary>
        /// A use delay's end is an absolute game time, and both ends carry
        /// <c>TimeOffsetSerializer</c>, so the document holds the remaining span rather than the
        /// clock reading: a delay filed with six minutes to run is read back with six minutes to
        /// run, whatever the clock did in between. Retrieve used to throw that away and re-arm every
        /// delay to a full length instead, which handed a player back more cooldown than they had.
        /// Nothing re-arms now, so the fraction is what this asserts.
        ///
        /// <para>Two failures sit on either side of that assertion, and the bounds are shaped to
        /// name both. A re-arm pushes the end out to a full length from now, which the upper bound
        /// catches. So does an additive shift: the thaw raises
        /// <c>EntityUnpausedEvent</c> per entity with the real storage duration, and
        /// <c>UseDelaySystem.OnUnpaused</c> answers it by adding that duration to every end. On a
        /// field the serializer has already re-based that correction is applied twice, and the ship
        /// comes back with its cooldowns pushed out by however long it sat in the drydock. The store
        /// and retrieve are separated by a real span of game time here precisely so that a double
        /// correction is larger than the tolerance rather than lost inside it.</para>
        /// </summary>
        [Test]
        public async Task AUseDelayKeepsItsRemainingTimeAcrossARoundTrip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var useDelay = server.System<UseDelaySystem>();
            var timing = server.ResolveDependency<IGameTiming>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            // A long delay with a known fraction still to run. Long enough that a re-arm to a full
            // length and a shift by the storage span are both far outside the tolerance below, and
            // far enough from zero that the delay cannot simply expire while the test runs.
            var length = TimeSpan.FromMinutes(10);
            var planted = TimeSpan.FromMinutes(6);

            await server.WaitPost(() =>
            {
                var item = entMan.SpawnEntity(MarketItemProtoId, new EntityCoordinates(shipGrid, new Vector2(1.5f, 1.5f)));
                useDelay.SetLength(item, length);
                var comp = entMan.GetComponent<UseDelayComponent>(item);
                // The component is access-locked to its system; the fraction has to be planted by hand.
#pragma warning disable RA0002
                var entry = comp.Delays.Values.Single();
                entry.StartTime = timing.CurTime - (length - planted);
                entry.EndTime = timing.CurTime + planted;
#pragma warning restore RA0002
            });

            await pair.RunTicksSync(5);

            var beforeStore = TimeSpan.Zero;
            await server.WaitAssertion(() =>
            {
                var item = FindChildWithComponentSync<UseDelayComponent>(entMan, shipGrid);
                Assert.That(item, Is.Not.Null);
                Assert.That(useDelay.IsDelayed(item!.Value), Is.True, "The control: the planted end reads as an active delay.");
                beforeStore = timing.CurTime;
            });

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            // A real span in storage. Everything this test is trying to distinguish between agrees
            // when the ship is filed and called back inside the same second, so the gap is the
            // measurement rather than a settle.
            await pair.RunTicksSync(200);

            var stored = TimeSpan.Zero;
            await server.WaitPost(() => stored = timing.CurTime);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            // Tick granularity, the ticks the retrieve itself burns, and the settle above. Small
            // against the six-minute fraction and against every failure the bounds are aimed at.
            var tolerance = TimeSpan.FromSeconds(2);

            await server.WaitAssertion(() =>
            {
                var elapsed = timing.CurTime - beforeStore;
                Assert.That(stored - beforeStore, Is.GreaterThan(tolerance * 2),
                    "The control on the gap: with no measurable time in storage a doubled unpause correction would hide inside the tolerance.");

                var item = FindChildWithComponentSync<UseDelayComponent>(entMan, retrieved.Grid!.Value);
                Assert.That(item, Is.Not.Null, "The item came back with the ship.");
#pragma warning disable RA0002
                var entry = entMan.GetComponent<UseDelayComponent>(item!.Value).Delays.Values.Single();
#pragma warning restore RA0002
                var remaining = entry.EndTime - timing.CurTime;

                Assert.Multiple(() =>
                {
                    Assert.That(remaining, Is.LessThanOrEqualTo(planted + tolerance),
                        $"A retrieved delay must not come back with more time to run than it went in with ({remaining} against {planted}). "
                        + "More means either a re-arm to a full length, or the storage duration added to an end the serializer had already re-based.");

                    Assert.That(remaining, Is.GreaterThanOrEqualTo(planted - elapsed - tolerance),
                        $"A retrieved delay must not lose more than the game time that actually passed ({remaining} against {planted} less {elapsed}).");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The ship-save path deletes anything marked as saving contraband unless it carries a
        /// contraband permit; the drydock kept everything, so ID cards and grenades rode along. The
        /// store purges by the same component rule now, and a permit only counts when it is the
        /// owner's: stored with nobody at a console, which is an impound or the round-end sweep, a
        /// permit travels when its mind belongs to the owning account. A stranger's permitted kit
        /// left aboard is purged rather than re-stamped to the owner on the next retrieve. The
        /// owner's permit and an ordinary item are the controls that the purge takes only what it
        /// should.
        /// </summary>
        [Test]
        public async Task SavingContrabandIsPurgedAtStoreUnlessPermitted()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var minds = server.System<MindSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            await server.WaitPost(() =>
            {
                var contraband = entMan.SpawnEntity(MarketItemProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f)));
                entMan.EnsureComponent<SavingContrabandComponent>(contraband);

                var ownersPermit = SpawnPermitted(entMan, new EntityCoordinates(shipGrid, new Vector2(1.5f, 0.5f)), MindOfAccount(minds, owner));
                entMan.EnsureComponent<SavingContrabandComponent>(ownersPermit);

                var strangersPermit = SpawnPermitted(entMan, new EntityCoordinates(shipGrid, new Vector2(2.5f, 0.5f)), MindOfAccount(minds, Guid.NewGuid()));
                entMan.EnsureComponent<SavingContrabandComponent>(strangersPermit);

                // Second row: the fixture lays only a 3x3 floor, and a sheet spawned off it is
                // reparented to the map and never reaches the store at all.
                entMan.SpawnEntity(MarketItemProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 1.5f)));
            });

            await pair.RunTicksSync(5);

            var before = await CensusGrid(pair, shipGrid);
            Assert.That(before[MarketItemProtoId], Is.EqualTo(4), "The control: four sheets aboard before the store.");

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            var after = await CensusGrid(pair, retrieved.Grid!.Value);
            Assert.That(after.GetValueOrDefault(MarketItemProtoId), Is.EqualTo(2),
                "The unpermitted contraband and the stranger's permitted one are gone; the owner's permitted one and the plain sheet are not.");

            await server.WaitAssertion(() =>
            {
                var query = entMan.AllEntityQueryEnumerator<SavingContrabandComponent, TransformComponent>();
                while (query.MoveNext(out var uid, out _, out var xform))
                {
                    if (xform.GridUid != retrieved.Grid!.Value)
                        continue;

                    Assert.That(entMan.HasComponent<ContrabandPermitItemComponent>(uid), Is.True,
                        "Every piece of saving contraband that came back carries a permit.");
                }
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A store made at a console has someone standing at it, and then a permit travels only when
        /// it was issued to that person's mind: the ship-save path's ClearPermitItemsOnGrid rule,
        /// reported missing from the drydock when a player stored a hull carrying another character's
        /// permitted kit. The foreign permit sits on an item that is NOT marked as saving contraband,
        /// so nothing but the permit rule can take it; the holder's own permit sits on a marked one, so
        /// its survival is the permit counting rather than the item being harmless.
        /// </summary>
        [Test]
        public async Task APermitTravelsOnlyWithTheMindThatStoresTheShip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var minds = server.System<MindSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            var holder = EntityUid.Invalid;
            await server.WaitPost(() =>
            {
                // Both minds belong to the owning account, so the account rule would keep both
                // permits: only the mind rule can tell them apart.
                holder = MindOfAccount(minds, owner);
                var otherCharacter = MindOfAccount(minds, owner);

                var held = SpawnPermitted(entMan, new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f)), holder);
                entMan.EnsureComponent<SavingContrabandComponent>(held);

                SpawnPermitted(entMan, new EntityCoordinates(shipGrid, new Vector2(1.5f, 0.5f)), otherCharacter);

                entMan.SpawnEntity(MarketItemProtoId, new EntityCoordinates(shipGrid, new Vector2(2.5f, 0.5f)));
            });

            await pair.RunTicksSync(5);

            var before = await CensusGrid(pair, shipGrid);
            Assert.That(before[MarketItemProtoId], Is.EqualTo(3), "The control: three sheets aboard before the store.");

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair,
                () => drydock.TryStoreShip(shipGrid, owner, null, permitHolderMind: holder));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            var after = await CensusGrid(pair, retrieved.Grid!.Value);
            Assert.That(after.GetValueOrDefault(MarketItemProtoId), Is.EqualTo(2),
                "The other character's permitted item is gone; the holder's and the plain sheet are not.");

            await server.WaitAssertion(() =>
            {
                var marked = 0;
                var unmarked = 0;
                var query = entMan.AllEntityQueryEnumerator<ContrabandPermitItemComponent, TransformComponent>();
                while (query.MoveNext(out var uid, out _, out var xform))
                {
                    if (xform.GridUid != retrieved.Grid!.Value)
                        continue;

                    if (entMan.HasComponent<SavingContrabandComponent>(uid))
                        marked++;
                    else
                        unmarked++;
                }

                Assert.That(marked, Is.EqualTo(1), "The holder's own permitted contraband came back.");
                Assert.That(unmarked, Is.Zero,
                    "The other character's permit was on an unmarked item, so only the permit rule could have taken it, and it did.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Permits follow the person, never the ship. When a ship arrives in someone's hands, by a
        /// retrieve or an import, a permit aboard is claimed only if it was issued to the character
        /// taking it, and anything else is seized with its item, so a transfer, a sale or a shared ship
        /// file cannot hand a permit on. Five permits cover the edges: the holder's own; one issued
        /// before the account was saved, recognised by name; another character's; a same-named
        /// character on another account; and the holder's own inside a container, which a lookup over
        /// the grid's bounds could walk past and leave unclaimed for the next store to purge.
        /// </summary>
        [Test]
        public async Task APermitIsClaimedOnlyByTheCharacterItWasIssuedTo()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var minds = server.System<MindSystem>();
            var permits = server.System<ContrabandPermitSystem>();
            var containers = server.System<SharedContainerSystem>();

            var (_, shipGrid, _) = await BuildShipAndStation(pair);

            var aliceAccount = new NetUserId(Guid.NewGuid());
            EntityUid retriever = default, aliceMind = default;
            EntityUid held = default, legacy = default, otherCharacter = default, otherAccount = default, contained = default;

            await server.WaitPost(() =>
            {
                retriever = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
                var mind = minds.CreateMind(null, "Alice");
#pragma warning disable RA0002
                mind.Comp.OriginalOwnerUserId = aliceAccount;
#pragma warning restore RA0002
                minds.TransferTo(mind, retriever);
                aliceMind = mind.Owner;

                EntityUid Issued(Vector2 at, string name, NetUserId? account)
                {
                    var item = entMan.SpawnEntity(MarketItemProtoId, new EntityCoordinates(shipGrid, at));
                    var permit = entMan.EnsureComponent<ContrabandPermitItemComponent>(item);
                    permit.PermitOwnerName = name;
                    permit.PermitOwnerAccount = account;
                    return item;
                }

                held = Issued(new Vector2(0.5f, 0.5f), "Alice", aliceAccount);
                legacy = Issued(new Vector2(1.5f, 0.5f), "Alice", null);
                otherCharacter = Issued(new Vector2(2.5f, 0.5f), "Bob", null);
                otherAccount = Issued(new Vector2(0.5f, 1.5f), "Alice", new NetUserId(Guid.NewGuid()));

                var box = entMan.SpawnEntity(MarketItemProtoId, new EntityCoordinates(shipGrid, new Vector2(1.5f, 1.5f)));
                contained = Issued(new Vector2(1.5f, 1.5f), "Alice", aliceAccount);
                containers.Insert(contained, containers.EnsureContainer<Container>(box, "permit-test"));
            });

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(containers.IsEntityInContainer(contained), Is.True, "The control: the holder's second permit really is inside a container.");
                Assert.That(entMan.GetComponent<TransformComponent>(contained).GridUid, Is.EqualTo(shipGrid),
                    "The control: and still aboard the ship, just not loose on its deck.");
            });

            await server.WaitPost(() => permits.InitializePermitItemsOnGrid(shipGrid, retriever));
            await pair.RunTicksSync(1);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(entMan.Deleted(otherCharacter), Is.True, "Another character's permit is seized with its item.");
                    Assert.That(entMan.Deleted(otherAccount), Is.True, "A character with the same name on another account is someone else.");

                    Assert.That(entMan.TryGetComponent<ContrabandPermitItemComponent>(held, out var heldPermit), Is.True, "The holder's own permit is claimed.");
                    Assert.That(heldPermit?.PermitOwnerMind, Is.EqualTo(aliceMind), "Claimed means stamped with the holder's mind, which the next store judges by.");
                    Assert.That(heldPermit?.PermitOwner, Is.EqualTo(retriever));

                    Assert.That(entMan.TryGetComponent<ContrabandPermitItemComponent>(legacy, out var legacyPermit), Is.True,
                        "A permit issued before the account was saved goes by the character's name.");
                    Assert.That(legacyPermit?.PermitOwnerAccount, Is.EqualTo(aliceAccount), "And it picks the account up on the way, so it is judged by both from now on.");

                    Assert.That(entMan.TryGetComponent<ContrabandPermitItemComponent>(contained, out var containedPermit), Is.True, "A permit inside a container is judged too.");
                    Assert.That(containedPermit?.PermitOwnerMind, Is.EqualTo(aliceMind),
                        "And claimed, or the next store would purge it from its own holder's ship for having no mind.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A permittable item carrying a permit issued to <paramref name="mind"/>, the way the permit
        /// console stamps one.
        /// </summary>
        private static EntityUid SpawnPermitted(IEntityManager entMan, EntityCoordinates at, EntityUid mind)
        {
            var item = entMan.SpawnEntity(MarketItemProtoId, at);
            entMan.EnsureComponent<ContrabandPermittableComponent>(item);
            entMan.EnsureComponent<ContrabandPermitItemComponent>(item).PermitOwnerMind = mind;
            return item;
        }

        /// <summary>
        /// A mind standing for a character of <paramref name="account"/>. Created with no user, since
        /// the mind system refuses a user id with no player data behind it and logs an error doing so;
        /// the original owner is written directly instead, which is the field the permit rule reads.
        /// </summary>
        private static EntityUid MindOfAccount(MindSystem minds, Guid account)
        {
            var mind = minds.CreateMind(null);
#pragma warning disable RA0002
            mind.Comp.OriginalOwnerUserId = new NetUserId(account);
#pragma warning restore RA0002
            return mind.Owner;
        }

        /// <summary>
        /// A crystallizer's recipe and gas input were view-variables fields, so a retrieved one came
        /// back reset and the regulator loop then ran against it, superheating the inlet. Both are
        /// data fields now.
        /// </summary>
        [Test]
        public async Task ACrystallizerKeepsItsSettings()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            await server.WaitPost(() =>
            {
                var crystallizer = entMan.SpawnEntity(CrystallizerProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f)));
                var comp = entMan.GetComponent<CrystallizerComponent>(crystallizer);
                comp.SelectedRecipeId = "roundtrip-recipe";
                comp.GasInput = 12.5f;
            });

            await pair.RunTicksSync(5);

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var crystallizer = FindChildWithComponentSync<CrystallizerComponent>(entMan, retrieved.Grid!.Value);
                Assert.That(crystallizer, Is.Not.Null, "The crystallizer came back with the ship.");
                var comp = entMan.GetComponent<CrystallizerComponent>(crystallizer!.Value);
                Assert.Multiple(() =>
                {
                    Assert.That(comp.SelectedRecipeId, Is.EqualTo("roundtrip-recipe"));
                    Assert.That(comp.GasInput, Is.EqualTo(12.5f));
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Appearance data written by the game, rather than seeded by a prototype, survives a round
        /// trip. No save has ever carried it: the component declares one read-only data field for
        /// the prototype seed and keeps everything else in a dictionary the serializer never sees,
        /// which is why a retrieved lathe froze mid-animation and a retrieved tray showed no dead
        /// plant while still holding one.
        /// </summary>
        /// <remarks>
        /// The two keys are deliberately ones nothing aboard this ship reads. A key some system
        /// re-derives on startup would pass whether the carrier worked or not, and the carrier is
        /// the only thing under test here. Two different enum types with two different value types,
        /// because the sidecar has to resolve both halves of each entry by name on the way back.
        /// </remarks>
        [Test]
        public async Task AppearanceSetOutsideMapInitSurvivesTheRoundTrip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            await server.WaitPost(() =>
            {
                var machine = entMan.SpawnEntity(CrystallizerProtoId, new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f)));
                entMan.EnsureComponent<AppearanceComponent>(machine);

                var appearance = server.System<SharedAppearanceSystem>();
                appearance.SetData(machine, LatheVisuals.IsRunning, true);
                appearance.SetData(machine, StorageVisuals.StorageUsed, 7);
            });

            await pair.RunTicksSync(5);

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var machine = FindChildWithComponentSync<CrystallizerComponent>(entMan, retrieved.Grid!.Value);
                Assert.That(machine, Is.Not.Null, "The machine came back with the ship.");

                var appearance = server.System<SharedAppearanceSystem>();
                Assert.Multiple(() =>
                {
                    Assert.That(
                        appearance.TryGetData<bool>(machine!.Value, LatheVisuals.IsRunning, out var running) && running,
                        Is.True,
                        "A boolean appearance value came back as it was set.");

                    Assert.That(
                        appearance.TryGetData<int>(machine!.Value, StorageVisuals.StorageUsed, out var used) ? used : -1,
                        Is.EqualTo(7),
                        "An integer appearance value under a second key type came back as it was set.");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The round trip compared against itself, rather than one machine at a time.
        ///
        /// <para>Every other test on this page asserts one thing somebody found broken by hand. This
        /// one snapshots the whole grid before the store and after the retrieve and asserts nothing
        /// moved, which is the only shape that can catch a gap nobody has thought of yet. When it
        /// fails it prints what changed, and each line is either a bug or a difference that belongs
        /// in the expected list below with a reason.</para>
        /// </summary>
        [Test]
        public async Task NothingOnTheShipChangesAcrossTheRoundTrip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            // A spread wide enough to cover every machine class that loses state: atmos devices that
            // switch, a lathe, a fridge, a research server, a turret and a crystallizer.
            await server.WaitPost(() =>
            {
                // The shared builder lays a three-by-three hull, not enough floor for that spread.
                // It has to be FLOOR rather than open space: an entity spawned off the tiles is
                // re-parented to the map instead of the ship, so it is never stored and the walk
                // below never sees it.
                var mapSys = server.System<SharedMapSystem>();
                var gridComp = entMan.GetComponent<MapGridComponent>(shipGrid);
                for (var x = 0; x < 6; x++)
                {
                    for (var y = 0; y < 3; y++)
                    {
                        mapSys.SetTile(shipGrid, gridComp, new Vector2i(x, y), new Tile(1));
                    }
                }

                // The research server goes down first on purpose. A lathe registers with whatever
                // server is already aboard when it initialises, so spawning the server after it
                // leaves the lathe's technology database empty before the store and filled after the
                // retrieve, where the revive step registers it against a server that exists by then.
                // That is this test's own doing rather than the drydock's, and it showed up as two
                // diff lines until the order changed.
                var protos = new[]
                {
                    ResearchServerProtoId,
                    PressurePumpProtoId, VolumePumpProtoId, FilterProtoId, MixerProtoId,
                    LatheProtoId, SmartFridgeProtoId, CrystallizerProtoId, TurretProtoId,
                    ApcProtoId,
                };

                // Two rows, leaving the middle one to the builder's airlock. One machine per tile,
                // because an anchored machine snaps to its tile centre and two on a tile would share
                // a path and be dropped as ambiguous.
                for (var i = 0; i < protos.Length; i++)
                {
                    var coords = new Vector2(i % 5 + 0.5f, i < 5 ? 0.5f : 2.5f);
                    entMan.SpawnEntity(protos[i], new EntityCoordinates(shipGrid, coords));
                }
            });

            await pair.RunTicksSync(10);

            DrydockStateSnapshot before = default!;
            await server.WaitPost(() => before = fidelity.SnapshotGrid(shipGrid));

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(10);

            DrydockStateSnapshot after = default!;
            await server.WaitPost(() => after = fidelity.SnapshotGrid(retrieved.Grid!.Value));

            // Controls first. A snapshot that covered nothing would compare clean and prove nothing,
            // which is the failure the serializability audit already learned to guard against.
            Assert.Multiple(() =>
            {
                var covered = string.Join(", ", before.Values.Keys.Select(k => k[..k.IndexOf('|')]).Distinct());
                Assert.That(before.Entities, Is.GreaterThan(10),
                    $"The snapshot covered the ship rather than a corner of it. Ambiguous: {before.Ambiguous}. Covered: {covered}");
                Assert.That(before.Values, Is.Not.Empty, "The snapshot rendered fields rather than nothing.");

                // A floor on both sides rather than equality between them. An entity whose prototype
                // declares save: false is aboard at the store and legitimately absent afterwards, so
                // the counts can differ by design and an equality here is a flake waiting to happen.
                // A machine that really went missing shows up as GONE lines in the diff below, which
                // is where that belongs.
                Assert.That(after.Entities, Is.GreaterThan(10),
                    $"The ship came back. Ambiguous paths: {before.Ambiguous} before, {after.Ambiguous} after.");
            });

            var diff = DrydockStateSnapshot.Diff(before, after).Where(DrydockRoundTripExpectations.IsUnexpected).ToList();

            Assert.That(diff, Is.Empty,
                "The round trip changed state nothing intends it to change:\n  " + string.Join("\n  ", diff));

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A generator stored running comes back running. The on flag is a data field and was always
        /// in the document; what lost it was the load. The transform system raises
        /// AnchorStateChangedEvent on every entity that starts up anchored, and the generator's
        /// handler switched off on any anchor change rather than only on coming unanchored, so the
        /// flag arrived true and was false one event later. The revive step then saw a generator
        /// that was off and left it alone. Seventy-three of them across the roster, and a hull that
        /// docks with every light out.
        ///
        /// <para>The shuttle variant starts itself on map init, which is the control here: if it is
        /// not running before the store, the test is asking the wrong question. Ticks after the
        /// retrieve are what let the generator loop and the anchor event do their worst.</para>
        /// </summary>
        [Test]
        public async Task ARunningGeneratorComesBackRunning()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            EntityUid generator = default;
            await server.WaitPost(() =>
            {
                generator = entMan.SpawnEntity(ShuttleGeneratorProtoId, new EntityCoordinates(shipGrid, new Vector2(1.5f, 1.5f)));
            });
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var xform = entMan.GetComponent<TransformComponent>(generator);
                Assert.Multiple(() =>
                {
                    Assert.That(xform.GridUid, Is.EqualTo(shipGrid), "Control: it landed on the hull rather than the map.");
                    Assert.That(xform.Anchored, Is.True, "Control: anchored, which is what a generator needs to start at all.");
                    Assert.That(entMan.GetComponent<FuelGeneratorComponent>(generator).On, Is.True,
                        "Control: the shuttle variant starts itself on map init, so it is running before the store.");
                    Assert.That(entMan.GetComponent<PowerSupplierComponent>(generator).Enabled, Is.True,
                        "Control: the generator loop has run and enabled the supplier.");
                });
            });

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            var grid = retrieved.Grid!.Value;

            // Enough for the anchor event at startup and a good many generator-loop passes, which are
            // the two things that could switch it off again.
            await pair.RunTicksSync(30);

            await server.WaitAssertion(() =>
            {
                var back = ChildrenWith<FuelGeneratorComponent>(entMan, grid).ToList();
                Assert.That(back, Has.Count.EqualTo(1), "The generator came back, once.");

                Assert.Multiple(() =>
                {
                    Assert.That(entMan.GetComponent<TransformComponent>(back[0]).Anchored, Is.True, "It came back anchored.");
                    Assert.That(entMan.GetComponent<FuelGeneratorComponent>(back[0]).On, Is.True,
                        "The generator is still switched on after the load, so its own anchoring did not switch it off.");
                    Assert.That(entMan.GetComponent<PowerSupplierComponent>(back[0]).Enabled, Is.True,
                        "And it is supplying, so the ship did not come back dark.");
                });
            });

            await pair.CleanReturnAsync();
        }

        private static EntityUid? FindChildWithComponentSync<T>(IEntityManager entMan, EntityUid grid) where T : IComponent
        {
            var query = entMan.AllEntityQueryEnumerator<T, TransformComponent>();
            while (query.MoveNext(out var uid, out _, out var xform))
            {
                if (xform.GridUid == grid)
                    return uid;
            }

            return null;
        }

        /// <summary>
        /// The last of the two sidecars, and the only piece of state here that does not live on an
        /// entity at all. A pipe net's air hangs off the node-group object graph, which the map
        /// serializer never visits, so it is not a serialization failure to detect: it is state
        /// attached to a structure that gets rebuilt from scratch on load. Without the sidecar a
        /// stored ship comes back with every pipe empty.
        ///
        /// <para>The restore is the odd one out too. It does not run in the Revive block; it waits
        /// for the reloaded grid's first node-group rebuild, because that is when there is a net to
        /// merge into. The sidecar's presence is the whole apply condition, and it removes itself
        /// immediately so that a player cutting a pipe later cannot re-fire the merge and duplicate
        /// the gas.</para>
        /// </summary>
        [Test]
        public async Task PipeNetGasSurvivesTheRoundTrip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();
            var xformSys = server.System<SharedTransformSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            await server.WaitPost(() =>
            {
                // Two adjacent pipes so there is a net rather than an isolated node. A pipe only
                // joins a net while anchored, and this prototype already spawns anchored onto a set
                // tile: anchoring it again trips a debug assert in the engine, because the entity
                // is already in that snap-grid cell.
                foreach (var pos in new[] { new Vector2(0.5f, 1.5f), new Vector2(1.5f, 1.5f) })
                {
                    var pipe = entMan.SpawnEntity(PipeProtoId, new EntityCoordinates(shipGrid, pos));
                    var xform = entMan.GetComponent<TransformComponent>(pipe);

                    if (!xform.Anchored)
                        xformSys.AnchorEntity(pipe);
                }
            });

            // Node groups are rebuilt on a deferred pass, so the net does not exist on the tick the
            // pipes were anchored.
            await pair.RunTicksSync(10);

            await server.WaitPost(() =>
            {
                foreach (var pipe in PipeNodesOn(entMan, shipGrid))
                    pipe.Air.AdjustMoles(Gas.Oxygen, 25f);
            });

            await pair.RunTicksSync(5);

            var molesBefore = await TotalPipeMoles(pair, shipGrid);
            Assert.That(molesBefore, Is.GreaterThan(0f),
                "The control: the pipes have to actually hold gas and be in a net, or nothing below is measuring the sidecar.");

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            // The merge waits for the first node-group rebuild after the load, which is later than
            // everything Revive does synchronously.
            await pair.RunTicksSync(15);

            var molesAfter = await TotalPipeMoles(pair, retrieved.Grid!.Value);

            Assert.That(molesAfter, Is.EqualTo(molesBefore).Within(0.01f),
                "Pipe gas is not on any entity, so this passes only because the sidecar carried each pipe's share and the rebuild merged it back.");

            await server.WaitAssertion(() =>
            {
                var query = entMan.AllEntityQueryEnumerator<DrydockPipeGasComponent>();
                Assert.That(query.MoveNext(out _, out _), Is.False,
                    "The sidecar removes itself on the merge. One left behind would re-merge on the next pipe a player cuts, which duplicates the gas.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A pump has an inlet node in one net and an outlet node in another. The first sidecar held
        /// one mixture per entity, so whichever net was written last won and the restore merged it
        /// into both nodes: gas crossed the pump, a mixer's two feeds leaked into each other, and a
        /// crystallizer's inlet dumped into its regulator loop. A lone
        /// pump is the smallest device with two nets; its two nodes must come back holding exactly
        /// what each held, and nothing of the other.
        /// </summary>
        [Test]
        public async Task ATwoPortDeviceKeepsEachNetsGasSeparate()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            await server.WaitPost(() => entMan.SpawnEntity(PressurePumpProtoId, new EntityCoordinates(shipGrid, new Vector2(1.5f, 1.5f))));
            await pair.RunTicksSync(10);

            await server.WaitPost(() =>
            {
                var (inlet, outlet) = PumpNodes(entMan, shipGrid);
                inlet.Air.AdjustMoles(Gas.Oxygen, 30f);
                outlet.Air.AdjustMoles(Gas.Nitrogen, 20f);
            });
            await pair.RunTicksSync(5);

            await server.WaitAssertion(() => AssertPumpGases(entMan, shipGrid, "Control, before the store"));

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(15);

            await server.WaitAssertion(() => AssertPumpGases(entMan, retrieved.Grid!.Value, "After the retrieve"));

            await pair.CleanReturnAsync();
        }

        private static (PipeNode Inlet, PipeNode Outlet) PumpNodes(IEntityManager entMan, EntityUid grid)
        {
            var pump = FindChildWithComponentSync<GasPressurePumpComponent>(entMan, grid);
            Assert.That(pump, Is.Not.Null, "The pump is aboard.");
            var comp = entMan.GetComponent<GasPressurePumpComponent>(pump!.Value);
            var nodes = entMan.GetComponent<NodeContainerComponent>(pump.Value).Nodes;
            return ((PipeNode)nodes[comp.InletName], (PipeNode)nodes[comp.OutletName]);
        }

        private static void AssertPumpGases(IEntityManager entMan, EntityUid grid, string when)
        {
            var (inlet, outlet) = PumpNodes(entMan, grid);
            Assert.Multiple(() =>
            {
                Assert.That(inlet.Air.GetMoles(Gas.Oxygen), Is.EqualTo(30f).Within(0.01f), $"{when}: the inlet holds its oxygen.");
                Assert.That(inlet.Air.GetMoles(Gas.Nitrogen), Is.EqualTo(0f).Within(0.01f), $"{when}: none of the outlet's nitrogen crossed into the inlet.");
                Assert.That(outlet.Air.GetMoles(Gas.Nitrogen), Is.EqualTo(20f).Within(0.01f), $"{when}: the outlet holds its nitrogen.");
                Assert.That(outlet.Air.GetMoles(Gas.Oxygen), Is.EqualTo(0f).Within(0.01f), $"{when}: none of the inlet's oxygen crossed into the outlet.");
            });
        }

        /// <summary>
        /// The second and last entry on the capture manifest, so between this and the lathe queue
        /// the whole manifest is now exercised rather than half of it.
        ///
        /// <para>Cargo market data is the grid's own record of what it sells, which is player-built
        /// state accumulated over a round rather than anything a prototype provides. It sits on the
        /// grid itself, so unlike the lathe it needs no machine aboard.</para>
        /// </summary>
        [Test]
        public async Task CargoMarketDataSurvivesTheRoundTrip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            await server.WaitPost(() =>
            {
                var market = entMan.EnsureComponent<CargoMarketDataComponent>(shipGrid);

                // The component is access-locked to the market system, and this test is neither.
                // Same precedent as the other integration tests that have to seed restricted state.
#pragma warning disable RA0002
                market.MarketDataList.Add(new MarketData(MarketItemProtoId, null, quantity: 7, price: 42.5));
#pragma warning restore RA0002
            });

            await pair.RunTicksSync(5);

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success));

            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.TryGetComponent<CargoMarketDataComponent>(retrieved.Grid!.Value, out var market), Is.True,
                    "The component rides the blob normally; it is the list inside it that needs carrying.");

#pragma warning disable RA0002
                var list = market!.MarketDataList;
#pragma warning restore RA0002

                Assert.That(list, Has.Count.EqualTo(1),
                    "MarketData has no serializer, so an empty list here means it was stripped rather than captured.");

                Assert.Multiple(() =>
                {
                    Assert.That(list[0].Prototype.Id, Is.EqualTo(MarketItemProtoId));
                    Assert.That(list[0].Quantity, Is.EqualTo(7));
                    Assert.That(list[0].Price, Is.EqualTo(42.5));
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The roster sweep's non-determinism, reproduced on demand. A sound played at grid
        /// coordinates is a real grid child until its despawn timer fires, but its prototype declares
        /// <c>save: false</c>, so the serializer never writes it: the validation counted it live,
        /// never saw it reload, and refused whichever ship had a sound in the air at that instant.
        ///
        /// <para>The sweep could only show the symptom, because whether a sound is aloft when the
        /// store runs is timing. This test plants one deliberately, which makes the refusal a
        /// certainty instead of a coin flip: before the validation learned the serializer's own
        /// exclusion, this failed every run.</para>
        /// </summary>
        [Test]
        public async Task ALiveSoundEffectDoesNotBlockTheStore()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await BuildShipAndStation(pair);

            EntityUid sound = default;
            await server.WaitPost(() =>
            {
                // What SharedAudioSystem.SetupAudio spawns, planted as a direct grid child the way
                // a sound played at grid coordinates lands. No despawn timer rides it, so unlike
                // the real thing it is guaranteed to still be there when the store serializes.
                sound = entMan.SpawnEntity(AudioProtoId, new EntityCoordinates(shipGrid, new Vector2(1.5f, 1.5f)));
            });

            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.GetComponent<MetaDataComponent>(sound).EntityPrototype?.MapSavable, Is.False,
                    "The control: if the Audio prototype ever stops declaring save: false, this test is planting an ordinary entity and proves nothing.");
                Assert.That(entMan.GetComponent<TransformComponent>(sound).ParentUid, Is.EqualTo(shipGrid),
                    "The control: the sound has to be a direct grid child, because that is the population the validation counts.");
            });

            var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null));
            Assert.That(result, Is.EqualTo(DrydockStoreResult.Success),
                "A sound in the air must not refuse the store. The serializer will not write it, and the validation has to count what the serializer writes, not what is live.");

            await pair.RunTicksSync(5);

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId!.Value, owner, station, null));
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success), "Stored with a sound aloft, then would not come back.");

            await pair.RunTicksSync(5);

            await server.WaitAssertion(() =>
            {
                var children = entMan.GetComponent<TransformComponent>(retrieved.Grid!.Value).ChildEnumerator;
                while (children.MoveNext(out var child))
                {
                    Assert.That(entMan.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID, Is.Not.EqualTo(AudioProtoId),
                        "The sound is ephemera and the serializer refuses it; one aboard the retrieved ship means it rode the document after all.");
                }
            });

            await pair.CleanReturnAsync();
        }

        private static IEnumerable<PipeNode> PipeNodesOn(IEntityManager entMan, EntityUid grid)
        {
            var query = entMan.AllEntityQueryEnumerator<NodeContainerComponent, TransformComponent>();
            while (query.MoveNext(out _, out var nodeContainer, out var xform))
            {
                if (xform.GridUid != grid)
                    continue;

                foreach (var node in nodeContainer.Nodes.Values)
                {
                    if (node is PipeNode pipe)
                        yield return pipe;
                }
            }
        }

        private static async Task<float> TotalPipeMoles(TestPair pair, EntityUid grid)
        {
            var total = 0f;
            await pair.Server.WaitPost(() =>
            {
                foreach (var pipe in PipeNodesOn(pair.Server.EntMan, grid))
                    total += pipe.Air.TotalMoles;
            });
            return total;
        }

        /// <summary>
        /// A three-by-three plated grid carrying one airlock, plus a station to dock it at. The
        /// tiles are laid before anything is spawned on them: a spawn at grid-local coordinates
        /// that are not on a set tile silently reparents to the map, and a grid census then reads
        /// the wrong parent.
        /// </summary>
        internal static async Task<(EntityUid Station, EntityUid ShipGrid, EntityUid Airlock)> BuildShipAndStation(TestPair pair)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var cfg = server.ResolveDependency<IConfigurationManager>();
            var shipyard = server.System<ShipyardSystem>();
            var stationSys = server.System<StationSystem>();
            var mapSys = server.System<SharedMapSystem>();

            var map = await pair.CreateTestMap();

            EntityUid station = default;
            EntityUid shipGrid = default;
            EntityUid airlock = default;

            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);

                // Slicing off, which for this cvar means no job and no queue at all rather than a
                // job with a zero budget: the pipeline runs on the caller's own async path, so the
                // only thing it ever waits for is the database. That is what keeps the tick pump in
                // DrydockTestHelpers.RunOnServer honest. Slicing has its own fixture, and a test that is about whether
                // a ship survives a round trip should not also be measuring the scheduler.
                cfg.SetCVar(TriadCCVars.DrydockTickBudgetMs, 0);

                // Not for the retrieve, which loads onto a private map of its own and no longer
                // refuses without this one. The shipyard's own paths still want a staged map, and
                // ARetrieveNeedsNoShipyardMap needs one here to delete.
                shipyard.SetupShipyardIfNeeded();

                // The dock target. As far as the retrieve gate is concerned a station is a
                // StationData component with a grid in it, which is what GetLargestGrid reads.
                station = entMan.Spawn();
                entMan.AddComponent<StationDataComponent>(station);
                stationSys.AddGridToStation(station, map.Grid.Owner);

                // The ship, on the same map but its own grid, so storing it cannot disturb the
                // dock target.
                var ship = mapSys.CreateGridEntity(map.MapId);
                shipGrid = ship.Owner;

                var tile = new Tile(1);
                for (var x = 0; x < 3; x++)
                {
                    for (var y = 0; y < 3; y++)
                    {
                        mapSys.SetTile(ship.Owner, ship.Comp, new Vector2i(x, y), tile);
                    }
                }

                entMan.EnsureComponent<ShuttleComponent>(shipGrid);
                entMan.System<MetaDataSystem>().SetEntityName(shipGrid, "Kestrel");

                airlock = entMan.SpawnEntity(AirlockProtoId, new EntityCoordinates(shipGrid, new Vector2(1f, 1f)));
            });

            // The station stands on the test grid, which the fork's janitors are built to delete.
            await pair.MakeCleanupImmune(map.Grid.Owner);

            await pair.RunTicksSync(5);

            return (station, shipGrid, airlock);
        }

        /// <summary>
        /// Every entity parented under the grid, counted per prototype. Recursive, because the
        /// interesting losses live inside containers rather than on the floor.
        /// </summary>
        private static async Task<Dictionary<string, int>> CensusGrid(TestPair pair, EntityUid grid)
        {
            var census = new Dictionary<string, int>();
            var entMan = pair.Server.EntMan;

            await pair.Server.WaitPost(() =>
            {
                var stack = new Stack<EntityUid>();
                stack.Push(grid);

                while (stack.Count > 0)
                {
                    var current = stack.Pop();

                    var children = entMan.GetComponent<TransformComponent>(current).ChildEnumerator;
                    while (children.MoveNext(out var child))
                    {
                        var proto = entMan.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID ?? "<no prototype>";
                        census[proto] = census.GetValueOrDefault(proto) + 1;
                        stack.Push(child);
                    }
                }
            });

            return census;
        }

        private static async Task<int> ReadWireCount(TestPair pair, EntityUid uid)
        {
            var count = -1;
            await pair.Server.WaitPost(() =>
            {
                count = pair.Server.EntMan.GetComponent<WiresComponent>(uid).WiresList.Count;
            });
            return count;
        }

        private static async Task<EntityUid?> FindChildWithComponent<T>(TestPair pair, EntityUid grid) where T : IComponent
        {
            EntityUid? found = null;
            var entMan = pair.Server.EntMan;

            await pair.Server.WaitPost(() =>
            {
                var children = entMan.GetComponent<TransformComponent>(grid).ChildEnumerator;
                while (children.MoveNext(out var child))
                {
                    if (!entMan.HasComponent<T>(child))
                        continue;

                    found = child;
                    return;
                }
            });

            return found;
        }
    }
}
