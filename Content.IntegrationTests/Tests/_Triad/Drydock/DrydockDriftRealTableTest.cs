#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server.Explosion.Components;
using Content.Server._Triad.Drydock;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Events;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The drift detector and the tier 1 re-bake against what only a running server has: the real
    /// migration files, the real prototype registry, the engine's own documents, and the loader that
    /// reads them. The drift matrix itself lives in <c>Content.Tests</c> on synthetic inputs.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockDocumentRebake))]
    public sealed class DrydockDriftRealTableTest
    {
        private static readonly Regex NextVisualUpdateLine = new(@"nextVisualUpdate: (\S+)");

        private static string Group(string proto, int uid) =>
            $"- proto: {proto}\n  entities:\n  - uid: {uid}\n    components:\n    - type: Transform\n      pos: 0.5,0.5\n      parent: 1\n";

        [Test]
        public async Task TheRealMappingFilesBuildTheLoadersTableAndDetectOnIt()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var resources = server.ResolveDependency<IResourceManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();

            DrydockMigrationTable table = null!;
            var ev = new BeforeEntityReadEvent();

            await server.WaitPost(() =>
            {
                table = DrydockMigrationTable.Load(resources);
                server.EntMan.EventBus.RaiseEvent(EventSource.Local, ev);
            });

            Assert.That(table.Renamed.Count + table.Deleted.Count, Is.GreaterThan(500),
                "The control: the real files hold roughly a thousand entries, so a near-empty table means they were not read.");

            // The table must be the one MapMigrationSystem hands the loader. Holiday replacements can
            // add renames of their own to the event, so the event may hold more, never less or other.
            Assert.Multiple(() =>
            {
                Assert.That(table.Deleted, Is.EquivalentTo(ev.DeletedPrototypes));
                foreach (var (from, to) in table.Renamed)
                {
                    Assert.That(ev.RenamedPrototypes.GetValueOrDefault(from), Is.EqualTo(to), $"rename of {from}");
                }
            });

            bool Known(string id) => protoMan.HasIndex<EntityPrototype>(id);

            var rename = table.Renamed
                .Where(kv => !table.Deleted.Contains(kv.Key) && !Known(kv.Key) && Known(kv.Value))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .First();
            var deleted = table.Deleted.Where(id => !Known(id)).Order(StringComparer.Ordinal).First();
            var known = protoMan.EnumeratePrototypes<EntityPrototype>()
                .Where(p => !p.Abstract && !table.Renamed.ContainsKey(p.ID) && !table.Deleted.Contains(p.ID))
                .Select(p => p.ID)
                .Order(StringComparer.Ordinal)
                .First();
            const string phantom = "DrydockDriftTestPhantomPrototype";

            var yaml = "meta:\n  format: 7\n  category: Grid\nentities:\n"
                + "- proto: \"\"\n  entities:\n  - uid: 1\n    components:\n    - type: Transform\n      parent: invalid\n"
                + Group(known, 2) + Group(rename.Key, 3) + Group(deleted, 4) + Group(phantom, 5);

            var (ids, format) = DrydockSystem.ReadDriftIds(yaml);
            var verdict = DrydockDrift.Detect(ids, table, Known, format, DrydockDrift.EngineWindow,
                DrydockFormat.Current, DrydockDrift.DrydockWindow);

            Assert.Multiple(() =>
            {
                Assert.That(verdict.Renamed, Is.EqualTo(new[] { new DrydockRename(rename.Key, rename.Value) }));
                Assert.That(verdict.Deleted, Is.EqualTo(new[] { deleted }));
                Assert.That(verdict.Unresolved, Is.EqualTo(new[] { phantom }));
                Assert.That(verdict.IsRefusal, Is.True);
                Assert.That(verdict.EngineFormatOutOfWindow, Is.False);
            });

            var rebaked = DrydockDocumentRebake.Transform(yaml, table, DrydockFormat.Current);
            var (rebakedIds, _) = DrydockSystem.ReadDriftIds(rebaked.Yaml);
            var after = DrydockDrift.Detect(rebakedIds, table, Known, format, DrydockDrift.EngineWindow,
                DrydockFormat.Current, DrydockDrift.DrydockWindow);

            Assert.Multiple(() =>
            {
                Assert.That(rebaked.AppliedRenames, Is.EqualTo(new[] { new DrydockRename(rename.Key, rename.Value) }));
                Assert.That(after.Renamed, Is.Empty, "The re-bake left a rename for the loader to do.");
                Assert.That(after.Deleted, Is.EqualTo(new[] { deleted }));
                Assert.That(after.Unresolved, Is.EqualTo(new[] { phantom }));
            });

            // The shape of the real table's rough edges. Dangling targets and ids both renamed and
            // deleted are reported only. Chains are asserted: the loader applies one rename per load,
            // but the re-bake sweep runs every boot, so a chain (A to B, B to C) would file a new
            // system revision each sweep, and a cycle would do it forever. Collapse a chain in the
            // mapping files (A straight to C) rather than loosening this.
            var chains = table.Renamed.Where(kv => table.Renamed.ContainsKey(kv.Value)).Select(kv => kv.Key).ToList();
            var dangling = table.Renamed.Count(kv => !Known(kv.Value) && !table.Deleted.Contains(kv.Value));
            var both = table.Renamed.Keys.Count(table.Deleted.Contains);
            TestContext.Out.WriteLine(
                $"renamed={table.Renamed.Count} deleted={table.Deleted.Count} chains={chains.Count} danglingTargets={dangling} renamedAndDeleted={both} eventExtraRenames={ev.RenamedPrototypes.Count - table.Renamed.Count}");

            Assert.That(chains, Is.Empty,
                "A migration rename points at an id that is itself renamed; the re-bake sweep would re-file those ships every boot.");

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task RealShipsReEmitByteForByteAndNeedNoReBake()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var resources = server.ResolveDependency<IResourceManager>();
            var mapLoader = server.System<MapLoaderSystem>();
            var map = await pair.CreateTestMap();

            var vessels = protoMan.EnumeratePrototypes<VesselPrototype>()
                .Where(v => !v.Abstract)
                .OrderBy(v => v.Price)
                .Where((_, i) => i % 15 == 0)
                .Take(3)
                .ToList();

            DrydockMigrationTable table = null!;
            await server.WaitPost(() => table = DrydockMigrationTable.Load(resources));

            var compared = 0;

            foreach (var vessel in vessels)
            {
                string? yaml = null;

                await server.WaitPost(() =>
                {
                    if (!mapLoader.TryLoadGrid(map.MapId, vessel.ShuttlePath, out var grid))
                        return;

                    using var writer = new StringWriter();
                    var options = new SerializationOptions { MissingEntityBehaviour = MissingEntityBehaviour.Ignore };
                    if (mapLoader.TrySaveGrid(grid!.Value.Owner, writer, options))
                        yaml = writer.ToString();

                    server.EntMan.DeleteEntity(grid!.Value.Owner);
                });

                await pair.RunTicksSync(2);

                if (yaml == null)
                    continue;

                using var reader = new StringReader(yaml);
                var root = (MappingDataNode) DataNodeParser.ParseYamlStream(reader).Single().Root;
                var result = DrydockDocumentRebake.Transform(yaml, table, DrydockFormat.Current);

                Assert.Multiple(() =>
                {
                    Assert.That(DrydockSystem.EmitDocument(root), Is.EqualTo(yaml),
                        $"{vessel.ID}: the engine's emitter does not reproduce its own document, so a re-bake would rewrite bytes it did not mean to.");
                    Assert.That(result.Yaml, Is.SameAs(yaml), $"{vessel.ID}: a freshly written ship was re-baked.");
                });

                compared++;
            }

            Assert.That(compared, Is.GreaterThan(0), "The control: no vessel loaded and saved, so nothing was compared.");
            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The <c>TriggerOnProximity.nextVisualUpdate</c> repair against the real serializer: what the old
        /// sentinel looks like in a document, that it reads back as a value and overflows the generated
        /// unpause handler, and that the repaired document reads back null and survives the same unpause.
        /// </summary>
        [Test]
        public async Task TheProximitySentinelRepairClearsTheUnpauseOverflow()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;

            var mapLoader = server.System<MapLoaderSystem>();
            var maps = server.System<SharedMapSystem>();
            var map = await pair.CreateTestMap();

            string yaml = null!;

            await server.WaitPost(() =>
            {
                var flasher = entMan.SpawnEntity("PortableFlasher", map.GridCoords);
                // What TriggerSystem.Proximity parked the field at before it became nullable.
                entMan.GetComponent<TriggerOnProximityComponent>(flasher).NextVisualUpdate = TimeSpan.MaxValue;

                using var writer = new StringWriter();
                Assert.That(mapLoader.TrySaveGrid(map.Grid.Owner, writer), Is.True);
                yaml = writer.ToString();
            });

            var match = NextVisualUpdateLine.Match(yaml);
            Assert.That(match.Success, Is.True, "The control: the sentinel was not written, so there is nothing to repair.");
            var seconds = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            TestContext.Out.WriteLine($"written: {match.Value}");
            Assert.That(seconds, Is.GreaterThan(DrydockDocumentRebake.ProximitySentinelSeconds));

            var repaired = DrydockDocumentRebake.Transform(yaml, DrydockMigrationTable.Empty, DrydockFormat.Current);
            Assert.That(repaired.AppliedSteps, Is.EqualTo(new[] { DrydockDocumentRebake.ProximityVisualSentinel.Name }));

            var (storedValue, storedOverflow) = await LoadPauseUnpause(pair, yaml);
            var (repairedValue, repairedOverflow) = await LoadPauseUnpause(pair, repaired.Yaml);

            Assert.Multiple(() =>
            {
                Assert.That(storedValue, Is.Not.Null, "The control: the stored sentinel read back as null, so the repair fixes nothing.");
                Assert.That(storedOverflow, Is.TypeOf<OverflowException>(), "The control: the stored sentinel did not overflow on unpause.");
                Assert.That(repairedValue, Is.Null);
                Assert.That(repairedOverflow, Is.Null);
            });

            await pair.CleanReturnAsync();

            async Task<(TimeSpan? Value, Exception? Thrown)> LoadPauseUnpause(TestPair p, string document)
            {
                EntityUid mapUid = default;
                TimeSpan? value = null;
                Exception? thrown = null;

                await server.WaitPost(() =>
                {
                    mapUid = maps.CreateMap(out var mapId);
                    Assert.That(mapLoader.TryLoadGrid(mapId, new StringReader(document), "drydock/rebake-test", out var grid), Is.True);

                    var query = entMan.EntityQueryEnumerator<TriggerOnProximityComponent, TransformComponent>();
                    while (query.MoveNext(out _, out var trigger, out var xform))
                    {
                        if (xform.GridUid == grid!.Value.Owner)
                            value = trigger.NextVisualUpdate;
                    }

                    maps.SetPaused(mapUid, true);
                });

                await p.RunTicksSync(5);

                await server.WaitPost(() =>
                {
                    try
                    {
                        maps.SetPaused(mapUid, false);
                    }
                    catch (Exception e)
                    {
                        thrown = e;
                    }

                    entMan.DeleteEntity(mapUid);
                });

                await p.RunTicksSync(2);
                return (value, thrown);
            }
        }
    }
}
