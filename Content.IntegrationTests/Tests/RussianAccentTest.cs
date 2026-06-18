using Content.Server.Speech;
using Content.Server.Speech.Components;
using Content.Server.Speech.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests;

// Triad: regression coverage for Terra Slavic (renamed + enriched from Russian). Tics off (deterministic).
[TestFixture]
[TestOf(typeof(RussianAccentSystem))]
public sealed class RussianAccentTest
{
    [Test]
    public async Task TerraSlavicSwapsDropArticlesCyrillic()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var comp = entMan.AddComponent<RussianAccentComponent>(uid);
#pragma warning disable RA0002
            comp.PrefixProb = 0f;
            comp.SuffixProb = 0f;
            comp.ArticleDropProb = 1f; // force deterministic article-drop for the assertions below
            comp.CopulaDropProb = 1f;  // force deterministic copula-drop
#pragma warning restore RA0002

            string Slav(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // Recognizable loanword swaps survive (kept per the rename brief). "da"/"nyet" cyrillicize:
            // 'd','a' are not in the glyph map but 'n','y','t' are, so assert on the post-Cyrillic form.
            Assert.That(Slav("yes"), Is.EqualTo("da"));         // d,a untouched by the glyph map
            Assert.That(Slav("friend"), Is.EqualTo("coмяade")); // comrade: m->м, r->я
            Assert.That(Slav("hello"), Is.EqualTo("pяivyeт"));  // privyet: r->я, t->т

            // Article-drop is the signature phonetic: the/a/an vanish.
            Assert.That(Slav("pass the wrench"), Does.Not.Contain("the"));
            Assert.That(Slav("pass the wrench"), Does.Not.Contain("тne")); // not cyrillicized-"the" either
            Assert.That(Slav("a bit"), Is.EqualTo("вiт"));      // "a " dropped, then bit -> вiт

            // Sentence-initial article hands its capital to the next word.
            Assert.That(Slav("The captain"), Does.StartWith("C")); // "The " dropped, Captain stays capital

            // Copula-drop is the other Slavic cue: "he is big" -> "he big" (is/are/am... vanish).
            Assert.That(Slav("he is big"), Does.Not.Contain("is"));

            // Faux-Cyrillic glyph swap on a word with no vocab/article hit.
            Assert.That(Slav("work"), Is.EqualTo("яaвoтa"));   // work -> rabota -> я,в,т glyphs

            // AppendSuffix contract: a suffix carrying its own end punctuation (", da?") supersedes the
            // sentence's, so "me." + ", da?" -> "me, da?", never the "me, da?." double-punct seam.
            Assert.That(AccentHelpers.AppendSuffix("listen to me.", ", da?"), Is.EqualTo("listen to me, da?"));
            Assert.That(AccentHelpers.AppendSuffix("go now", ", da?"), Is.EqualTo("go now, da?"));
            // A punctuation-free suffix still preserves the sentence's terminal mark (unchanged behavior).
            Assert.That(AccentHelpers.AppendSuffix("rustler!", ", comrade"), Is.EqualTo("rustler, comrade!"));
        });

        await pair.CleanReturnAsync();
    }
}
