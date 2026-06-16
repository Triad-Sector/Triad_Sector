// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server._Triad.Speech.Components;
using Content.Server._Triad.Speech.EntitySystems;
using Content.Server.Speech;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests;

// Triad: regression coverage for the Freeport Corsair buccaneer accent. Tics off for the deterministic swaps.
[TestFixture]
[TestOf(typeof(FreeportCorsairAccentSystem))]
public sealed class FreeportCorsairAccentTest
{
    [Test]
    public async Task FreeportCorsairSwapsAndGdrop()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var comp = entMan.AddComponent<FreeportCorsairAccentComponent>(uid);
#pragma warning disable RA0002
            comp.PrefixProb = 0f;
            comp.SuffixProb = 0f;
#pragma warning restore RA0002

            string Cor(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // Buccaneer vocab swaps.
            Assert.That(Cor("you"), Is.EqualTo("ye"));
            Assert.That(Cor("yes"), Is.EqualTo("aye"));
            Assert.That(Cor("friend"), Is.EqualTo("matey"));
            Assert.That(Cor("captain"), Is.EqualTo("cap'n"));
            Assert.That(Cor("treasure"), Is.EqualTo("booty"));

            // Salty g-drop (keep-list spares king).
            Assert.That(Cor("sailing"), Is.EqualTo("sailin'"));
            Assert.That(Cor("king"), Is.EqualTo("king"));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FreeportCorsairSuffixTic()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var comp = entMan.AddComponent<FreeportCorsairAccentComponent>(uid);
#pragma warning disable RA0002
            comp.PrefixProb = 0f;
            comp.SuffixProb = 1f;
            comp.Suffixes = new() { "accent-freeportcorsair-suffix-1" }; // ", savvy?"
#pragma warning restore RA0002

            string Cor(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            // Self-punctuating suffix lands cleanly (AppendSuffix handles the trailing '?').
            Assert.That(Cor("we sail at dawn"), Does.Contain("savvy?"));
        });

        await pair.CleanReturnAsync();
    }
}
