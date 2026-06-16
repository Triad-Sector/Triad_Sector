using Content.Server._DV.Speech.Components;
using Content.Server._DV.Speech.EntitySystems;
using Content.Server.Speech;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests;

// Triad: regression coverage for scottish after promotion (word list + g-drop + helpers). Tics off.
[TestFixture]
[TestOf(typeof(ScottishAccentSystem))]
public sealed class ScottishAccentTest
{
    [Test]
    public async Task ScottishSwapsAndDrops()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var comp = entMan.AddComponent<ScottishAccentComponent>(uid);
#pragma warning disable RA0002
            comp.PrefixProb = 0f;
            comp.SuffixProb = 0f;
#pragma warning restore RA0002

            string Scot(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // Word swaps (idiot -> eejit, officer -> bobby with the -> tha).
            Assert.That(Scot("idiot"), Is.EqualTo("eejit"));
            Assert.That(Scot("the officer"), Does.Contain("bobby"));

            // Newly-added systematic g-drop, with the shared keep-list sparing king.
            Assert.That(Scot("running"), Does.Contain("runnin'"));
            Assert.That(Scot("king"), Is.EqualTo("king"));
        });

        await pair.CleanReturnAsync();
    }
}
