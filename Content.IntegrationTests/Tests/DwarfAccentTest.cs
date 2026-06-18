using Content.Server._DV.Speech.Components;
using Content.Server._DV.Speech.EntitySystems;
using Content.Server.Speech;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests;

// Triad: regression coverage for the Dwarven Brogue (merged Scottish + Dwarf). Tics off (deterministic).
[TestFixture]
[TestOf(typeof(DwarfAccentSystem))]
public sealed class DwarfAccentTest
{
    [Test]
    public async Task DwarfBrogueSwapsDropsGlottal()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var comp = entMan.AddComponent<DwarfAccentComponent>(uid);
#pragma warning disable RA0002
            comp.PrefixProb = 0f;
            comp.SuffixProb = 0f;
#pragma warning restore RA0002

            string Dwarf(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // Merged word list (idiot -> eejit, officer -> bobby with the -> tha).
            Assert.That(Dwarf("idiot"), Is.EqualTo("eejit"));
            Assert.That(Dwarf("the officer"), Does.Contain("bobby"));

            // g-drop with the keep-list, and the new ou->oo vowel-shift vocab.
            Assert.That(Dwarf("running"), Does.Contain("runnin'"));
            Assert.That(Dwarf("king"), Is.EqualTo("king"));
            Assert.That(Dwarf("house"), Is.EqualTo("hoose"));

            // Glottal stop on intervocalic t, guarded against -tion (so "nation" is left alone).
            Assert.That(Dwarf("water"), Is.EqualTo("wa'er"));
            Assert.That(Dwarf("butter"), Is.EqualTo("bu'er"));
            Assert.That(Dwarf("nation"), Is.EqualTo("nation"));

            // Scots ch-velar (-ight -> -icht) and vocalised L (word-final -all -> -aw).
            Assert.That(Dwarf("night"), Is.EqualTo("nicht"));
            Assert.That(Dwarf("all"), Is.EqualTo("aw"));
            Assert.That(Dwarf("call"), Is.EqualTo("caw"));

            // Population vocab swaps from prod chat. The apostrophe in "an'" blocks FixArticles (it keys on
            // an article token followed by whitespace), and none of these hit g-drop/glottal/-ight/-all.
            Assert.That(Dwarf("and"), Is.EqualTo("an'"));
            Assert.That(Dwarf("just"), Is.EqualTo("jus'"));
            Assert.That(Dwarf("your"), Is.EqualTo("yer"));
            Assert.That(Dwarf("good"), Is.EqualTo("guid"));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DwarfSlight()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var comp = entMan.AddComponent<DwarfAccentComponent>(uid);
#pragma warning disable RA0002
            comp.Strength = AccentStrength.Slight;
            comp.PrefixProb = 0f;
            comp.SuffixProb = 0f;
            comp.SlightChance = 1f;
#pragma warning restore RA0002

            string Dwarf(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // g-drop kept (readable); glottal kept at chance 1.
            Assert.That(Dwarf("running"), Does.Contain("runnin'"));
            Assert.That(Dwarf("water"), Is.EqualTo("wa'er"));
            // -ight and -all passes excluded in slight (stay intelligible).
            Assert.That(Dwarf("night"), Is.EqualTo("night"));
            Assert.That(Dwarf("all"), Is.EqualTo("all"));
            // Iconic swap kept; bulk vowel-shift entries dropped from the slim list, so "house" stays.
            Assert.That(Dwarf("yes"), Is.EqualTo("aye"));
            Assert.That(Dwarf("house"), Is.EqualTo("house"));

            // glottal off at chance 0.
#pragma warning disable RA0002
            comp.SlightChance = 0f;
#pragma warning restore RA0002
            Assert.That(Dwarf("water"), Is.EqualTo("water"));
        });

        await pair.CleanReturnAsync();
    }
}
