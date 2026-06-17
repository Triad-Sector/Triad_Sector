using Content.Server.Speech;
using Content.Server.Speech.Components;
using Content.Server.Speech.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests;

// Triad: regression coverage for the enriched Pirate accent (display name "Freeport Corsair"). Tics off
// for the deterministic swaps.
[TestFixture]
[TestOf(typeof(PirateAccentSystem))]
public sealed class PirateAccentTest
{
    [Test]
    public async Task PirateSwapsAndGdrop()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var comp = entMan.AddComponent<PirateAccentComponent>(uid);
#pragma warning disable RA0002
            comp.PrefixProb = 0f;
            comp.SuffixProb = 0f;
#pragma warning restore RA0002

            string Pir(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // Buccaneer vocab swaps.
            Assert.That(Pir("you"), Is.EqualTo("ye"));
            Assert.That(Pir("yes"), Is.EqualTo("aye"));
            Assert.That(Pir("friend"), Is.EqualTo("matey"));
            Assert.That(Pir("captain"), Is.EqualTo("cap'n"));
            Assert.That(Pir("treasure"), Is.EqualTo("booty"));

            // Salty g-drop (keep-list spares king).
            Assert.That(Pir("sailing"), Is.EqualTo("sailin'"));
            Assert.That(Pir("king"), Is.EqualTo("king"));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PirateSuffixTic()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var comp = entMan.AddComponent<PirateAccentComponent>(uid);
#pragma warning disable RA0002
            comp.PrefixProb = 0f;
            comp.SuffixProb = 1f;
            comp.Suffixes = new() { "accent-pirate-suffix-1" }; // ", savvy?"
#pragma warning restore RA0002

            string Pir(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // Self-punctuating suffix lands cleanly (AppendSuffix handles the trailing '?').
            Assert.That(Pir("we sail at dawn"), Does.Contain("savvy?"));
        });

        await pair.CleanReturnAsync();
    }
}
