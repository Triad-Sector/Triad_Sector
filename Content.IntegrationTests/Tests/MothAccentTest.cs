using Content.Server.Speech;
using Content.Server.Speech.Components;
using Content.Server.Speech.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests;

// Triad: regression coverage for the moth buzz + the new fluttery wingbeat tic. Baseline species accent.
[TestFixture]
[TestOf(typeof(MothAccentSystem))]
public sealed class MothAccentTest
{
    [Test]
    public async Task MothBuzzesAndFlutters()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var comp = entMan.AddComponent<MothAccentComponent>(uid);
#pragma warning disable RA0002
            comp.FlutterChance = 0f; // flutter off: isolate the deterministic buzz
#pragma warning restore RA0002

            string Moth(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // Buzz: z -> zzz.
            Assert.That(Moth("buzz"), Is.EqualTo("buzzz"));
            // A z-less line is unchanged when the flutter is off.
            Assert.That(Moth("hello there"), Is.EqualTo("hello there"));

            // Force the flutter on with a single-item pool: it lands before terminal punctuation.
#pragma warning disable RA0002
            comp.FlutterChance = 1f;
            comp.Flutters = new() { "accent-moth-flutter-1" }; // ", bzzt"
#pragma warning restore RA0002
            Assert.That(Moth("hello there"), Does.Contain("bzzt"));
        });

        await pair.CleanReturnAsync();
    }
}
