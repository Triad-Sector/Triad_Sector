// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Shuttles.Systems;
using Content.Shared.Shuttles.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Triad.Shuttles;

/// <summary>
/// Regression tests for the dangling FTL visualizer reference. The visualizer is spawned attached to
/// the shuttle's FTL target, so when that target grid is removed mid-flight the visualizer dies as its
/// child and none of the explicit QueueDel/null sites run. FTLComponent.VisualizerEntity is an
/// AutoNetworkedField, so the dead uid was then re-serialized by PVS on every state send, logging a
/// resolve error with a full stack capture on the game state hot path.
/// </summary>
[TestFixture]
[TestOf(typeof(ShuttleSystem))]
public sealed class FtlVisualizerTest
{
    /// <summary>
    /// The case that produced the errors: the visualizer dies without going through FTL teardown.
    /// </summary>
    [Test]
    public async Task VisualizerDeletion_ClearsShuttleReference()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var shuttle = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var visualizer = entMan.SpawnEntity(null, MapCoordinates.Nullspace);

            var ftl = entMan.AddComponent<FTLComponent>(shuttle);
            var visuals = entMan.AddComponent<FtlVisualizerComponent>(visualizer);
            visuals.Grid = shuttle;
            ftl.VisualizerEntity = visualizer;

            // Stands in for the target grid being removed out from under it.
            entMan.DeleteEntity(visualizer);

            Assert.That(ftl.VisualizerEntity, Is.Null,
                "a visualizer that dies on its own must clear the shuttle's reference to it");

            entMan.DeleteEntity(shuttle);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A shuttle can outlive one visualizer and spawn another. The late death of the old one must not
    /// clear the reference to its replacement.
    /// </summary>
    [Test]
    public async Task StaleVisualizerDeletion_LeavesCurrentReferenceAlone()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var shuttle = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var stale = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var current = entMan.SpawnEntity(null, MapCoordinates.Nullspace);

            var ftl = entMan.AddComponent<FTLComponent>(shuttle);
            entMan.AddComponent<FtlVisualizerComponent>(stale).Grid = shuttle;
            entMan.AddComponent<FtlVisualizerComponent>(current).Grid = shuttle;

            ftl.VisualizerEntity = current;

            entMan.DeleteEntity(stale);

            Assert.That(ftl.VisualizerEntity, Is.EqualTo(current),
                "only the visualizer the shuttle actually points at may clear the reference");

            entMan.DeleteEntity(current);
            entMan.DeleteEntity(shuttle);
        });

        await pair.CleanReturnAsync();
    }
}
