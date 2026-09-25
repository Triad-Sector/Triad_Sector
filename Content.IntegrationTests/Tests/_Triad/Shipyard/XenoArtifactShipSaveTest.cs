#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Content.Server._HL.Shipyard;
using Content.Server._Triad.Shipyard;
using Content.Shared.Xenoarchaeology.Artifact;
using Content.Shared.Xenoarchaeology.Artifact.Components;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.UnitTesting.Pool;

namespace Content.IntegrationTests.Tests._Triad.Shipyard;

/// <summary>
/// An artifact's nodes are entities in its node container, and its graph names them by NetEntity. The
/// legacy ship save has to carry both halves with the nodes' wear intact, and a ship file that lost the
/// nodes has to load without leaving an unlock session that throws on every tick.
/// </summary>
[TestFixture]
[TestOf(typeof(ShipyardGridSaveSystem))]
[TestOf(typeof(ShipSaveYamlSanitizer))]
public sealed class XenoArtifactShipSaveTest
{
    private const string ArtifactProtoId = "ComplexXenoArtifact";

    [Test]
    public async Task AnAnchoredArtifactKeepsItsGraphThroughAShipSave()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var saveSystem = entMan.System<ShipyardGridSaveSystem>();
        var mapLoader = entMan.System<MapLoaderSystem>();
        var artifactSystem = entMan.System<SharedXenoArtifactSystem>();

        var map = await pair.CreateTestMap();
        var artifact = await SpawnAnchoredArtifact(server, map);

        var lockedBefore = new Dictionary<int, bool>();
        var durabilityBefore = new Dictionary<int, (int Current, int Max)>();
        List<List<bool>> edgesBefore = new();
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<XenoArtifactComponent>(artifact);

            // One unlocked node, spent, so the round trip has progress and wear to lose.
            var root = artifactSystem.GetAllNodes((artifact, comp))
                .First(n => artifactSystem.GetDirectPredecessorNodes((artifact, comp), n).Count == 0);
            artifactSystem.SetNodeUnlocked((artifact, comp), root);
            artifactSystem.AdjustNodeDurability((root.Owner, root.Comp), -root.Comp.Durability);

            foreach (var index in artifactSystem.GetAllNodeIndices((artifact, comp)))
            {
                var node = artifactSystem.GetNode((artifact, comp), index);
                lockedBefore[index] = node.Comp.Locked;
                durabilityBefore[index] = (node.Comp.Durability, node.Comp.MaxDurability);
            }

            edgesBefore = comp.NodeAdjacencyMatrix.Select(row => row.ToList()).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(lockedBefore.Values, Has.Some.False, "Fixture: no node was unlocked before the save.");
                Assert.That(durabilityBefore.Values.Any(d => d.Current == 0), Is.True,
                    "Fixture: no node was spent before the save.");
            });
        });

        string? yaml = null;
        await server.WaitAssertion(() =>
        {
            Assert.That(saveSystem.TryBuildShipSaveYaml(map.Grid.Owner, out yaml, out _), Is.True,
                "The ship save threw or refused.");
        });

        var scrubbedYaml = ShipSaveYamlSanitizer.ScrubShipLoadYaml(yaml!, out var scrubbed);
        Assert.That(scrubbed, Is.EqualTo(0), "A file the current save wrote left the load scrub something to remove.");

        var loaded = await LoadShipYaml(server, mapLoader, scrubbedYaml, map);

        await server.WaitAssertion(() =>
        {
            var loadedArtifacts = loaded.Entities.Where(e => entMan.HasComponent<XenoArtifactComponent>(e)).ToList();
            Assert.That(loadedArtifacts, Has.Count.EqualTo(1), "Exactly one artifact should come back.");

            var uid = loadedArtifacts[0];
            var comp = entMan.GetComponent<XenoArtifactComponent>(uid);
            var indices = artifactSystem.GetAllNodeIndices((uid, comp)).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(indices, Is.EquivalentTo(lockedBefore.Keys), "The graph came back with different slots.");

                foreach (var index in indices)
                {
                    Assert.That(artifactSystem.TryGetNode((uid, comp), index, out var node), Is.True,
                        $"Slot {index} names a node that did not load.");

                    if (node == null)
                        continue;

                    Assert.That(node.Value.Comp.Locked, Is.EqualTo(lockedBefore[index]),
                        $"Slot {index} came back with a different lock state.");
                    Assert.That((node.Value.Comp.Durability, node.Value.Comp.MaxDurability), Is.EqualTo(durabilityBefore[index]),
                        $"Slot {index} came back with different durability.");
                }

                Assert.That(comp.NodeAdjacencyMatrix, Is.EqualTo(edgesBefore), "The graph came back with different edges.");
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AnArtifactSavedWithoutItsNodesLoadsWithAFreshGraphAndNoUnlockSession()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var saveSystem = entMan.System<ShipyardGridSaveSystem>();
        var mapLoader = entMan.System<MapLoaderSystem>();
        var artifactSystem = entMan.System<SharedXenoArtifactSystem>();

        var map = await pair.CreateTestMap();
        var artifact = await SpawnAnchoredArtifact(server, map);

        // The shape a legacy ship file carries: the nodes deleted, the graph still naming them, and an
        // unlock session that is already due. One call, so no tick runs the due session in between.
        string? yaml = null;
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<XenoArtifactComponent>(artifact);
            var unlocking = entMan.EnsureComponent<XenoArtifactUnlockingComponent>(artifact);
            unlocking.TriggeredNodeIndexes.Add(artifactSystem.GetAllNodeIndices((artifact, comp)).First());
            unlocking.EndTime = server.Timing.CurTime;

            foreach (var node in artifactSystem.GetAllNodes((artifact, comp)).ToList())
                entMan.DeleteEntity(node);

            Assert.Multiple(() =>
            {
                Assert.That(comp.NodeVertices, Has.Some.Not.Null,
                    "Fixture: deleting the nodes emptied the graph, so this is not the legacy file shape.");
                Assert.That(artifactSystem.GetAllNodes((artifact, comp)), Is.Empty,
                    "Fixture: a node survived the delete.");
            });

            Assert.That(saveSystem.TryBuildShipSaveYaml(map.Grid.Owner, out yaml, out _), Is.True,
                "The ship save threw or refused.");
            Assert.That(yaml, Does.Contain("XenoArtifactUnlocking"), "Fixture: the unlock session was not saved.");

            // A saved ship leaves the world, and this one's session would throw on the next tick.
            entMan.DeleteEntity(artifact);
        });

        var scrubbedYaml = ShipSaveYamlSanitizer.ScrubShipLoadYaml(yaml!, out var scrubbed);
        Assert.That(scrubbed, Is.GreaterThan(0), "The load scrub left an artifact graph naming missing nodes.");

        var loaded = await LoadShipYaml(server, mapLoader, scrubbedYaml, map);
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var loadedArtifacts = loaded.Entities.Where(e => entMan.HasComponent<XenoArtifactComponent>(e)).ToList();
            Assert.That(loadedArtifacts, Has.Count.EqualTo(1), "Exactly one artifact should come back.");

            var uid = loadedArtifacts[0];
            var comp = entMan.GetComponent<XenoArtifactComponent>(uid);
            var indices = artifactSystem.GetAllNodeIndices((uid, comp)).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(entMan.HasComponent<XenoArtifactUnlockingComponent>(uid), Is.False,
                    "The unlock session from the file survived the load.");
                Assert.That(comp.IsGenerationRequired, Is.False, "The artifact still owes a graph.");
                Assert.That(indices, Is.Not.Empty, "The artifact came back with no graph.");

                foreach (var index in indices)
                {
                    Assert.That(artifactSystem.TryGetNode((uid, comp), index, out _), Is.True,
                        $"Slot {index} names a node that does not exist.");
                }
            });
        });

        await pair.CleanReturnAsync();
    }

    private static async Task<EntityUid> SpawnAnchoredArtifact(
        Robust.UnitTesting.RobustIntegrationTest.ServerIntegrationInstance server,
        TestMapData map)
    {
        var entMan = server.EntMan;
        var transform = entMan.System<SharedTransformSystem>();
        var artifactSystem = entMan.System<SharedXenoArtifactSystem>();

        EntityUid artifact = default;
        await server.WaitPost(() =>
        {
            artifact = entMan.SpawnEntity(ArtifactProtoId, map.GridCoords);
            transform.AnchorEntity(artifact);
        });

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<XenoArtifactComponent>(artifact);
            Assert.Multiple(() =>
            {
                Assert.That(entMan.GetComponent<TransformComponent>(artifact).Anchored, Is.True,
                    "Fixture: the artifact must be anchored, or the purge deletes it whole.");
                Assert.That(artifactSystem.GetAllNodes((artifact, comp)), Is.Not.Empty,
                    "Fixture: the artifact generated no graph.");
            });
        });

        return artifact;
    }

    private static async Task<LoadResult> LoadShipYaml(
        Robust.UnitTesting.RobustIntegrationTest.ServerIntegrationInstance server,
        MapLoaderSystem mapLoader,
        string yaml,
        TestMapData map)
    {
        LoadResult? loaded = null;
        await server.WaitAssertion(() =>
        {
            // Merged onto the live test map, offset so the loaded grid does not sit on the original.
            var opts = new MapLoadOptions { MergeMap = map.MapId, Offset = new Vector2(100, 100) };
            Assert.That(mapLoader.TryLoadGeneric(new StringReader(yaml), "xenoarch-ship-save-test", out loaded, opts), Is.True,
                "The saved YAML did not load back.");
        });

        return loaded!;
    }
}
