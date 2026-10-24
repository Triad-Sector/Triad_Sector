#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._Triad.Drydock;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using YamlDotNet.RepresentationModel;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// Holds the drift fingerprint still while the way it is read changes.
    ///
    /// <para>The fingerprint and format version are written onto every revision and compared across
    /// stores to decide whether a stored ship names content that has moved. That makes the exact
    /// value a persisted contract rather than an implementation detail: a read that returns a
    /// different fingerprint for the same document would read as drift on ships nobody touched, and
    /// the ladder that heals drift would start work it does not need to do.</para>
    ///
    /// <para>So when the read was changed from loading the whole document into a node tree to
    /// streaming it, this test was the condition of the change. The oracle below is the node-tree
    /// read exactly as it was, kept here so the two can be compared on real ship documents rather
    /// than on a fixture that happens to be shaped conveniently.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockDriftMetadataTest
    {
        [Test]
        public async Task TheStreamingReadMatchesTheNodeTreeReadOnRealShips()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapLoader = server.System<MapLoaderSystem>();

            var map = await pair.CreateTestMap();

            // A spread rather than one hull: the streaming read has to agree about documents with
            // different prototype sets, and a single ship would not catch a mistake in how a group
            // with no proto, or a nested instance list, is skipped.
            var vessels = protoMan.EnumeratePrototypes<VesselPrototype>()
                .Where(v => !v.Abstract)
                .OrderBy(v => v.Price)
                .Where((_, i) => i % 9 == 0)
                .Take(8)
                .ToList();

            Assert.That(vessels, Is.Not.Empty, "The control: no vessels resolved, so this compares nothing.");

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

                var streamed = DrydockSystem.ReadDriftMetadata(yaml);
                var tree = NodeTreeOracle(yaml);

                Assert.That(streamed.FormatVersion, Is.EqualTo(tree.FormatVersion),
                    $"{vessel.ID}: format version disagrees.");
                Assert.That(streamed.Fingerprint, Is.EqualTo(tree.Fingerprint),
                    $"{vessel.ID}: fingerprint disagrees, so stored revisions would read as drifted.");

                compared++;
            }

            Assert.That(compared, Is.GreaterThan(0),
                "The control: every vessel failed to load or serialize, so nothing was actually compared.");

            // Without this the pair dirty-returns, which NUnit reports as a SKIP rather than a pass,
            // and a control that skips is a control that is not running.
            await pair.CleanReturnAsync();
        }

        [Test]
        public void TheStreamingReadSurvivesDocumentsItShouldNotChokeOn()
        {
            // Each of these is a shape the node-tree read tolerated by filtering rather than by
            // throwing, so the streaming read has to tolerate it too.
            var cases = new Dictionary<string, string>
            {
                ["no meta and no entities"] = "other: 1\n",
                ["meta is not a mapping"] = "meta: 3\nentities: []\n",
                ["entities is not a sequence"] = "meta:\n  format: 7\nentities: nope\n",
                ["a group with no proto"] = "meta:\n  format: 7\nentities:\n- entities:\n  - uid: 1\n",
                ["a group that is not a mapping"] = "meta:\n  format: 7\nentities:\n- - 1\n",
                ["an empty proto"] = "meta:\n  format: 7\nentities:\n- proto: \"\"\n  entities: []\n",
                ["nested instance data"] = "meta:\n  format: 7\nentities:\n- proto: Foo\n  entities:\n  - uid: 1\n    components:\n    - type: Transform\n      pos: 1,2\n",
            };

            foreach (var (name, yaml) in cases)
            {
                var streamed = DrydockSystem.ReadDriftMetadata(yaml);
                var tree = NodeTreeOracle(yaml);

                Assert.That(streamed.FormatVersion, Is.EqualTo(tree.FormatVersion), $"{name}: format version.");
                Assert.That(streamed.Fingerprint, Is.EqualTo(tree.Fingerprint), $"{name}: fingerprint.");
            }
        }

        /// <summary>
        /// A/B of the two reads over one real document, with the store pipeline taken out of the
        /// picture. The pipeline's own run-to-run noise is far wider than this phase, so measuring
        /// the change through a store cannot resolve it; this can. Reports rather than asserts,
        /// because a timing assertion on a shared machine is a flaky test.
        /// </summary>
        [Test]
        public async Task CompareDriftReadCost()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapLoader = server.System<MapLoaderSystem>();
            var map = await pair.CreateTestMap();

            // The biggest hull available, because the whole question is how the read scales with
            // document size.
            var vessel = protoMan.EnumeratePrototypes<VesselPrototype>()
                .Where(v => !v.Abstract)
                .OrderByDescending(v => v.Price)
                .First();

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
            Assert.That(yaml, Is.Not.Null, "The control: no document, so nothing was measured.");

            const int warm = 3;
            const int runs = 15;

            for (var i = 0; i < warm; i++)
            {
                DrydockSystem.ReadDriftMetadata(yaml!);
                NodeTreeOracle(yaml!);
            }

            var streamed = new List<double>();
            var tree = new List<double>();

            // Interleaved, so a drift in machine load lands on both rather than on whichever ran second.
            for (var i = 0; i < runs; i++)
            {
                var w = System.Diagnostics.Stopwatch.StartNew();
                DrydockSystem.ReadDriftMetadata(yaml!);
                w.Stop();
                streamed.Add(w.Elapsed.TotalMilliseconds);

                w = System.Diagnostics.Stopwatch.StartNew();
                NodeTreeOracle(yaml!);
                w.Stop();
                tree.Add(w.Elapsed.TotalMilliseconds);
            }

            streamed.Sort();
            tree.Sort();

            TestContext.Out.WriteLine($"vessel={vessel.ID} documentKB={yaml!.Length / 1024}");
            TestContext.Out.WriteLine($"streaming  median {streamed[runs / 2]:F1} ms  min {streamed[0]:F1}  max {streamed[^1]:F1}");
            TestContext.Out.WriteLine($"node tree  median {tree[runs / 2]:F1} ms  min {tree[0]:F1}  max {tree[^1]:F1}");
            TestContext.Out.WriteLine($"ratio {tree[runs / 2] / streamed[runs / 2]:F2}x");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The read exactly as it was before it streamed, kept as the comparison target. Do not
        /// "optimize" this: its whole job is to be the slow, obvious implementation.
        /// </summary>
        private static (byte[] Fingerprint, int FormatVersion) NodeTreeOracle(string yaml)
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));

            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
                return (SHA256.HashData(Encoding.UTF8.GetBytes(string.Empty)), 0);

            var formatVer = 0;
            if (root.Children.TryGetValue(new YamlScalarNode("meta"), out var metaNode)
                && metaNode is YamlMappingNode meta
                && meta.Children.TryGetValue(new YamlScalarNode("format"), out var format))
            {
                int.TryParse(((YamlScalarNode) format).Value, out formatVer);
            }

            var protos = new SortedSet<string>(StringComparer.Ordinal);
            if (root.Children.TryGetValue(new YamlScalarNode("entities"), out var entitiesNode)
                && entitiesNode is YamlSequenceNode entities)
            {
                foreach (var entry in entities.Children.OfType<YamlMappingNode>())
                {
                    if (entry.Children.TryGetValue(new YamlScalarNode("proto"), out var proto)
                        && ((YamlScalarNode) proto).Value is { Length: > 0 } protoId)
                    {
                        protos.Add(protoId);
                    }
                }
            }

            return (SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', protos))), formatVer);
        }
    }
}
