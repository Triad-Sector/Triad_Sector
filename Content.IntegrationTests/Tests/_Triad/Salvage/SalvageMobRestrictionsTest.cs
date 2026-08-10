// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server._NF.Salvage;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Triad.Salvage;

/// <summary>
/// Regression test for the stale uid in SalvageMobRestrictionsGridComponent.MobsToKill. OnRemoveGrid
/// used MetaData(target), which throws outright on an entity that no longer exists, and entries can
/// go stale because OnRemove only drops a mob while its LinkedGridEntity still resolves. Because the
/// throw escaped the foreach, every mob after the stale one was skipped.
/// </summary>
[TestFixture]
[TestOf(typeof(SalvageMobRestrictionsSystem))]
public sealed class SalvageMobRestrictionsTest
{
    /// <summary>
    /// A dead uid in the kill list must not throw the component removal.
    /// </summary>
    [Test]
    public async Task StaleMobUid_DoesNotThrowOnGridRemoval()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var grid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var doomed = entMan.SpawnEntity(null, MapCoordinates.Nullspace);

            var restrictions = entMan.AddComponent<SalvageMobRestrictionsGridComponent>(grid);
            restrictions.MobsToKill.Add(doomed);

            // The mob dies without OnRemove getting a chance to drop it from the set, which is what
            // happens when its LinkedGridEntity no longer resolves.
            entMan.DeleteEntity(doomed);

            Assert.DoesNotThrow(
                () => entMan.RemoveComponent<SalvageMobRestrictionsGridComponent>(grid),
                "a stale uid in MobsToKill must not throw out of component removal");

            entMan.DeleteEntity(grid);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The consequence that actually mattered: the throw aborted the loop, so live mobs queued behind
    /// a stale entry were silently never processed.
    /// </summary>
    [Test]
    public async Task StaleMobUid_DoesNotStopLaterMobsBeingProcessed()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var grid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var stale = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var live = entMan.SpawnEntity(null, MapCoordinates.Nullspace);

            var restrictions = entMan.AddComponent<SalvageMobRestrictionsGridComponent>(grid);

            // Order matters: the stale entry has to be walked before the live one for this to prove
            // the loop survives it. HashSet order is not guaranteed, so assert on the outcome for
            // the live mob rather than on iteration order.
            restrictions.MobsToKill.Add(stale);
            restrictions.MobsToKill.Add(live);

            entMan.DeleteEntity(stale);

            entMan.RemoveComponent<SalvageMobRestrictionsGridComponent>(grid);

            // The live mob has no BodyComponent, so it takes the explode-and-delete branch.
            Assert.That(entMan.Deleted(live) || entMan.IsQueuedForDeletion(live), Is.True,
                "a live mob sharing the kill list with a stale uid must still be processed");

            entMan.DeleteEntity(grid);
        });

        await pair.CleanReturnAsync();
    }
}
