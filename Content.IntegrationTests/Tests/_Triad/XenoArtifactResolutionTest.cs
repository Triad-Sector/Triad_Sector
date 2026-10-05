// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Generic;
using System.Linq;
using Content.Shared.EntityTable.EntitySelectors;
using Content.Shared.Xenoarchaeology.Artifact;
using Content.Shared.Xenoarchaeology.Artifact.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad;

/// <summary>
/// Triad: end-to-end coverage of the artifact loop. One test fires every effect the tables can
/// roll; the other solves whole artifacts node by node through the real unlock machinery. The
/// pool fails a pair on any [ERRO], so an effect that throws or logs an error fails these even
/// without a dedicated assert. The unlock test also pins the invariant that an unlock session
/// always ends: the unlocking component surviving past its window is the runaway popup loop.
/// </summary>
[TestFixture]
public sealed class XenoArtifactResolutionTest
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: TriadTestEffectArtifact
  name: artifact
  components:
  - type: XenoArtifact
    isGenerationRequired: false

# The test map is a bare plating grid in vacuum, where the pressure and cold triggers
# satisfy themselves ambiently and keep opening unlock sessions under the test's feet.
# This roster is every trigger that stays inert without a deliberate act.
- type: weightedRandomXenoArchTrigger
  id: TriadTestTriggers
  weights:
    TriggerMusic: 1
    TriggerHeat: 1
    TriggerWater: 1
    TriggerRadiation: 1
    TriggerExamine: 1
    TriggerBruteDamage: 1
    TriggerWrenching: 1
    TriggerPrying: 1
    TriggerScrewing: 1
    TriggerPulsing: 1
    TriggerBlood: 1
    TriggerDeath: 1
    TriggerMagnet: 1
    TriggerConsumeKnowledge: 1
    TriggerConsumeCarbs: 1
    TriggerConsumeMeat: 1
    TriggerConsumeProduce: 1
    TriggerInteractStamp: 1
    TriggerInteractShock: 1
    TriggerAttackStaminaDamage: 1
    TriggerAttackLaser: 1

# Real generation (severity curve included), but unlock windows shortened so a solve
# is ~15 ticks per node instead of ~300.
- type: entity
  id: TriadTestSolveArtifact
  parent: ComplexXenoArtifact
  suffix: Test
  components:
  - type: XenoArtifact
    triggerWeights: TriadTestTriggers
    unlockStateDuration: 0.5
    unlockStateIncrementPerNode: 0
    unlockStateRefractory: 0.05

- type: entity
  id: TriadTestSolveArtifactItem
  parent: ComplexXenoArtifactItem
  suffix: Test
  components:
  - type: XenoArtifact
    triggerWeights: TriadTestTriggers
    unlockStateDuration: 0.5
    unlockStateIncrementPerNode: 0
    unlockStateRefractory: 0.05
";

    /// <summary>
    /// The default test grid is a single tile; effects that spawn-and-anchor around the artifact
    /// (anomaly injectors and the like) then land off-grid and error. Plate out a real floor.
    /// </summary>
    private static async Task PlateTestGrid(Pair.TestPair pair, Robust.UnitTesting.Pool.TestMapData testMap, int radius = 4)
    {
        var server = pair.Server;
        var tileMan = server.ResolveDependency<Robust.Shared.Map.ITileDefinitionManager>();
        var mapSystem = server.System<SharedMapSystem>();
        await server.WaitPost(() =>
        {
            var plating = new Robust.Shared.Map.Tile(tileMan["Plating"].TileId);
            for (var x = -radius; x <= radius; x++)
            {
                for (var y = -radius; y <= radius; y++)
                {
                    mapSystem.SetTile(testMap.Grid, new Robust.Shared.Maths.Vector2i(x, y), plating);
                }
            }
        });
        await server.WaitRunTicks(1);
    }

    /// <summary>
    /// Every effect reachable through the three effect tables activates without erroring.
    /// Each effect gets a fresh artifact so one effect's aftermath cannot mask another's.
    /// </summary>
    [Test]
    [Retry(2)] // heisentest: an effect spawner can anchor against a dying grid (#586); drop when fixed
    public async Task EveryTableEffectResolves()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        await PlateTestGrid(pair, testMap);

        var entManager = server.ResolveDependency<IEntityManager>();
        var artifactSystem = entManager.System<SharedXenoArtifactSystem>();

        var effectIds = new List<EntProtoId>();
        await server.WaitPost(() =>
        {
            var flat = new List<(EntProtoId Id, float Weight)>();
            foreach (var tableId in new[]
                     {
                         "XenoArtifactEffectsDefaultTable",
                         "XenoArtifactEffectsHandheldOnlyTable",
                         "XenoArtifactEffectsStructureOnlyTable",
                     })
            {
                artifactSystem.FlattenEffectTable(new NestedSelector { TableId = tableId }, 1f, flat);
            }

            effectIds = flat.Select(f => f.Id).Distinct().OrderBy(id => id.Id).ToList();
        });

        // A collapsed table walk would pass vacuously; the roster is ~70 strong.
        Assert.That(effectIds, Has.Count.GreaterThanOrEqualTo(50),
            "effect table flatten returned implausibly few entries");

        foreach (var effectId in effectIds)
        {
            await server.WaitPost(() =>
            {
                var artifact = entManager.SpawnAtPosition("TriadTestEffectArtifact", testMap.GridCoords);
                Entity<XenoArtifactComponent> artifactEnt = (artifact, entManager.GetComponent<XenoArtifactComponent>(artifact));

                Assert.That(artifactSystem.AddNode((artifact, artifactEnt.Comp), effectId, out var node, dirty: false),
                    $"failed to add node for effect {effectId}");

                var coords = entManager.GetComponent<TransformComponent>(artifact).Coordinates;
                Assert.That(
                    artifactSystem.ActivateNode(artifactEnt, node!.Value, null, null, coords, consumeDurability: false),
                    $"activation returned false for effect {effectId}");
            });

            // let deferred work (spawners, explosions, applied components) run its first ticks
            await server.WaitRunTicks(3);
        }

        await server.WaitRunTicks(15);
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Solves complete artifacts node by node through the genuine trigger -> unlock-session ->
    /// finish -> free-activation pipeline, in both forms. Asserts per node that the unlock
    /// session ends and the node comes out unlocked and worth points, and per artifact that
    /// the full solve is reached.
    /// </summary>
    [Test]
    [TestCase("TriadTestSolveArtifact")]
    [TestCase("TriadTestSolveArtifactItem")]
    public async Task FullSolveThroughUnlockSessions(string prototype)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        await PlateTestGrid(pair, testMap);

        var entManager = server.ResolveDependency<IEntityManager>();
        var artifactSystem = entManager.System<SharedXenoArtifactSystem>();

        for (var run = 0; run < 2; run++)
        {
            EntityUid artifact = default;
            var nodeCount = 0;
            await server.WaitPost(() =>
            {
                artifact = entManager.SpawnAtPosition(prototype, testMap.GridCoords);
                var comp = entManager.GetComponent<XenoArtifactComponent>(artifact);
                nodeCount = artifactSystem.GetAllNodes((artifact, comp)).Count();
            });
            Assert.That(nodeCount, Is.GreaterThan(0), "artifact generated no nodes");

            // Solve in depth order. Each pass targets one node whose predecessors are all
            // unlocked, triggers it plus every ancestor inside one window, and waits the
            // window out. The ancestor set makes the required-index check an exact match,
            // so the unlock is deterministic.
            for (var solved = 0; solved < nodeCount; solved++)
            {
                EntityUid targetNode = default;
                var found = false;
                await server.WaitPost(() =>
                {
                    var comp = entManager.GetComponent<XenoArtifactComponent>(artifact);
                    var candidate = artifactSystem.GetAllNodes((artifact, comp))
                        .Where(n => n.Comp.Locked && artifactSystem.HasUnlockedPredecessor((artifact, comp), n))
                        .OrderBy(n => n.Comp.Depth)
                        .FirstOrDefault();
                    if (candidate == default)
                        return;

                    found = true;
                    targetNode = candidate;

                    artifactSystem.TriggerXenoArtifact((artifact, comp), (candidate.Owner, candidate.Comp), force: true);
                    foreach (var ancestorIdx in artifactSystem.GetPredecessorNodes((artifact, comp), artifactSystem.GetIndex((artifact, comp), candidate)))
                    {
                        var ancestor = artifactSystem.GetNode((artifact, comp), ancestorIdx);
                        artifactSystem.TriggerXenoArtifact((artifact, comp), (ancestor.Owner, ancestor.Comp), force: true);
                    }
                });

                Assert.That(found, Is.True,
                    $"no unlockable node left after {solved}/{nodeCount} solves -- graph has an unreachable node");

                // The window is 0.5 seconds; poll until the session ends rather than assuming a
                // tickrate, then require it to be over. A session that survives the whole bound
                // is the runaway finish loop.
                for (var tick = 0; tick < 120; tick += 5)
                {
                    await server.WaitRunTicks(5);
                    var open = false;
                    await server.WaitPost(() => open = entManager.HasComponent<XenoArtifactUnlockingComponent>(artifact));
                    if (!open)
                        break;
                }

                await server.WaitPost(() =>
                {
                    Assert.That(entManager.HasComponent<XenoArtifactUnlockingComponent>(artifact), Is.False,
                        "unlocking session survived past its window -- runaway finish loop");
                    var nodeComp = entManager.GetComponent<XenoArtifactNodeComponent>(targetNode);
                    Assert.That(nodeComp.Locked, Is.False, "targeted node did not unlock");
                    Assert.That(nodeComp.ResearchValue, Is.GreaterThan(0), "unlocked node is worth zero points");
                });

                // refractory (0.05s) between sessions
                await server.WaitRunTicks(3);
            }

            await server.WaitPost(() =>
            {
                var comp = entManager.GetComponent<XenoArtifactComponent>(artifact);
                var locked = artifactSystem.GetAllNodes((artifact, comp)).Count(n => n.Comp.Locked);
                Assert.That(locked, Is.EqualTo(0), "artifact not fully solved");
                Assert.That(artifactSystem.GetCompletionMultiplier((artifact, comp)),
                    Is.EqualTo(SharedXenoArtifactSystem.FullSolveMultiplier),
                    "full solve did not reach the full-solve multiplier");
            });
        }

        await server.WaitRunTicks(15);
        await pair.CleanReturnAsync();
    }
}
