#nullable enable

using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Drydock;
using Content.Shared._Triad.ShipSize;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The round-end sweep, driven against a round of its own rather than a real round end, which
    /// the pooled pair never produces. The criterion is a piloting console and a dock; the verdicts
    /// are the row states and the timeline rows they leave; and the hold is what keeps the restart
    /// waiting while the sweep files, up to its ceiling and not past it.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockSweepTest
    {
        private const string ShuttleAirlockProtoId = "AirlockShuttle";

        [Test]
        public async Task TheSweepImpoundsWhatCouldHaveComeHomeAndWritesOffWhatCouldNot()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var cfg = server.ResolveDependency<IConfigurationManager>();
            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();
            var mapSys = server.System<SharedMapSystem>();
            var stationSys = server.System<StationSystem>();
            var metaData = server.System<MetaDataSystem>();
            var transform = server.System<SharedTransformSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            for (var i = 0; i < 3; i++)
                await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);
            var round = await db.AddNewRound(await db.AddOrGetServer("drydock-test"));

            // Three rows out in the round: a hull with a helm and a dock, a hull with neither, and a
            // row no hull carries at all. Each is stored and then claimed the way a retrieve claims.
            var homeward = Guid.NewGuid();
            var helpless = Guid.NewGuid();
            var gone = Guid.NewGuid();
            foreach (var id in new[] { homeward, helpless, gone })
            {
                Assert.That((await store.FileRevision(Revision(id, owner), new byte[] { 1 }, 3)).Outcome, Is.EqualTo(DrydockBerthResult.Success));
                Assert.That(await store.TrySetState(id, DrydockShipState.Stored, DrydockShipState.CheckedOut, DrydockAuditAction.Retrieve, owner, round, null), Is.True);
                await store.VacateBerth(id);
            }

            var map = await pair.CreateTestMap();
            EntityUid station = default;
            EntityUid able = default;
            EntityUid unable = default;
            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);
                cfg.SetCVar(TriadCCVars.DrydockImpoundRoundEndFraction, 0.5f);

                station = entMan.Spawn();
                entMan.AddComponent<StationDataComponent>(station);
                stationSys.AddGridToStation(station, map.Grid.Owner);

                able = BuildHull(entMan, mapSys, metaData, transform, map.MapId, new Vector2(20f, 20f), "Behir", owner, homeward);
                var helm = entMan.SpawnEntity(null, new EntityCoordinates(able, new Vector2(1.5f, 1.5f)));
                entMan.EnsureComponent<ShuttleConsoleComponent>(helm);
                entMan.SpawnEntity(ShuttleAirlockProtoId, new EntityCoordinates(able, new Vector2(0.5f, 1.5f)));

                unable = BuildHull(entMan, mapSys, metaData, transform, map.MapId, new Vector2(60f, 60f), "Hulk", owner, helpless);
            });

            // The fork's janitors delete small, unpowered, worthless grids far from a player, and
            // every grid here is exactly one.
            await pair.MakeCleanupImmune(map.Grid.Owner);
            await pair.MakeCleanupImmune(able);
            await pair.MakeCleanupImmune(unable);
            await pair.RunTicksSync(5);

            // Control: the criterion reads each hull the way the sweep will.
            await server.WaitAssertion(() =>
            {
                var (helms, docks) = drydock.GridsWithHelmsAndDocks();
                Assert.That(drydock.CouldHaveComeHome(able, helms, docks), Is.True, "A helm and a dock: it could have brought itself home.");
                Assert.That(drydock.CouldHaveComeHome(unable, helms, docks), Is.False, "Neither: it could not.");
            });

            await RunSweep(pair, drydock, round);

            var rows = (await store.GetShipsByOwner(owner)).ToDictionary(r => r.ShipGuid);
            Assert.Multiple(() =>
            {
                Assert.That(rows[homeward].State, Is.EqualTo(DrydockShipState.Impounded), "A hull that could have come home is taken into the lot.");
                Assert.That(rows[homeward].ImpoundRedeemable, Is.True, "The owner may reclaim it.");
                Assert.That(rows[homeward].ImpoundReason, Is.EqualTo($"Still in the world at the end of round {round}."));
                Assert.That(rows[homeward].BerthId, Is.Null, "The lot is not a berth.");
                Assert.That(rows[homeward].CheckedOutRoundId, Is.Null, "Nothing is out.");
                Assert.That(rows[helpless].State, Is.EqualTo(DrydockShipState.Destroyed), "No helm, no dock: written off.");
                Assert.That(rows[gone].State, Is.EqualTo(DrydockShipState.Destroyed), "No hull at all: written off.");
            });

            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.Deleted(able), Is.True, "The impound took the hull out of the world.");
                Assert.That(entMan.Deleted(unable), Is.False, "A write-off leaves the hull to the round's end.");
            });

            var taken = (await store.GetAudit(homeward))[^1];
            Assert.Multiple(() =>
            {
                Assert.That(taken.Action, Is.EqualTo(DrydockAuditAction.Impound));
                Assert.That(taken.ActorUserId, Is.Null, "The sweep is the system.");
                Assert.That(taken.SubjectUserId, Is.EqualTo(owner));
                Assert.That(taken.RoundId, Is.EqualTo(round));
                Assert.That(taken.Reason, Does.Contain("owner can reclaim"));
            });

            // The fee is the sweep's share of what the store appraised the hull at as it was taken.
            var appraisal = (await store.GetCurrentAppraisals(owner))[homeward];
            Assert.That(appraisal, Is.Not.Null, "The forced store appraises the hull like any store.");
            Assert.That(rows[homeward].ImpoundFee, Is.EqualTo(DrydockImpoundFee.Against(appraisal!.Value, 50)));

            var writtenOff = (await store.GetAudit(helpless))[^1];
            Assert.Multiple(() =>
            {
                Assert.That(writtenOff.Action, Is.EqualTo(DrydockAuditAction.ShipDestroyed));
                Assert.That(writtenOff.ActorUserId, Is.Null);
                Assert.That(writtenOff.SubjectUserId, Is.EqualTo(owner));
                Assert.That(writtenOff.RoundId, Is.EqualTo(round));
                Assert.That(writtenOff.Reason, Does.Contain("no piloting console"));
            });
            Assert.That((await store.GetAudit(gone))[^1].Action, Is.EqualTo(DrydockAuditAction.ShipDestroyed));

            // Sweeping the same round again judges nothing: every row has moved on.
            await RunSweep(pair, drydock, round);
            Assert.That((await store.GetAudit(helpless)).Count(a => a.Action == DrydockAuditAction.ShipDestroyed), Is.EqualTo(1),
                "A verdict is written once; a second pass finds the row no longer checked out.");

            // The way back from a write-off is the admin's restore, and the timeline says Restore.
            var berth = (await store.GetBerths(owner)).First(b => b.Occupant == null).Berth.BerthId;
            Assert.That(await store.TryRestoreShip(helpless, berth, Guid.NewGuid(), round, "found it"), Is.EqualTo(DrydockBerthResult.Success));
            Assert.That((await store.GetAudit(helpless))[^1].Action, Is.EqualTo(DrydockAuditAction.Restore));

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The restart waits while a sweep is filing and not past the ceiling. Driven through the
        /// same hold the ticker calls, with the sweep started for a round of this test's own.
        /// </summary>
        [Test]
        public async Task TheRestartWaitsForTheSweepAndNotPastItsCeiling()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var cfg = server.ResolveDependency<IConfigurationManager>();
            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();
            var mapSys = server.System<SharedMapSystem>();
            var metaData = server.System<MetaDataSystem>();
            var transform = server.System<SharedTransformSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);
            var round = await db.AddNewRound(await db.AddOrGetServer("drydock-test"));

            var shipId = Guid.NewGuid();
            Assert.That((await store.FileRevision(Revision(shipId, owner), new byte[] { 1 }, 3)).Outcome, Is.EqualTo(DrydockBerthResult.Success));
            Assert.That(await store.TrySetState(shipId, DrydockShipState.Stored, DrydockShipState.CheckedOut, DrydockAuditAction.Retrieve, owner, round, null), Is.True);
            await store.VacateBerth(shipId);

            var map = await pair.CreateTestMap();
            EntityUid hull = default;
            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);
                cfg.SetCVar(TriadCCVars.DrydockImpoundRestartCeilingSeconds, 180);

                hull = BuildHull(entMan, mapSys, metaData, transform, map.MapId, new Vector2(20f, 20f), "Behir", owner, shipId);
                var helm = entMan.SpawnEntity(null, new EntityCoordinates(hull, new Vector2(1.5f, 1.5f)));
                entMan.EnsureComponent<ShuttleConsoleComponent>(helm);
                entMan.SpawnEntity(ShuttleAirlockProtoId, new EntityCoordinates(hull, new Vector2(0.5f, 1.5f)));
            });
            await pair.MakeCleanupImmune(map.Grid.Owner);
            await pair.MakeCleanupImmune(hull);
            await pair.RunTicksSync(5);

            // Nothing running: nothing to wait for.
            var resumed = 0;
            await server.WaitAssertion(() => Assert.That(drydock.HoldWhileSweeping(() => resumed++), Is.False, "Control: with no sweep in flight the restart is not held."));

            // The sweep in flight: the restart is held and told to come back.
            var held = false;
            await server.WaitPost(() =>
            {
                drydock.StartRoundEndSweep(round);
                held = drydock.HoldWhileSweeping(() => resumed++);
            });
            Assert.That(held, Is.True, "A sweep still filing holds the restart.");

            var deadline = Stopwatch.StartNew();
            while (drydock.SweepInFlight && deadline.Elapsed < TimeSpan.FromSeconds(60))
                await pair.RunTicksSync(1);

            Assert.That(drydock.SweepInFlight, Is.False, "The sweep never finished.");
            Assert.That((await store.GetShipHeader(shipId))!.State, Is.EqualTo(DrydockShipState.Impounded), "Control: the sweep the restart waited for did its work.");

            // The rescheduled restart comes back on the game clock, and once the sweep is over it is not held again.
            while (resumed == 0 && deadline.Elapsed < TimeSpan.FromSeconds(60))
                await pair.RunTicksSync(1);
            Assert.That(resumed, Is.GreaterThanOrEqualTo(1), "The held restart is rescheduled, not dropped.");
            await server.WaitAssertion(() => Assert.That(drydock.HoldWhileSweeping(() => resumed++), Is.False, "Drained, so the restart goes ahead."));

            // With no ceiling at all the restart is never held, whatever is in flight.
            var second = await db.AddNewRound(await db.AddOrGetServer("drydock-test"));
            await server.WaitPost(() => cfg.SetCVar(TriadCCVars.DrydockImpoundRestartCeilingSeconds, 0));
            var heldAtZero = true;
            await server.WaitPost(() =>
            {
                drydock.StartRoundEndSweep(second);
                heldAtZero = drydock.HoldWhileSweeping(() => resumed++);
            });
            Assert.That(heldAtZero, Is.False, "A ceiling of zero holds nothing.");
            deadline.Restart();
            while (drydock.SweepInFlight && deadline.Elapsed < TimeSpan.FromSeconds(60))
                await pair.RunTicksSync(1);

            await pair.CleanReturnAsync();
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Runs the sweep for one round on the game thread and pumps until it finishes, bounded by the wall clock.</summary>
        private static async Task RunSweep(Pair.TestPair pair, DrydockSystem drydock, int round)
        {
            Task? sweep = null;
            await pair.Server.WaitPost(() => sweep = drydock.RunRoundEndSweep(round));

            var deadline = Stopwatch.StartNew();
            while (!sweep!.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(60))
                await pair.RunTicksSync(1);

            Assert.That(sweep!.IsCompleted, Is.True, "The round-end sweep never finished.");
            await sweep;
            await pair.RunTicksSync(5);
        }

        /// <summary>
        /// A 3x3 hull stamped as a retrieved drydock ship: the identity the sweep finds it by and
        /// the ownership the warning and the impound read.
        /// </summary>
        private static EntityUid BuildHull(IEntityManager entMan, SharedMapSystem mapSys, MetaDataSystem metaData, SharedTransformSystem transform, MapId mapId, Vector2 at, string name, Guid owner, Guid shipId)
        {
            var grid = mapSys.CreateGridEntity(mapId);
            var tile = new Tile(1);
            for (var x = 0; x < 3; x++)
            {
                for (var y = 0; y < 3; y++)
                    mapSys.SetTile(grid.Owner, grid.Comp, new Vector2i(x, y), tile);
            }

            transform.SetWorldPosition(grid.Owner, at);
            entMan.EnsureComponent<ShuttleComponent>(grid.Owner);
            metaData.SetEntityName(grid.Owner, name);
            entMan.EnsureComponent<ShipOwnershipComponent>(grid.Owner).OwnerUserId = new NetUserId(owner);
#pragma warning disable RA0002
            entMan.EnsureComponent<DrydockIdentityComponent>(grid.Owner).ShipId = shipId;
#pragma warning restore RA0002
            return grid.Owner;
        }

        private static DrydockRevisionRequest Revision(Guid shipId, Guid owner) => new()
        {
            ShipGuid = shipId,
            OwnerUserId = owner,
            ShipName = "Behir",
            SizeClass = nameof(ShipSizeClass.Cutter),
            MarkStored = true,
            Kind = DrydockRevisionKind.PlayerStore,
            ActorUserId = owner,
            EngineFormatVer = 7,
            ProtoFingerprint = new byte[] { 1 },
            CapturedKeyHash = new byte[] { 1 },
            Checksum = new byte[] { 1 },
            SizeBytes = 1,
            Manifest = "{}",
            AppraisedValue = 24000,
        };
    }
}
