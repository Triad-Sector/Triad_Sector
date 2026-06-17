using Content.Server.Speech;
using Content.Server.Speech.Components;
using Content.Server.Speech.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests;

// Triad: regression coverage for Terra Gallic (renamed + enriched from French). Tics off (deterministic).
[TestFixture]
[TestOf(typeof(FrenchAccentSystem))]
public sealed class FrenchAccentTest
{
    [Test]
    public async Task TerraGallicSwapsPhoneticsPunctuation()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var comp = entMan.AddComponent<FrenchAccentComponent>(uid);
#pragma warning disable RA0002
            comp.PrefixProb = 0f;
            comp.SuffixProb = 0f;
#pragma warning restore RA0002

            string Gal(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // Vocab swaps now fire (the ApplyReplacements key was dead/empty before this enrichment).
            Assert.That(Gal("yes"), Is.EqualTo("oui"));
            Assert.That(Gal("no"), Is.EqualTo("non"));
            Assert.That(Gal("friend"), Is.EqualTo("ami"));
            // A swap supersedes the h-drop: "hello" -> "bonjour" (not "'ello").
            Assert.That(Gal("hello"), Is.EqualTo("bonjour"));

            // Phonetic: th -> 'z. "the" has no vocab swap here, so it phoneticizes to "'ze".
            Assert.That(Gal("the"), Is.EqualTo("'ze"));
            // Word-initial h -> ' on a word with no swap.
            Assert.That(Gal("here"), Does.StartWith("'"));

            // French-style spacing inserted before terminal ? / !.
            Assert.That(Gal("what?"), Does.Contain(" ?"));

            // 18+ swap is input-gated.
            Assert.That(Gal("fuck"), Is.EqualTo("putain"));
        });

        await pair.CleanReturnAsync();
    }
}
