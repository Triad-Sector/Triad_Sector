using Content.Server._NF.Speech.Components;
using Content.Server._NF.Speech.EntitySystems;
using Content.Server.Speech;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests;

// Triad: regression coverage for streetpunk after the pipeline reorder (swaps-first) + shared helpers.
// Tics off (deterministic).
[TestFixture]
[TestOf(typeof(StreetpunkAccentSystem))]
public sealed class StreetpunkAccentTest
{
    [Test]
    public async Task StreetpunkSwapsDropsAndArticles()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var comp = entMan.AddComponent<StreetpunkAccentComponent>(uid);
#pragma warning disable RA0002
            comp.PrefixProb = 0f;
            comp.SuffixProb = 0f;
#pragma warning restore RA0002

            string Punk(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // g-drop with the shared keep-list: running -> runnin', but king stays king.
            Assert.That(Punk("running"), Does.Contain("runnin'"));
            Assert.That(Punk("king"), Is.EqualTo("king"));

            // Order fix: word swaps now fire (officer -> badge). Was dead under the old phonetics-first order.
            Assert.That(Punk("the officer"), Does.Contain("badge"));

            // a/an re-agrees after a swap flips the vowel-sound (officer -> badge: "an officer" -> "a badge").
            var swapped = Punk("an officer");
            Assert.That(swapped, Does.Contain("a badge"));
            Assert.That(swapped, Does.Not.Contain("an badge"));
        });

        await pair.CleanReturnAsync();
    }
}
