// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server._Triad.Speech.Components;
using Content.Server._Triad.Speech.EntitySystems;
using Content.Server.Speech;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests;

// Triad: regression coverage for the New Brooklyn everyman borough accent. Tics off for the swaps.
[TestFixture]
[TestOf(typeof(NewBrooklynAccentSystem))]
public sealed class NewBrooklynAccentTest
{
    [Test]
    public async Task NewBrooklynSwapsAndPhonetics()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var comp = entMan.AddComponent<NewBrooklynAccentComponent>(uid);
#pragma warning disable RA0002
            comp.PrefixProb = 0f;
            comp.SuffixProb = 0f;
#pragma warning restore RA0002

            string Brk(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // Everyman borough vocab swaps.
            Assert.That(Brk("the"), Is.EqualTo("da"));
            Assert.That(Brk("you"), Is.EqualTo("yous"));
            Assert.That(Brk("here"), Is.EqualTo("heeyah"));
            Assert.That(Brk("friend"), Is.EqualTo("buddy"));
            Assert.That(Brk("coffee"), Is.EqualTo("a regulah"));

            // Borough phonetics: -ing -> -in', or -> uh, ar -> ah (mid-word).
            Assert.That(Brk("thinking"), Is.EqualTo("thinkin'"));
            Assert.That(Brk("forget"), Is.EqualTo("fuhget"));
            Assert.That(Brk("target"), Is.EqualTo("tahget"));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task NewBrooklynSuffixTic()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var comp = entMan.AddComponent<NewBrooklynAccentComponent>(uid);
#pragma warning disable RA0002
            comp.PrefixProb = 0f;
            comp.SuffixProb = 1f;
            comp.Suffixes = new() { "accent-newbrooklyn-suffix-3" }; // ", ya know?"
#pragma warning restore RA0002

            string Brk(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // Forced single-item suffix pool: the tic lands before the sentence's terminal punctuation.
            Assert.That(Brk("we run da block"), Does.Contain("ya know?"));
        });

        await pair.CleanReturnAsync();
    }
}
