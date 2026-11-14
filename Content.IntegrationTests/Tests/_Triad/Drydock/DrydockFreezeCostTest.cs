#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using System.Numerics;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// Prices the "freeze then slice" store: what it costs to move a ship onto a private paused map
    /// before serializing it, and what that move does to the document that gets stored.
    ///
    /// <para>Chunking the store's serialize across ticks is only safe if nothing can mutate the ship
    /// between slices. Pausing is the engine's own mechanism for that, but
    /// <see cref="SharedMapSystem.SetPaused"/> recurses into every descendant via
    /// <c>SetEntityPaused</c>, and <c>EntitySerializer</c> writes a <c>paused</c> key for any entity
    /// carrying that flag. So the freeze is visible in the artifact we persist, and this test is the
    /// evidence for exactly how visible.</para>
    ///
    /// <para>Reports rather than asserts on timing. The document comparison does assert: the only
    /// difference a freeze is allowed to make is the paused flag itself. Anything else means moving
    /// the ship changed the ship, which no amount of speed would justify.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockFreezeCostTest
    {
        /// <summary>
        /// How long each drift sample runs. Long enough for atmos and machine updates to land, short
        /// enough that six hulls still finish inside a pooled pair's patience.
        /// </summary>
        private const int DriftTicks = 20;

        [Test]
        public async Task FreezingAShipCostsLittleAndChangesOnlyThePausedFlag()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapLoader = server.System<MapLoaderSystem>();
            var mapSys = server.System<SharedMapSystem>();
            var xformSys = server.System<SharedTransformSystem>();
            var metaSys = server.System<MetaDataSystem>();

            var map = await pair.CreateTestMap();

            var vessels = protoMan.EnumeratePrototypes<VesselPrototype>()
                .Where(v => !v.Abstract)
                .Where(v => !v.AddComponents.ContainsKey("ShipSavingBlacklist"))
                .OrderBy(v => v.Price)
                .Where((_, i) => i % 3 == 0)
                .Take(20)
                .ToList();

            Assert.That(vessels, Is.Not.Empty, "The control: no vessels resolved, so this measures nothing.");

            var report = new List<string>();
            var leaks = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            var compared = 0;

            foreach (var vessel in vessels)
            {
                EntityUid? live = null;
                string? beforeYaml = null;
                string? afterYaml = null;
                var entities = 0;
                var paused = 0;
                EntityUid? scratchMap = null;
                double createMs = 0, moveMs = 0, pauseMs = 0, serializeMs = 0;

                await server.WaitPost(() =>
                {
                    if (!mapLoader.TryLoadGrid(map.MapId, vessel.ShuttlePath, out var grid))
                        return;

                    live = grid!.Value.Owner;
                });

                await pair.RunTicksSync(3);

                if (live == null)
                {
                    TestContext.Out.WriteLine($"SKIP {vessel.ID}: would not load.");
                    continue;
                }

                // How much a ship drifts on a live map over the same span, so the frozen result below
                // means something. Without this the "frozen document did not change" assertion could
                // just be a ship that was never going to change.
                string? liveFirst = null;
                string? liveSecond = null;

                await server.WaitPost(() => liveFirst = Serialize(mapLoader, live.Value));
                await pair.RunTicksSync(DriftTicks);
                await server.WaitPost(() => liveSecond = Serialize(mapLoader, live.Value));

                var liveDrift = liveFirst != null && liveSecond != null
                    ? Diff(liveFirst, liveSecond).Samples.Count
                    : -1;

                await server.WaitPost(() =>
                {
                    entities = CountRecursive(entMan, live.Value);

                    // Baseline: the document a store writes today, with the ship on a live map.
                    beforeYaml = Serialize(mapLoader, live.Value);

                    var watch = Stopwatch.StartNew();
                    var drydockMap = mapSys.CreateMap(out _);

                    // Paused while it is still empty, so the engine's own recursive walk has one
                    // entity to walk instead of a whole ship. SharedMapSystem.SetPaused recurses
                    // into every descendant, and doing that after the grid has arrived is the
                    // unbudgeted whole-ship walk this design exists to avoid.
                    mapSys.SetPaused(drydockMap, true);
                    createMs = watch.Elapsed.TotalMilliseconds;

                    watch.Restart();
                    xformSys.SetCoordinates(live.Value, new EntityCoordinates(drydockMap, Vector2.Zero));
                    moveMs = watch.Elapsed.TotalMilliseconds;

                    // The walk counts and back-stops; it is not what freezes the ship. The reparent
                    // above already did that: SharedTransformSystem.ChangeMapId reads the new map's
                    // pause state and ChangeMapIdRecursive stamps it onto every descendant, so by
                    // the time this runs nearly every SetEntityPaused call is an early return
                    // against a flag that already matches. What it is measuring, then, is the price
                    // of owning the walk anyway, which the production freeze does for the same
                    // reason: a walk we own is a walk we can slice, and a back-stop that costs this
                    // little is worth having against an engine change.
                    watch.Restart();
                    paused = SlicePause(entMan, metaSys, live.Value);
                    pauseMs = watch.Elapsed.TotalMilliseconds;

                    watch.Restart();
                    afterYaml = Serialize(mapLoader, live.Value);
                    serializeMs = watch.Elapsed.TotalMilliseconds;

                    scratchMap = drydockMap;
                });

                await pair.RunTicksSync(3);

                // The premise of slicing a serialize across ticks: a frozen ship holds still. Two
                // windows rather than one, because moving a grid tears down and rebuilds its
                // powernet, and that rebuild settles over the following ticks. A first window that
                // differs and a second that does not is settling, which a store can simply wait out.
                // A second window that also differs is live simulation, which would tear a sliced
                // snapshot and would sink this design.
                string? frozenSecond = null;
                string? frozenThird = null;

                await pair.RunTicksSync(DriftTicks);
                await server.WaitPost(() => frozenSecond = Serialize(mapLoader, live.Value));
                await pair.RunTicksSync(DriftTicks);
                await server.WaitPost(() => frozenThird = Serialize(mapLoader, live.Value));

                var settleDrift = afterYaml != null && frozenSecond != null
                    ? Diff(afterYaml, frozenSecond).Samples
                    : new List<string> { "the frozen ship would not serialize twice" };

                var frozenDrift = frozenSecond != null && frozenThird != null
                    ? Diff(frozenSecond, frozenThird).Samples
                    : new List<string> { "the frozen ship would not serialize three times" };

                // Inventory rather than fail-fast. One leaking system stops the sweep at the first
                // hull that carries it, and the whole point is to learn how many there are.
                foreach (var line in frozenDrift)
                    Record(leaks, "drift:" + KeyOf(line), vessel.ID);

                await server.WaitPost(() =>
                {
                    if (scratchMap is { } m && !entMan.Deleted(m))
                        entMan.DeleteEntity(m);
                });

                await pair.RunTicksSync(2);

                Assert.That(beforeYaml, Is.Not.Null, $"{vessel.ID}: the live-map serialize produced nothing.");
                Assert.That(afterYaml, Is.Not.Null, $"{vessel.ID}: the frozen serialize produced nothing.");

                var (added, removed, addedPaused, samples, xformSamples) = Diff(beforeYaml!, afterYaml!);

                // The whole fidelity question. A freeze may add paused flags, and it may move the
                // grid entity's own transform, because relocating the grid is the entire point. It
                // may not change anything else about the ship.
                // Inventoried rather than thrown, so one odd hull does not end the sweep. Both are
                // asserted in aggregate at the end.
                foreach (var line in samples)
                    Record(leaks, "move:" + KeyOf(line), vessel.ID);

                // The grid is one entity, so at most its own pos and rot may differ. More than that
                // means something aboard the ship moved, which a freeze must never cause.
                if (xformSamples.Count > 2)
                    Record(leaks, $"move:transform x{xformSamples.Count}", vessel.ID);

                report.Add($"{vessel.ID,-20} {entities,6} {paused,7} {createMs,8:F1} {moveMs,8:F1} {pauseMs,8:F1} "
                           + $"{serializeMs,10:F1} {beforeYaml!.Length / 1024,7} {added,7} {removed,7} {addedPaused,8} "
                           + $"{liveDrift,10} {settleDrift.Count,8} {frozenDrift.Count,8}");

                compared++;

                await server.WaitPost(() =>
                {
                    if (!entMan.Deleted(live.Value))
                        entMan.DeleteEntity(live.Value);
                });
                await pair.RunTicksSync(2);
            }

            Assert.That(compared, Is.GreaterThan(0),
                "The control: every vessel failed to load, so nothing was actually measured.");

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("=== what keeps changing on a frozen ship ===");
            if (leaks.Count == 0)
            {
                TestContext.Out.WriteLine("nothing: every frozen hull held still.");
            }
            else
            {
                foreach (var (key, hulls) in leaks)
                    TestContext.Out.WriteLine($"{key,-40} {hulls.Count,3} hull(s): {string.Join(", ", hulls.Take(8))}");
            }

            TestContext.Out.WriteLine("");
            TestContext.Out.WriteLine("=== drydock freeze cost ===");
            TestContext.Out.WriteLine($"{"vessel",-20} {"ents",6} {"paused",7} {"map ms",8} {"move ms",8} {"pause ms",8} "
                                      + $"{"ser ms",10} {"docKB",7} {"+lines",7} {"-lines",7} {"+paused",8} "
                                      + $"{"liveDrift",10} {"settle",8} {"frozen",8}");
            foreach (var line in report)
                TestContext.Out.WriteLine(line);

            Assert.That(leaks, Is.Empty,
                $"{leaks.Count} field(s) still change on a frozen ship, so a sliced serialize would tear the "
                + $"snapshot: {string.Join(" | ", leaks.Select(l => $"{l.Key} ({l.Value.Count} hulls)"))}");

            await pair.CleanReturnAsync();
        }

        private static void Record(SortedDictionary<string, SortedSet<string>> leaks, string key, string hull)
        {
            if (!leaks.TryGetValue(key, out var hulls))
                leaks[key] = hulls = new SortedSet<string>(StringComparer.Ordinal);

            hulls.Add(hull);
        }

        /// <summary>The yaml key a drift line names, so leaks group by field rather than by value.</summary>
        private static string KeyOf(string line)
        {
            var trimmed = Strip(line.TrimStart('+', '-', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ' '));
            var colon = trimmed.IndexOf(':');
            return colon > 0 ? trimmed[..colon] : trimmed;
        }

        /// <summary>
        /// The pause walk a sliced store runs, done here in one pass so its total cost is
        /// measurable. Breadth-first from the grid down, matching the order the production freeze
        /// walks in.
        ///
        /// <para>It counts and back-stops rather than being the mechanism. Reparenting onto an
        /// already-paused map pauses the whole subtree inside the engine's own recursion, so by the
        /// time this runs <c>SetEntityPaused</c> mostly returns early against a flag that already
        /// matches. The number it reports is therefore the entity count, and the milliseconds beside
        /// it are the price of keeping a walk we own and can slice.</para>
        /// </summary>
        /// <returns>How many entities were visited.</returns>
        private static int SlicePause(IEntityManager entMan, MetaDataSystem metaSys, EntityUid grid)
        {
            var count = 0;
            var queue = new Queue<EntityUid>();
            queue.Enqueue(grid);

            while (queue.TryDequeue(out var uid))
            {
                metaSys.SetEntityPaused(uid, true);
                count++;

                var children = entMan.GetComponent<TransformComponent>(uid).ChildEnumerator;
                while (children.MoveNext(out var child))
                    queue.Enqueue(child);
            }

            return count;
        }

        private static string? Serialize(MapLoaderSystem mapLoader, EntityUid grid)
        {
            using var writer = new StringWriter();
            var options = new SerializationOptions { MissingEntityBehaviour = MissingEntityBehaviour.Ignore };
            return mapLoader.TrySaveGrid(grid, writer, options) ? writer.ToString() : null;
        }

        /// <summary>
        /// Line-multiset diff of the two documents. Lines that are only a paused flag are counted
        /// separately and are the one difference a freeze is allowed to make; everything else comes
        /// back in <c>samples</c> and fails the test.
        /// </summary>
        private static (int Added, int Removed, int AddedPaused, List<string> Samples, List<string> XformSamples)
            Diff(string before, string after)
        {
            var beforeCounts = Tally(before);
            var afterCounts = Tally(after);

            var added = 0;
            var removed = 0;
            var addedPaused = 0;
            var samples = new List<string>();
            var xformSamples = new List<string>();

            foreach (var (line, count) in afterCounts)
            {
                beforeCounts.TryGetValue(line, out var was);
                var delta = count - was;
                if (delta <= 0)
                    continue;

                added += delta;
                Bucket($"+{delta} {line}", line, ref addedPaused, delta, samples, xformSamples);
            }

            foreach (var (line, count) in beforeCounts)
            {
                afterCounts.TryGetValue(line, out var now);
                var delta = count - now;
                if (delta <= 0)
                    continue;

                removed += delta;

                // A removed paused flag is just as much a freeze artifact as an added one: an entity
                // that was already paused on the live map and is written differently afterwards.
                var ignoredPaused = 0;
                Bucket($"-{delta} {line}", line, ref ignoredPaused, delta, samples, xformSamples);
            }

            return (added, removed, addedPaused, samples, xformSamples);
        }

        private static void Bucket(
            string rendered,
            string line,
            ref int pausedCount,
            int delta,
            List<string> samples,
            List<string> xformSamples)
        {
            var trimmed = Strip(line);

            // The serializer stamps meta.time with the wall clock, so two serializes a second apart
            // always differ there. That is file metadata, not ship state.
            if (trimmed.StartsWith("time:", StringComparison.Ordinal))
                return;

            if (trimmed is "paused: true" or "paused: false")
                pausedCount += delta;
            else if (trimmed.StartsWith("pos:", StringComparison.Ordinal)
                     || trimmed.StartsWith("rot:", StringComparison.Ordinal))
                xformSamples.Add(rendered);
            else
                samples.Add(rendered);
        }

        private static string Strip(string line)
        {
            return line.TrimStart().TrimStart('-', ' ');
        }

        private static Dictionary<string, int> Tally(string yaml)
        {
            var counts = new Dictionary<string, int>();

            foreach (var raw in yaml.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0)
                    continue;

                counts.TryGetValue(line, out var count);
                counts[line] = count + 1;
            }

            return counts;
        }

        private static int CountRecursive(IEntityManager entMan, EntityUid grid)
        {
            var count = 0;
            var stack = new Stack<EntityUid>();
            stack.Push(grid);

            while (stack.Count > 0)
            {
                var children = entMan.GetComponent<TransformComponent>(stack.Pop()).ChildEnumerator;
                while (children.MoveNext(out var child))
                {
                    count++;
                    stack.Push(child);
                }
            }

            return count;
        }
    }
}
