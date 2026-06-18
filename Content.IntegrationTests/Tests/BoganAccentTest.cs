using Content.Server.Speech;
using Content.Server.Speech.Components;
using Content.Server.Speech.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests;

// Triad: regression coverage for the reworked Bogan accent on the shared AccentHelpers. Tics off
// (deterministic), so it asserts the word swaps + g-dropping drawl and the -ing keep-list.
[TestFixture]
[TestOf(typeof(BoganAccentSystem))]
public sealed class BoganAccentTest
{
    [Test]
    public async Task BoganSwapsAndDrops()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var comp = entMan.AddComponent<BoganAccentComponent>(uid);
#pragma warning disable RA0002 // tics off for deterministic assertions
            comp.PrefixProb = 0f;
            comp.SuffixProb = 0f;
#pragma warning restore RA0002

            string Bogan(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // Word swaps + g-drop (running -> runnin').
            Assert.That(Bogan("the doctor is running"), Is.EqualTo("the quack is runnin'"));
            // good -> "bloody ripper", then non-rhotic -er -> -a makes it the authentic "bloody rippa".
            Assert.That(Bogan("that is good"), Is.EqualTo("that is bloody rippa"));

            // The shared -ing keep-list still spares short non-gerund nouns.
            Assert.That(Bogan("the king is going"), Is.EqualTo("the king is goin'"));

            // Non-rhotic Aussie -er -> -a (two letters before "er", so "her"/"per" are spared).
            Assert.That(Bogan("computer"), Is.EqualTo("computa"));

            // prison -> clink reads in any context (no stranded article).
            Assert.That(Bogan("go to prison"), Is.EqualTo("go to clink"));
        });

        await pair.CleanReturnAsync();
    }
}
