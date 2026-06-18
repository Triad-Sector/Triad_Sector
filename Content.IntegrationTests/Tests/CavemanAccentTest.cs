using Content.Server._NF.Speech.Components;
using Content.Server._NF.Speech.EntitySystems;
using Content.Server.Speech;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests;

// Triad: regression coverage for the newly-wired caveman filler-word drop (the/is/to/a/am...).
[TestFixture]
[TestOf(typeof(CavemanAccentSystem))]
public sealed class CavemanAccentTest
{
    [Test]
    public async Task CavemanDropsFillerWords()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            entMan.AddComponent<CavemanAccentComponent>(uid);

            string Caveman(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // All-filler input drops to nothing, then falls back to a grunt (never empty, no "the").
            Assert.That(Caveman("the the the"), Is.Not.Empty);
            Assert.That(Caveman("the the the"), Does.Not.Contain("the").IgnoreCase);

            // Filler words are dropped from a real sentence; the content words stay.
            var dropped = Caveman("you are good");
            Assert.That(dropped, Does.Not.Contain("are").IgnoreCase);
            Assert.That(dropped, Does.Contain("you").IgnoreCase);

            // Prepositions/conjunctions are dropped too: "you and me" -> "you me".
            Assert.That(Caveman("you and me"), Does.Not.Contain("and").IgnoreCase);
        });

        await pair.CleanReturnAsync();
    }
}
