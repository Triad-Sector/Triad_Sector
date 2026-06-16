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
        });

        await pair.CleanReturnAsync();
    }
}
