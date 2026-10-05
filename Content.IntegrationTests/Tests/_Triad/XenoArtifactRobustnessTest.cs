#nullable enable

using System;
using System.Linq;
using Content.Shared.Xenoarchaeology.Artifact;
using Content.Shared.Xenoarchaeology.Artifact.Components;
using Robust.Shared.Audio;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._Triad;

/// <summary>
/// An artifact's graph can name a node that does not resolve, and the unlock and trigger passes run on
/// every tick. A miss on those paths is skipped, and a throw ends at the artifact that threw.
/// </summary>
[TestFixture]
[TestOf(typeof(SharedXenoArtifactSystem))]
public sealed class XenoArtifactRobustnessTest
{
    private const string ArtifactProtoId = "ComplexXenoArtifact";
    private const string LooseNodeProtoId = "XenoRobustnessLooseNode";

    [TestPrototypes]
    private const string Prototypes = $@"
- type: entity
  id: {LooseNodeProtoId}
  components:
  - type: XenoArtifactNode
  - type: XATTimer
    possibleDelayInSeconds:
      min: 60
      max: 60
";

    [Test]
    public async Task AnUnlockSessionWithANodeThatDoesNotResolveFinishesCleanly()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var artifactSystem = entMan.System<SharedXenoArtifactSystem>();

        var map = await pair.CreateTestMap();

        EntityUid artifact = default;
        await server.WaitAssertion(() =>
        {
            artifact = entMan.SpawnEntity(ArtifactProtoId, map.GridCoords);
            var comp = entMan.GetComponent<XenoArtifactComponent>(artifact);
            var nodes = artifactSystem.GetAllNodes((artifact, comp)).ToList();
            Assert.That(nodes, Has.Count.GreaterThan(1), "Fixture: the artifact needs two nodes.");

            // Suppressed, so no node can unlock: the session ends in failure and no effect fires. The
            // unlock pass still visits every slot before it asks whether a node can unlock.
            artifactSystem.SetSuppressed((artifact, comp), true);
            var unlocking = entMan.EnsureComponent<XenoArtifactUnlockingComponent>(artifact);
            unlocking.EndTime = server.Timing.CurTime;
            entMan.DeleteEntity(nodes[^1]);

            Assert.That(artifactSystem.GetAllNodeIndices((artifact, comp)).Count(),
                Is.GreaterThan(artifactSystem.GetAllNodes((artifact, comp)).Count()),
                "Fixture: the deleted node's slot should still be in the graph.");
        });

        await server.WaitRunTicks(3);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<XenoArtifactUnlockingComponent>(artifact), Is.False,
                "The session did not finish.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AnUnlockThatThrowsEndsItsSession()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var artifactSystem = entMan.System<SharedXenoArtifactSystem>();

        var map = await pair.CreateTestMap();

        EntityUid artifact = default;
        await server.WaitAssertion(() =>
        {
            artifact = entMan.SpawnEntity(ArtifactProtoId, map.GridCoords);

            // Suppressed, so the session takes the failure path, which plays this sound; resolving a
            // collection that does not exist throws.
            artifactSystem.SetSuppressed((artifact, entMan.GetComponent<XenoArtifactComponent>(artifact)), true);
            var unlocking = entMan.EnsureComponent<XenoArtifactUnlockingComponent>(artifact);
            unlocking.UnlockActivationFailedSound = new SoundCollectionSpecifier("XenoRobustnessMissingCollection");
            unlocking.EndTime = server.Timing.CurTime;
        });

        // The throw is logged as an error by design; only the session's end is under test.
        var failureLevel = pair.ServerLogHandler.FailureLevel;
        pair.ServerLogHandler.FailureLevel = null;
        try
        {
            await server.WaitRunTicks(5);
        }
        finally
        {
            pair.ServerLogHandler.FailureLevel = failureLevel;
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<XenoArtifactUnlockingComponent>(artifact), Is.False,
                "The session that threw is still running, so it throws again on every tick.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task GraphQueriesSkipANodeThatDoesNotResolve()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var artifactSystem = entMan.System<SharedXenoArtifactSystem>();

        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var artifact = entMan.SpawnEntity(ArtifactProtoId, map.GridCoords);
            var comp = entMan.GetComponent<XenoArtifactComponent>(artifact);

            var child = artifactSystem.GetAllNodes((artifact, comp))
                .First(n => artifactSystem.GetDirectPredecessorNodes((artifact, comp), n).Count > 0);
            var parent = artifactSystem.GetDirectPredecessorNodes((artifact, comp), child).First();
            entMan.DeleteEntity(parent);

            var loose = entMan.SpawnEntity(LooseNodeProtoId, map.GridCoords);
            var looseComp = entMan.GetComponent<XenoArtifactNodeComponent>(loose);
            var notANode = entMan.SpawnEntity(null, map.GridCoords);

            Assert.Multiple(() =>
            {
                foreach (var node in artifactSystem.GetAllNodes((artifact, comp)))
                {
                    Assert.That(() => artifactSystem.GetPredecessorNodes((artifact, comp), node), Throws.Nothing,
                        "GetPredecessorNodes threw on a graph with a slot that does not resolve.");
                    Assert.That(() => artifactSystem.GetSuccessorNodes((artifact, comp), node), Throws.Nothing,
                        "GetSuccessorNodes threw on a graph with a slot that does not resolve.");
                }

                Assert.That(artifactSystem.GetPredecessorNodes((artifact, comp), child).Select(n => n.Owner),
                    Does.Not.Contain(parent.Owner), "A deleted predecessor came back.");
                Assert.That(artifactSystem.NodeHasEdge((artifact, comp), (parent.Owner, null), (child.Owner, child.Comp)),
                    Is.False, "An edge from a deleted node was reported.");

                Assert.That(artifactSystem.GetPredecessorNodes((artifact, comp), (loose, looseComp)), Is.Empty,
                    "A node outside the graph was given predecessors.");
                Assert.That(artifactSystem.GetSuccessorNodes((artifact, comp), (loose, looseComp)), Is.Empty,
                    "A node outside the graph was given successors.");
                Assert.That(artifactSystem.NodeHasEdge((artifact, comp), (loose, looseComp), (child.Owner, child.Comp)),
                    Is.False, "A node outside the graph was given an edge.");

                Assert.That(artifactSystem.AddNode((artifact, comp), (notANode, null)), Is.False,
                    "AddNode accepted an entity that is not a node.");
                Assert.That(artifactSystem.RemoveNode((artifact, comp), (notANode, null)), Is.False,
                    "RemoveNode accepted an entity that is not a node.");
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ATriggerOnANodeItsArtifactDoesNotListIsSkipped()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var artifactSystem = entMan.System<SharedXenoArtifactSystem>();

        var map = await pair.CreateTestMap();

        XenoArtifactUnlockingComponent unlocking = default!;
        await server.WaitAssertion(() =>
        {
            var artifact = entMan.SpawnEntity(ArtifactProtoId, map.GridCoords);

            // Suppressed, so none of the artifact's own nodes can trigger and fill the session.
            artifactSystem.SetSuppressed((artifact, entMan.GetComponent<XenoArtifactComponent>(artifact)), true);

            // A node that names the artifact while the artifact does not list it, so the per-tick trigger
            // query meets it; the open session is what made that query look the node up.
            var loose = entMan.SpawnEntity(LooseNodeProtoId, map.GridCoords);
#pragma warning disable RA0002
            entMan.GetComponent<XenoArtifactNodeComponent>(loose).Attached = entMan.GetNetEntity(artifact);
#pragma warning restore RA0002

            unlocking = entMan.EnsureComponent<XenoArtifactUnlockingComponent>(artifact);
            unlocking.EndTime = server.Timing.CurTime + TimeSpan.FromMinutes(5);
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(unlocking.TriggeredNodeIndexes, Is.Empty, "A node outside the graph triggered the artifact.");
        });

        await pair.CleanReturnAsync();
    }
}
