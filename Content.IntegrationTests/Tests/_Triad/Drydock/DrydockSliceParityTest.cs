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
using Content.Server._NF.Shipyard.Systems;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.ShipSize;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// Slicing is only worth having if it changes nothing but when the work happens. A store spread
    /// over hundreds of ticks has to file the ship the same store filed in one, and the ship it
    /// hands back has to survive the same fidelity oracle every other drydock test measures against.
    ///
    /// <para>What this deliberately does not claim is byte equality between the two documents, and
    /// the reason is worth writing down rather than discovering twice. Two things stop it. Yaml uids
    /// are handed out in <c>HashSet&lt;EntityUid&gt;</c> iteration order, so two loads of the same
    /// hull are not contractually guaranteed to number their entities the same way; and a document
    /// written later is written at a later clock, so anything that advances with time is expected to
    /// differ. The engine's own metadata block puts a wall-clock stamp in every file for the same
    /// reason. So the comparison here is structural: the same prototypes, the same components on
    /// them, the same entity count. Values are the fidelity oracle's job, and it runs on both round
    /// trips below.</para>
    ///
    /// <para>The control that makes any of it mean anything is the tick count. A sliced store
    /// suspends at every phase boundary and again whenever it spends its budget, so it must take
    /// visibly more ticks than the same store at a budget of zero. Without that assertion this whole
    /// fixture could pass by running two identical unsliced stores.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockSliceParityTest
    {
        /// <summary>
        /// The budget the sliced half runs at: the shipping default, since the question is whether
        /// the setting players will actually be served by is safe.
        /// </summary>
        private const int SlicedBudgetMs = 2;

        [Test]
        public async Task ASlicedStoreRoundTripsAsCleanlyAsAnUnslicedOne()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var cfg = server.ResolveDependency<IConfigurationManager>();
            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);

            // Three, one per store below, because a berth is only freed once the retrieve that
            // empties it has finished.
            for (var i = 0; i < 3; i++)
                await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, ship) = await BuildShipAndStation(pair);

            // --- the warm-up, which is not measured ---
            // A retrieve stamps ShipOwnershipComponent and StationVariationHasRunComponent onto the
            // grid it hands back, and NavMapComponent lands second-hand off RecreateStation's
            // StationGridAddedEvent (NavMapSystem.cs:66). A never-retrieved hull is therefore a
            // different shape from a retrieved one, so both measured stores below read a retrieved
            // grid and the comparison is generation 1 against generation 2, not 0 against 1.
            await server.WaitPost(() => cfg.SetCVar(TriadCCVars.DrydockTickBudgetMs, 0));

            var warmStore = await RunOnServer(pair, () => drydock.TryStoreShip(ship, owner, null, stationUid: station));
            Assert.That(warmStore.Result.Result, Is.EqualTo(DrydockStoreResult.Success), "Control: the warm-up store has to succeed.");
            await pair.RunTicksSync(5);

            var warmBack = await RunOnServer(pair, () => drydock.TryRetrieveShip(warmStore.Result.ShipId!.Value, owner, station, null));
            Assert.That(warmBack.Result.Result, Is.EqualTo(DrydockRetrieveResult.Success), "Control: the warm-up retrieve has to complete.");
            var warmGrid = warmBack.Result.Grid!.Value;
            await pair.RunTicksSync(10);

            DrydockStateSnapshot beforeState = default!;
            await server.WaitPost(() => beforeState = fidelity.SnapshotGrid(warmGrid));
            Assert.That(beforeState.Entities, Is.GreaterThan(1),
                "The control on the oracle: it has to be looking at more than the grid, or a clean diff below says nothing.");

            // --- the unsliced half, which is also the baseline document ---
            var unsliced = await RunOnServer(pair, () => drydock.TryStoreShip(warmGrid, owner, null, stationUid: station));
            Assert.That(unsliced.Result.Result, Is.EqualTo(DrydockStoreResult.Success), "Control: the unsliced store has to succeed.");
            var unslicedShipId = unsliced.Result.ShipId!.Value;
            await pair.RunTicksSync(5);

            // Through LoadCurrent rather than the blob table directly: the two stores below are two
            // revisions of the same hull, so "the blob for this ship" is ambiguous and only the
            // current revision is the document that store filed.
            var unslicedLoad = await store.LoadCurrent(unslicedShipId);
            Assert.That(unslicedLoad, Is.Not.Null, "The unsliced store filed a readable current revision.");
            var unslicedDocument = Encoding.UTF8.GetString(DrydockSystem.DecompressZstd(unslicedLoad!.Blob));

            var firstBack = await RunOnServer(pair, () => drydock.TryRetrieveShip(unslicedShipId, owner, station, null));
            Assert.That(firstBack.Result.Result, Is.EqualTo(DrydockRetrieveResult.Success), "Control: the unsliced round trip has to complete.");
            var unslicedGrid = firstBack.Result.Grid!.Value;
            await pair.RunTicksSync(10);

            DrydockStateSnapshot unslicedState = default!;
            await server.WaitPost(() => unslicedState = fidelity.SnapshotGrid(unslicedGrid));

            var unslicedDrift = DrydockStateSnapshot.Diff(beforeState, unslicedState)
                .Where(DrydockRoundTripExpectations.IsUnexpected)
                .ToList();

            Assert.That(unslicedDrift, Is.Empty,
                "The baseline: an unsliced round trip of this fixture is clean, so anything the sliced one loses below is slicing's doing and not the fixture's."
                + Environment.NewLine + string.Join(Environment.NewLine, unslicedDrift));

            // --- the sliced half, on the ship the unsliced half handed back ---
            await server.WaitPost(() => cfg.SetCVar(TriadCCVars.DrydockTickBudgetMs, SlicedBudgetMs));

            var sliced = await RunOnServer(pair, () => drydock.TryStoreShip(unslicedGrid, owner, null, stationUid: station));
            Assert.That(sliced.Result.Result, Is.EqualTo(DrydockStoreResult.Success), "A sliced store still stores the ship.");
            var slicedShipId = sliced.Result.ShipId!.Value;
            await pair.RunTicksSync(5);

            Assert.That(sliced.Ticks, Is.GreaterThan(unsliced.Ticks),
                $"The control on the slicing: the sliced store took {sliced.Ticks} ticks against the unsliced store's {unsliced.Ticks}. "
                + "A sliced store suspends at every phase boundary, so if it did not take visibly longer the budget never reached the pipeline and this fixture ran the same store twice.");

            var slicedLoad = await store.LoadCurrent(slicedShipId);
            Assert.That(slicedLoad, Is.Not.Null, "The sliced store filed a readable current revision.");
            Assert.That(slicedLoad!.Revision.Revision, Is.GreaterThan(unslicedLoad.Revision.Revision),
                "The control on which document is which: the sliced store filed a later revision of the same hull, so the two documents below really are the two stores.");
            var slicedDocument = Encoding.UTF8.GetString(DrydockSystem.DecompressZstd(slicedLoad.Blob));

            var secondBack = await RunOnServer(pair, () => drydock.TryRetrieveShip(slicedShipId, owner, station, null));
            Assert.That(secondBack.Result.Result, Is.EqualTo(DrydockRetrieveResult.Success), "A sliced store's ship still comes back.");
            var slicedGrid = secondBack.Result.Grid!.Value;
            await pair.RunTicksSync(10);

            DrydockStateSnapshot slicedState = default!;
            await server.WaitPost(() => slicedState = fidelity.SnapshotGrid(slicedGrid));

            var slicedDrift = DrydockStateSnapshot.Diff(unslicedState, slicedState)
                .Where(DrydockRoundTripExpectations.IsUnexpected)
                .ToList();

            Assert.That(slicedDrift, Is.Empty,
                $"{slicedDrift.Count} field(s) changed across a sliced round trip that survived the unsliced one:"
                + Environment.NewLine + string.Join(Environment.NewLine, slicedDrift));

            // --- the documents, structurally ---
            var unslicedShape = DocumentShape(unslicedDocument);
            var slicedShape = DocumentShape(slicedDocument);

            Assert.That(unslicedShape.Count, Is.GreaterThan(2),
                "The control on the comparison: fewer than a handful of distinct keys means the parser stopped matching the document format and is comparing nothing.");

            var differences = new List<string>();
            foreach (var key in unslicedShape.Keys.Union(slicedShape.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                var before = unslicedShape.GetValueOrDefault(key);
                var after = slicedShape.GetValueOrDefault(key);
                if (before != after)
                    differences.Add($"{key}: {before} unsliced against {after} sliced");
            }

            Assert.That(differences, Is.Empty,
                $"{differences.Count} structural difference(s) between the document a sliced store filed and the one an unsliced store filed:"
                + Environment.NewLine + string.Join(Environment.NewLine, differences));

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// What a stored document is made of, with everything that legitimately differs between two
        /// documents left out. Prototypes and component types are the two things a store cannot get
        /// wrong quietly: a dropped entity, a duplicated one, a substitution, or a component that
        /// failed to serialize all show up as a count that moved. Values do not appear here on
        /// purpose, because anything that advances with the clock differs between two documents
        /// written minutes apart, and values are what the fidelity oracle is for.
        /// </summary>
        private static Dictionary<string, int> DocumentShape(string yaml)
        {
            var shape = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var raw in yaml.Split('\n'))
            {
                var line = raw.TrimEnd('\r').Trim();

                string key;
                if (line.StartsWith("- proto:", StringComparison.Ordinal))
                    key = "proto " + line["- proto:".Length..].Trim();
                else if (line.StartsWith("- type:", StringComparison.Ordinal))
                    key = "component " + line["- type:".Length..].Trim();
                else if (line.StartsWith("- uid:", StringComparison.Ordinal))
                    key = "entity";
                else
                    continue;

                shape[key] = shape.GetValueOrDefault(key) + 1;
            }

            return shape;
        }

        /// <summary>
        /// A three-by-three plated grid with a few things bolted to it, and a station to fly it back
        /// to. Tiles first: a spawn at grid-local coordinates that are not on a set tile silently
        /// reparents to the map, and would then never be part of the ship at all.
        /// </summary>
        private static async Task<(EntityUid Station, EntityUid Ship)> BuildShipAndStation(TestPair pair)
        {
            var server = pair.Server;
            var entMan = server.EntMan;

            var cfg = server.ResolveDependency<IConfigurationManager>();
            var shipyard = server.System<ShipyardSystem>();
            var stationSys = server.System<StationSystem>();
            var mapSys = server.System<SharedMapSystem>();

            var map = await pair.CreateTestMap();

            EntityUid station = default;
            EntityUid ship = default;

            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);

                // Not for the retrieve, which loads onto a private map of its own and no longer
                // refuses without this one. The shipyard's own paths still want a staged map.
                shipyard.SetupShipyardIfNeeded();

                station = entMan.Spawn();
                entMan.AddComponent<StationDataComponent>(station);
                stationSys.AddGridToStation(station, map.Grid.Owner);

                var grid = mapSys.CreateGridEntity(map.MapId);
                ship = grid.Owner;

                var tile = new Tile(1);
                for (var x = 0; x < 3; x++)
                {
                    for (var y = 0; y < 3; y++)
                    {
                        mapSys.SetTile(grid.Owner, grid.Comp, new Vector2i(x, y), tile);
                    }
                }

                entMan.EnsureComponent<ShuttleComponent>(ship);
                entMan.System<MetaDataSystem>().SetEntityName(ship, "Parity");

                // Enough aboard that a sliced walk has something to slice, and enough kinds of thing
                // that a phase which quietly skipped a class of entity would move a count below.
                entMan.SpawnEntity("Airlock", new EntityCoordinates(ship, new Vector2(0f, 1f)));
                entMan.SpawnEntity("Airlock", new EntityCoordinates(ship, new Vector2(2f, 1f)));
                entMan.SpawnEntity("GasPipeStraight", new EntityCoordinates(ship, new Vector2(1f, 0f)));
                entMan.SpawnEntity("GasPipeStraight", new EntityCoordinates(ship, new Vector2(1f, 2f)));
            });

            // The station stands on the test grid, which the fork's janitors are built to delete.
            await pair.MakeCleanupImmune(map.Grid.Owner);

            await pair.RunTicksSync(10);

            return (station, ship);
        }

        /// <summary>
        /// Starts a pipeline on the game thread and pumps until it finishes, counting the ticks it
        /// took. The count is the only handle a test has on whether the pipeline actually sliced, so
        /// it is returned rather than merely bounded.
        /// </summary>
        private static async Task<(T Result, int Ticks)> RunOnServer<T>(TestPair pair, Func<Task<T>> start)
        {
            Task<T>? task = null;
            await pair.Server.WaitPost(() => task = start());

            var ticks = 0;

            // Generous, because a sliced store of even a small hull suspends once per phase and
            // again whenever it runs out of budget. The assertion below, not the ceiling, is what
            // reports a pipeline that stopped making progress.
            for (; ticks < 5000 && !task!.IsCompleted; ticks++)
            {
                await pair.RunTicksSync(1);
            }

            Assert.That(task!.IsCompleted, Is.True,
                "The drydock operation never completed: either it is blocked on the database, or a slice never resumed.");

            return (await task, ticks);
        }
    }
}
