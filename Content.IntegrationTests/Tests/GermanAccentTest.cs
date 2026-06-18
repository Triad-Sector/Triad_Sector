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

            // th -> z (German has no /th/): "that" -> "zat", "with" stacks w->v then th->z -> "viz".
            Assert.That(Ger("that"), Is.EqualTo("zat"));
            Assert.That(Ger("with"), Is.EqualTo("viz"));

            // w -> v on consonantal w, but never the "ow/aw/ew" vowel digraphs ("how" keeps its w).
            Assert.That(Ger("water"), Is.EqualTo("vater"));
            Assert.That(Ger("how"), Is.EqualTo("how"));
            // Elongated chat w-runs are spared (w after w), so "owwww" isn't amplified to "owvvv".
            Assert.That(Ger("owwww"), Is.EqualTo("owwww"));

            // v -> f, and it does NOT eat the v that w->v just produced ("water" stayed "vater").
            Assert.That(Ger("very"), Is.EqualTo("fery"));
            Assert.That(Ger("have"), Is.EqualTo("hafe"));

            // Final-obstruent devoicing: g->k (so -ing -> -ink), and d->t on a non-swapped word.
            Assert.That(Ger("singing"), Is.EqualTo("singink"));
            Assert.That(Ger("cold"), Is.EqualTo("colt"));

            // Population vocab swaps mined from prod chat. "good"/"now" are now vocab (gut/jetzt), not the
            // old phonetic forms (goot/now); these run before the phonetics so nothing re-mangles them.
            Assert.That(Ger("good"), Is.EqualTo("gut"));
            Assert.That(Ger("now"), Is.EqualTo("jetzt"));
            Assert.That(Ger("is"), Is.EqualTo("ist"));
            Assert.That(Ger("for"), Is.EqualTo("für"));
            Assert.That(Ger("but"), Is.EqualTo("aber"));
            Assert.That(Ger("please"), Is.EqualTo("bitte"));
            Assert.That(Ger("never"), Is.EqualTo("niemals"));
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
