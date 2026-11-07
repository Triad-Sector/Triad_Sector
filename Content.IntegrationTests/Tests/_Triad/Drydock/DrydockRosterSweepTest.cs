#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.ShipSize;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// Breadth, where the other drydock tests are depth. Everything else builds a ship out of a few
    /// hand-picked entities chosen to fail for one reason at a time; this one takes the ships the
    /// fork actually sells and puts every one of them through a round trip.
    ///
    /// <para>It is the test that answers the question the hand-built ones cannot: whether this
    /// fork's own content survives, including the Monolith-derived content the implementation this
    /// was ported from never saw. A vessel that refuses to store, comes back missing entities, or
    /// comes back physically inconsistent is a ship somebody paid for.</para>
    ///
    /// <para>It deliberately does not stop at the first failure. A sweep whose value is "which of
    /// the hundred and forty are broken" is worth much less if it only ever names one, so every
    /// vessel is attempted and the failures are reported together.</para>
    ///
    /// <para>One pair for the whole sweep, because acquiring a pooled pair costs roughly twenty
    /// seconds against a round trip's few, so a pair per vessel would make this an hour instead of
    /// minutes. About three minutes for the whole roster.</para>
    ///
    /// <para><b>Refusals here are real defects, and were not always.</b> A sound played at grid
    /// coordinates is a real grid child until its despawn timer fires, but its prototype declares
    /// <c>save: false</c>, so the serializer never writes it: the validation counted it live, never
    /// saw it reload, and refused whichever ship had a sound in the air at that instant, which made
    /// the whole sweep non-deterministic. The validation now counts through the
    /// serializer's own exclusion, and
    /// <see cref="DrydockRoundTripTest.ALiveSoundEffectDoesNotBlockTheStore"/> plants a sound
    /// deliberately to hold that fix down. With the timing removed, a refusal here is a real
    /// defect in a real hull, so refusals are asserted like every other failure.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockRosterSweepTest
    {

        [Test]
        public async Task EveryRosterVesselSurvivesARoundTrip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var cfg = server.ResolveDependency<IConfigurationManager>();
            var db = server.ResolveDependency<IServerDbManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var drydock = server.System<DrydockSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var shipyard = server.System<ShipyardSystem>();
            var stationSys = server.System<StationSystem>();
            var mapLoader = server.System<MapLoaderSystem>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);

            // Every vessel is stored and retrieved in turn, so one berth would do; three keep a
            // single failed retrieve from turning every later store into a capacity refusal.
            var store = server.ResolveDependency<DrydockStore>();
            for (var i = 0; i < 3; i++)
                await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var map = await pair.CreateTestMap();

            EntityUid station = default;

            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);

                // Slicing off, which for this cvar means no job at all rather than a job with a zero
                // budget. This sweep runs the whole roster through a pooled pair on a sixty second
                // wall-clock pump per operation (DrydockTestHelpers.RunOnServer); a capital hull
                // sliced at the shipping default suspends thousands of times and would blow that
                // ceiling on breadth alone. What slicing does
                // to a store is its own fixture's question, and it is a question about one hull.
                cfg.SetCVar(TriadCCVars.DrydockTickBudgetMs, 0);

                shipyard.SetupShipyardIfNeeded();

                station = entMan.Spawn();
                entMan.AddComponent<StationDataComponent>(station);
                stationSys.AddGridToStation(station, map.Grid.Owner);
            });

            // This is the fixture the janitor actually caught: an unprotected run lost the station's
            // grid partway down the roster, and every retrieve after that was refused NoStation with
            // its berth still held, which then read as twenty-two drydock failures.
            await pair.MakeCleanupImmune(map.Grid.Owner);

            await pair.RunTicksSync(5);

            var roster = protoMan.EnumeratePrototypes<VesselPrototype>()
                .Where(v => !v.Abstract)
                .OrderBy(v => v.ID)
                .ToList();

            // Faction hulls are refused by the console before any of this runs, and they are refused
            // because they are issued full of restricted loadout the save path is supposed to strip.
            // Sweeping one measures a path no player can reach, and it does not measure it quietly:
            // the strip takes anchored ship machinery too, so a TDF hull reports its whole propulsion
            // as lost state and buries the findings that are about ships somebody can actually store.
            // Skipped by the same component the console gates on, so this list cannot drift from it.
            //
            // The sweep calls TryStoreShip directly rather than going through the console, and it
            // loads hulls straight from ShuttlePath, where a vessel's addComponents have not been
            // applied yet. So the gate has to be read off the prototype here; asking the grid would
            // silently answer "not blacklisted" for every ship in the fleet.
            var refused = roster
                .Where(v => v.AddComponents.ContainsKey("ShipSavingBlacklist"))
                .ToList();

            var vessels = roster.Except(refused).ToList();

            Assert.That(vessels, Is.Not.Empty, "The control: an empty roster would make this sweep vacuous.");
            Assert.That(refused, Is.Not.Empty,
                "The control on the skip: the fork has faction vessels, so filtering them out and "
                + "getting nothing back means the gate stopped matching rather than that they are gone.");

            var failures = new List<string>();
            var deltas = new List<string>();

            // Field-level differences across the whole fleet, aggregated by what changed rather
            // than by which ship it changed on, because the same gap shows up on every vessel
            // carrying the machine and a per-ship list would bury that under its own length.
            var drift = new Dictionary<string, (int Occurrences, string Example)>();
            var snapshotCost = System.Diagnostics.Stopwatch.StartNew();
            snapshotCost.Stop();
            var covered = 0;
            var ambiguous = 0;
            var swept = 0;

            // How many entities the thaw check below actually looked at, across the whole fleet. A
            // per-vessel walk that quietly stopped reaching anything would otherwise report every
            // hull as thawed.
            var thawWalked = 0;

            foreach (var vessel in vessels)
            {
                EntityUid? loaded = null;

                // Checked every vessel, because a station that quietly loses its grid reports as a
                // drydock refusal on every hull after it: NoStation until the berths fill, then
                // NoBerth. One named failure here beats twenty-two that blame the wrong system.
                var stationHasGrid = false;
                await server.WaitPost(() =>
                {
                    stationHasGrid = entMan.TryGetComponent<StationDataComponent>(station, out var data)
                                     && stationSys.GetLargestGrid(data) is not null;
                });

                Assert.That(stationHasGrid, Is.True,
                    $"Before {vessel.ID}: the requesting station has no grid left, so the refusals below would be about the fixture and not about the drydock.");

                await server.WaitPost(() =>
                {
                    if (mapLoader.TryLoadGrid(map.MapId, vessel.ShuttlePath, out var grid))
                        loaded = grid!.Value.Owner;
                });

                await pair.RunTicksSync(3);

                if (loaded == null)
                {
                    failures.Add($"{vessel.ID}: its shuttle file at {vessel.ShuttlePath} would not load at all.");
                    continue;
                }

                var before = await CensusGrid(pair, loaded.Value);

                DrydockStateSnapshot beforeState = default!;
                await server.WaitPost(() =>
                {
                    snapshotCost.Start();
                    beforeState = fidelity.SnapshotGrid(loaded!.Value);
                    snapshotCost.Stop();
                });

                try
                {
                    var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair,
                        () => drydock.TryStoreShip(loaded.Value, owner, null));

                    if (result != DrydockStoreResult.Success || shipId == null)
                    {
                        failures.Add($"{vessel.ID}: store refused with {result}.");
                        await Cleanup(pair, loaded);
                        continue;
                    }

                    await pair.RunTicksSync(3);

                    var retrieved = await DrydockTestHelpers.RunOnServer(pair,
                        () => drydock.TryRetrieveShip(shipId.Value, owner, station, null));

                    if (!retrieved.Succeeded)
                    {
                        failures.Add($"{vessel.ID}: stored, then would not come back ({retrieved.Result}).");
                        continue;
                    }

                    // The same settling time the loaded ship got before its snapshot. A power net
                    // rebuilds over several ticks and its devices toggle while it does, so comparing
                    // three ticks of settling against ten reports the simulation settling rather than
                    // the round trip losing anything: the first fleet-wide run spent thousands of
                    // lines saying so. Equalised on this side rather than by ticking the loaded ship
                    // longer, because the extra ticks gave the firelock update seven more chances per
                    // vessel to ask a torn-down map for its atmosphere, which is a known flake here.
                    await pair.RunTicksSync(3);

                    DrydockStateSnapshot afterState = default!;
                    await server.WaitPost(() =>
                    {
                        snapshotCost.Start();
                        afterState = fidelity.SnapshotGrid(retrieved.Grid!.Value);
                        snapshotCost.Stop();
                    });

                    // Ticking is the physics assertion. A grid whose broadphase or contact list came
                    // back inconsistent throws on the tick, not on the load, and the harness turns
                    // that into a failure here rather than somewhere unrelated later.
                    await pair.RunTicksSync(7);

                    // The thaw, across the whole fleet. Every document filed since the store learned
                    // to freeze carries a paused flag per entity, and the retrieve loads onto a
                    // paused map on purpose, so the walk that clears those flags is the only thing
                    // standing between a player and a ship that looks perfect and cannot act. The
                    // hand-built fixture proves the walk works on nine entities; this proves it does
                    // not run out of patience on nine hundred. Collected as a failure line rather
                    // than asserted here, so one frozen hull does not end the sweep.
                    var stillFrozen = 0;
                    var walked = 0;
                    await server.WaitPost(() =>
                    {
                        var stack = new Stack<EntityUid>();
                        stack.Push(retrieved.Grid!.Value);

                        while (stack.Count > 0)
                        {
                            var current = stack.Pop();
                            walked++;

                            if (entMan.GetComponent<MetaDataComponent>(current).EntityPaused)
                                stillFrozen++;

                            var children = entMan.GetComponent<TransformComponent>(current).ChildEnumerator;
                            while (children.MoveNext(out var child))
                                stack.Push(child);
                        }
                    });

                    thawWalked += walked;

                    if (stillFrozen > 0)
                        failures.Add($"{vessel.ID}: {stillFrozen} of {walked} entities came back from the drydock still paused.");

                    var after = await CensusGrid(pair, retrieved.Grid!.Value);

                    covered += beforeState.Entities;
                    ambiguous += beforeState.Ambiguous;

                    foreach (var line in DrydockStateSnapshot.Diff(beforeState, afterState)
                                 .Where(DrydockRoundTripExpectations.IsUnexpected))
                    {
                        var kind = DiffKind(line);
                        var seen = drift.GetValueOrDefault(kind, (Occurrences: 0, Example: line));
                        drift[kind] = (seen.Occurrences + 1, seen.Example);
                    }

                    // Reported, deliberately not asserted. Census equality is the wrong bar for a
                    // roster sweep, because a store is not supposed to be lossless: it empties
                    // unoccupied AI cores on purpose, which costs the brain vessel, the holo and
                    // their action entities. Drop and duplicate detection is the store's own
                    // validation diff, which runs on every vessel here and is what refuses the two
                    // that refuse. This line exists so a change in what a round trip costs shows up
                    // in the run output instead of being invisible.
                    if (!SameCensus(before, after))
                        deltas.Add($"{vessel.ID}: {DescribeDelta(before, after)}");

                    await Cleanup(pair, retrieved.Grid);
                }
                catch (Exception e)
                {
                    failures.Add($"{vessel.ID}: threw {e.GetType().Name}: {e.Message}");
                }

                swept++;
            }

            await TestContext.Out.WriteLineAsync(
                $"[roster-sweep] snapshot walked {covered} entities ({ambiguous} dropped as ambiguous) "
                + $"in {snapshotCost.Elapsed.TotalSeconds:F1}s of the run.");

            // What the sweep did not look at, named rather than merely absent, so a hull that goes
            // missing from the report because someone blacklisted it is visible here instead of
            // reading as a ship that passed.
            await TestContext.Out.WriteLineAsync(
                $"[roster-sweep] {vessels.Count} of {roster.Count} vessel(s) swept; {refused.Count} "
                + $"skipped as save-blacklisted: {string.Join(", ", refused.Select(v => v.ID))}");

            if (drift.Count > 0)
            {
                await TestContext.Out.WriteLineAsync(
                    $"[roster-sweep] {drift.Count} kind(s) of state changed across the round trip:"
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, drift
                        .OrderByDescending(d => d.Value.Occurrences)
                        .Select(d => $"  {d.Value.Occurrences,5} on entities  {d.Key}"
                                     + Environment.NewLine + $"       e.g. {d.Value.Example}")));
            }

            if (deltas.Count > 0)
            {
                await TestContext.Out.WriteLineAsync(
                    $"[roster-sweep] {deltas.Count} vessel(s) changed manifest across the round trip:"
                    + Environment.NewLine + string.Join(Environment.NewLine, deltas));
            }

            Assert.That(thawWalked, Is.GreaterThan(swept),
                "The control on the thaw check: it has to have visited more entities than there were hulls, or "
                + "it is reporting every ship unpaused because it never reached one.");

            Assert.That(failures, Is.Empty,
                $"{failures.Count} roster vessel(s) failed a drydock round trip outright, out of {vessels.Count} "
                + $"({swept} completed a full attempt):{Environment.NewLine}"
                + string.Join(Environment.NewLine, failures));

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The part of a diff line that says what changed, with the ship and the entity it happened
        /// on stripped off. One gap on one machine type produces a line per vessel that carries it,
        /// and grouping by this is what turns a hundred lines back into one finding.
        /// </summary>
        private static string DiffKind(string line)
        {
            var verb = line.IndexOf(' ');
            if (verb < 0)
                return line;

            // The verb stays in the key. Without it a field that vanished on one entity and appeared
            // on another collapse into one row, which is exactly the pair that says an entity moved
            // rather than went missing, and the first fleet-wide run could not tell those apart.
            var kind = line[..verb];
            var body = line[verb..].TrimStart();

            var pipe = body.IndexOf('|');
            if (pipe < 0)
                return $"{kind} {body}";

            var field = body[(pipe + 1)..];
            var space = field.IndexOf(' ');
            return $"{kind} {(space < 0 ? field : field[..space])}";
        }

        private static async Task Cleanup(TestPair pair, EntityUid? grid)
        {
            if (grid == null)
                return;

            await pair.Server.WaitPost(() =>
            {
                if (!pair.Server.EntMan.Deleted(grid.Value))
                    pair.Server.EntMan.DeleteEntity(grid.Value);
            });

            await pair.RunTicksSync(3);
        }

        private static bool SameCensus(Dictionary<string, int> before, Dictionary<string, int> after)
        {
            if (before.Count != after.Count)
                return false;

            foreach (var (proto, count) in before)
            {
                if (!after.TryGetValue(proto, out var got) || got != count)
                    return false;
            }

            return true;
        }

        private static string DescribeDelta(Dictionary<string, int> before, Dictionary<string, int> after)
        {
            var parts = new List<string>();

            foreach (var (proto, count) in before)
            {
                var got = after.GetValueOrDefault(proto);
                if (got != count)
                    parts.Add($"{proto} {count}->{got}");
            }

            foreach (var (proto, count) in after)
            {
                if (!before.ContainsKey(proto))
                    parts.Add($"{proto} 0->{count}");
            }

            // A whole-ship difference is not worth printing in full; the first few name the shape.
            return string.Join(", ", parts.Take(8)) + (parts.Count > 8 ? $", and {parts.Count - 8} more" : "");
        }

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
                        stack.Push(child);

                        // The serializer's own exclusion: a save: false prototype (sounds, chat
                        // effects) is never written into a store, so a census that counts one is
                        // measuring timing rather than the round trip. Counting sounds made the
                        // first run of this sweep report a hundred and twenty differences that
                        // were all timing, and the store's validation had the same bug.
                        var meta = entMan.GetComponent<MetaDataComponent>(child);
                        if (meta.EntityPrototype?.MapSavable == false)
                            continue;

                        var proto = meta.EntityPrototype?.ID ?? "<no prototype>";
                        census[proto] = census.GetValueOrDefault(proto) + 1;
                    }
                }
            });

            return census;
        }
    }
}
