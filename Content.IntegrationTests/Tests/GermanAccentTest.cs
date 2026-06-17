using Content.Server.Speech;
using Content.Server.Speech.Components;
using Content.Server.Speech.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests;

// Triad: regression coverage for Terra Germanic (renamed + enriched from German). Tics + das + umlaut
// probs forced for determinism.
[TestFixture]
[TestOf(typeof(GermanAccentSystem))]
public sealed class GermanAccentTest
{
    [Test]
    public async Task TerraGermanicSwapsPhonetics()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var comp = entMan.AddComponent<GermanAccentComponent>(uid);
#pragma warning disable RA0002
            comp.PrefixProb = 0f;
            comp.SuffixProb = 0f;
            comp.DasProb = 0f;     // no the->das, so "the" deterministically swaps to "ze"
            comp.UmlautProb = 0f;  // no random umlauts polluting the assertions
#pragma warning restore RA0002

            string Ger(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // Vocab swaps (the existing 62-entry German list, now under the terragermanic key).
            Assert.That(Ger("yes"), Is.EqualTo("ja"));
            Assert.That(Ger("no"), Is.EqualTo("nein"));
            Assert.That(Ger("idiot"), Is.EqualTo("dummkopf"));

            // "the" -> "ze" when das is disabled.
            Assert.That(Ger("the"), Is.EqualTo("ze"));

            // th -> zh phonetic on a word-initial th ("that" -> "zhat").
            Assert.That(Ger("that"), Is.EqualTo("zhat"));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TerraGermanicTheBecomesDas()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var comp = entMan.AddComponent<GermanAccentComponent>(uid);
#pragma warning disable RA0002
            comp.PrefixProb = 0f;
            comp.SuffixProb = 0f;
            comp.DasProb = 1f;     // force the->das
            comp.UmlautProb = 0f;
#pragma warning restore RA0002

            string Ger(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // With das forced, "the" becomes "das" (the char-shift preserves the lowercase).
            Assert.That(Ger("the"), Is.EqualTo("das"));
        });

        await pair.CleanReturnAsync();
    }
}
