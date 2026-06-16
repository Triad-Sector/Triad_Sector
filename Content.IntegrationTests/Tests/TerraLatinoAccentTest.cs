using Content.Server.Speech;
using Content.Server.Speech.Components;
using Content.Server.Speech.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests;

// Triad: regression coverage for Terra Latino (renamed + enriched from Spanish). Tics off (deterministic).
[TestFixture]
[TestOf(typeof(TerraLatinoAccentSystem))]
public sealed class TerraLatinoAccentTest
{
    [Test]
    public async Task TerraLatinoSwapsPhoneticsPunctuation()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var comp = entMan.AddComponent<TerraLatinoAccentComponent>(uid);
#pragma warning disable RA0002
            comp.PrefixProb = 0f;
            comp.SuffixProb = 0f;
#pragma warning restore RA0002

            string Latino(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // Vocab swaps (kept recognizable loanwords). "good" has no leading s, so no es-insertion.
            Assert.That(Latino("good"), Is.EqualTo("bueno"));
            Assert.That(Latino("friend"), Is.EqualTo("amigo"));
            Assert.That(Latino("very good"), Is.EqualTo("muy bueno"));

            // Signature phonetic: insert an accented é before a word-initial English S (epenthetic vowel,
            // reads as "eh-station" not a typo'd "estation").
            Assert.That(Latino("station"), Is.EqualTo("éstation"));
            Assert.That(Latino("the station"), Is.EqualTo("the éstation"));
            // A capitalized S-word moves the capital to the é: "Stop" -> "Éstop", never "ÉStop".
            Assert.That(Latino("Stop"), Is.EqualTo("Éstop"));

            // ...but the es-insertion must NOT re-prefix our own Spanish swaps. "yes"->"si" stays "si"
            // (not "esi"); "sir"->"señor" stays "señor" (not "eseñor"). This is the skip-set guard.
            Assert.That(Latino("yes"), Is.EqualTo("si"));
            Assert.That(Latino("sir"), Is.EqualTo("señor"));

            // Inverted opening punctuation on ?/!.
            Assert.That(Latino("what?"), Does.StartWith("¿"));
            Assert.That(Latino("help!"), Does.StartWith("¡"));

            // 18+ swap is input-gated (fires only on the literal word).
            Assert.That(Latino("damn"), Is.EqualTo("carajo"));
        });

        await pair.CleanReturnAsync();
    }
}
