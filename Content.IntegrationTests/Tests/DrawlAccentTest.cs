using Content.Server.Speech.Components;
using Content.Server.Speech.EntitySystems;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests;

// Triad: regression coverage for the collapsed Southern/Cowboy drawl engine. Asserts the deterministic
// behavior (word-list swaps + g-dropping drawl, tic placement, caps handling) using single-item tic
// pools with the probability forced, so there is no RNG flakiness.
[TestFixture]
[TestOf(typeof(DrawlAccentSystem))]
public sealed class DrawlAccentTest
{
    [Test]
    public async Task DrawlSwapsAndDrops()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var sysMan = server.ResolveDependency<IEntitySystemManager>();

        await server.WaitAssertion(() =>
        {
            var drawl = sysMan.GetEntitySystem<DrawlAccentSystem>();

#pragma warning disable RA0002 // poke [Access]-gated tic config for the test
            // Tics off: isolate the deterministic word-list + g-dropping drawl.
            var cowboyNoTics = new CowboyAccentComponent { PrefixProb = 0f, SuffixProb = 0f };
            var southernNoTics = new SouthernAccentComponent { PrefixProb = 0f, SuffixProb = 0f };

            // Single-item pools with the tic forced on, to assert placement deterministically.
            var cowboySuffix = new CowboyAccentComponent
            {
                PrefixProb = 0f,
                SuffixProb = 1f,
                Suffixes = new() { "accent-cowboy-suffix-1" }, // ", partner"
            };
            var cowboyPrefix = new CowboyAccentComponent
            {
                PrefixProb = 1f,
                SuffixProb = 0f,
                Prefixes = new() { "accent-cowboy-prefix-1" }, // "Yee-haw,"
            };
#pragma warning restore RA0002

            // Word-list swap + g-drop: running -> skedaddling -> skedaddlin', arrest -> lasso, thief -> rustler.
            var cowboyLine = drawl.Drawl("I am running to arrest the thief", cowboyNoTics);
            Assert.That(cowboyLine, Does.Contain("skedaddlin'"));
            Assert.That(cowboyLine, Does.Contain("lasso"));
            Assert.That(cowboyLine, Does.Contain("rustler"));

            // Southern g-dropping: running -> runnin', and -> an', fighting -> fightin'.
            var southernLine = drawl.Drawl("running and fighting", southernNoTics);
            Assert.That(southernLine, Is.EqualTo("runnin' an' fightin'"));

            // The drawl monophthong: standalone "I" -> "Ah" (but the contraction "I'm" is left alone).
            Assert.That(drawl.Drawl("I", southernNoTics), Is.EqualTo("Ah"));
            // Sentence-initial "And" is caught now (the old case-sensitive rule missed it).
            Assert.That(drawl.Drawl("And then", southernNoTics), Does.StartWith("An' "));

            // Short non-gerund -ing nouns are spared; real gerunds still drop.
            Assert.That(drawl.Drawl("the king is running", southernNoTics), Is.EqualTo("the king is runnin'"));
            Assert.That(drawl.Drawl("a stinging wing", southernNoTics), Is.EqualTo("a stingin' wing"));

            // An "I'm"/"I'd" contraction after a prefix keeps its capital, not lowercased to "i'm".
            Assert.That(drawl.Drawl("I'm thinking", cowboyPrefix), Does.StartWith("Yee-haw, I'm"));

            // a/an re-agrees after a swap flips the following word's vowel-sound.
            Assert.That(drawl.Drawl("there is a nukie", cowboyNoTics), Does.Contain("an outlaw"));
            Assert.That(drawl.Drawl("there is an alien", cowboyNoTics), Does.Contain("a space critter"));
            Assert.That(drawl.Drawl("there is an alien", cowboyNoTics), Does.Not.Contain("an space"));
            // ...but correct articles on un-swapped words are left alone (exception sets).
            Assert.That(drawl.Drawl("an honest marshal", southernNoTics), Does.Contain("an honest"));
            Assert.That(drawl.Drawl("a university", southernNoTics), Does.StartWith("a university"));

            // Suffix slots in BEFORE the terminal punctuation, not after it.
            var suffixed = drawl.Drawl("you are under arrest!", cowboySuffix);
            Assert.That(suffixed, Does.EndWith("!"));
            Assert.That(suffixed, Does.Contain(", partner"));
            Assert.That(suffixed, Does.Not.Contain("!,"));

            // A lone leading "I" is not a shout: the prefix stays mixed-case.
            var notShout = drawl.Drawl("I think so", cowboyPrefix);
            Assert.That(notShout, Does.StartWith("Yee-haw,"));
            Assert.That(notShout, Does.Not.StartWith("YEE-HAW"));

            // A genuine shout DOES carry to the prefix.
            var shout = drawl.Drawl("STOP RIGHT THERE", cowboyPrefix);
            Assert.That(shout, Does.StartWith("YEE-HAW,"));
        });

        await pair.CleanReturnAsync();
    }
}
