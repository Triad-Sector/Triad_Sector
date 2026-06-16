using Content.Server._NF.Speech.Components;
using Content.Server._NF.Speech.EntitySystems;
using Content.Server.Speech;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests;

// Triad: regression coverage for goblin's phonetics + the tics/a-an layered on via AccentHelpers.
// Tics off (deterministic).
[TestFixture]
[TestOf(typeof(GoblinAccentSystem))]
public sealed class GoblinAccentTest
{
    [Test]
    public async Task GoblinPhoneticsAndArticles()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var comp = entMan.AddComponent<GoblinAccentComponent>(uid);
#pragma warning disable RA0002
            comp.PrefixProb = 0f;
            comp.SuffixProb = 0f;
#pragma warning restore RA0002

            string Goblin(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // Core phonetics: the -> da (case-preserving, incl. sentence-initial "The"), -ing, self.
            Assert.That(Goblin("the"), Is.EqualTo("da"));
            Assert.That(Goblin("The"), Is.EqualTo("Da"));
            Assert.That(Goblin("THE"), Is.EqualTo("DA"));
            Assert.That(Goblin("running"), Does.Contain("runnin'"));
            Assert.That(Goblin("myself"), Does.Contain("sewf"));

            // h-dropping makes "a hell" sound vowel-initial -> a/an fixup gives the cockney "an 'ell".
            Assert.That(Goblin("a hell"), Is.EqualTo("an 'ell"));
        });

        await pair.CleanReturnAsync();
    }
}
