using Content.Server.Speech;
using Content.Server.Speech.Components;
using Content.Server.Speech.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests;

// Triad: regression coverage for the lizard hiss after adding the soft-c rule. Baseline species accent.
[TestFixture]
[TestOf(typeof(LizardAccentSystem))]
public sealed class LizardAccentTest
{
    [Test]
    public async Task LizardHisses()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            entMan.AddComponent<LizardAccentComponent>(uid);

            string Liz(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // Core hiss: s -> sss.
            Assert.That(Liz("yes"), Is.EqualTo("yesss"));

            // Soft c (before e/i/y) now hisses too, lengthened by the s-pass.
            Assert.That(Liz("city"), Is.EqualTo("sssity"));
            Assert.That(Liz("nice"), Is.EqualTo("nissse"));

            // Hard c (before a/o/u/consonant) is a /k/ sound and is left alone.
            Assert.That(Liz("cat"), Is.EqualTo("cat"));
            Assert.That(Liz("clone"), Is.EqualTo("clone"));

            // x still becomes ks (exit -> ekssit).
            Assert.That(Liz("exit"), Does.Contain("kss"));
        });

        await pair.CleanReturnAsync();
    }
}
