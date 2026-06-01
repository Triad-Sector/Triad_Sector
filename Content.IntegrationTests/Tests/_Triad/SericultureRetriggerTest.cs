using System.Linq;
using Content.Shared.DoAfter;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Sericulture;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._Triad;

/// <summary>
/// Regression test for the Bionic Spinarette / Sericulture interrupt. The weave action's short
/// cooldown lets a player re-trigger it before the production DoAfter finishes. Re-triggering must
/// leave the in-flight weave running rather than cancelling it, otherwise the weave is interrupted
/// every time the player re-clicks and no silk is ever produced.
/// </summary>
[TestFixture]
public sealed class SericultureRetriggerTest
{
    [Test]
    public async Task RetriggerDoesNotCancelWeave()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var hunger = server.System<HungerSystem>();

        var map = await pair.CreateTestMap();

        var mob = EntityUid.Invalid;
        await server.WaitPost(() =>
        {
            // MobArachnid carries Sericulture natively (same component the BionicSpinarette trait grants).
            mob = entMan.SpawnEntity("MobArachnid", map.GridCoords);

            // Keep the weaver well-fed so the hunger gate never short-circuits the weave.
            if (entMan.TryGetComponent<HungerComponent>(mob, out var hungerComp))
                hunger.SetHunger(mob, 200f, hungerComp);
        });

        // First trigger starts a weave.
        await server.WaitAssertion(() =>
        {
            entMan.EventBus.RaiseLocalEvent(mob, new SericultureActionEvent());
            Assert.That(RunningWeaves(entMan, mob), Is.EqualTo(1), "the weave should start on first trigger");
        });

        // Re-trigger mid-weave (an impatient re-click before the production DoAfter completes).
        await server.WaitAssertion(() =>
        {
            entMan.EventBus.RaiseLocalEvent(mob, new SericultureActionEvent());
            Assert.That(RunningWeaves(entMan, mob), Is.EqualTo(1),
                "re-triggering must leave the in-flight weave running, not cancel it");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Counts DoAfters on the mob that are still in progress (neither cancelled nor completed).
    /// </summary>
    private static int RunningWeaves(IEntityManager entMan, EntityUid mob)
    {
        if (!entMan.TryGetComponent<DoAfterComponent>(mob, out var doAfters))
            return 0;

        return doAfters.DoAfters.Values.Count(d => d.CancelledTime == null && !d.Completed);
    }
}
