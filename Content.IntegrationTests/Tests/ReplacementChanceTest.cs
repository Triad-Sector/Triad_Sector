/*
 * Triad - This file is licensed under AGPLv3
 * Copyright (c) 2025 Triad Contributors
 * See AGPLv3.txt for details.
 */

using Content.Server.Speech.EntitySystems;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests;

// Triad: regression coverage for the per-word replacementChance dial on ReplacementAccentSystem. The
// chance is rolled per matching word (a smooth density dial), not once per message. Default 1f = always.
[TestFixture]
public sealed class ReplacementChanceTest
{
    // Reuse existing newbrooklyn loc keys (words-3 "the" -> replace-3 "da") so no test-only .ftl is
    // needed. Only replacementChance differs between the two test accents.
    [TestPrototypes]
    private const string Prototypes = @"
- type: accent
  id: TestChanceZero
  replacementChance: 0
  wordReplacements:
    accent-newbrooklyn-words-3: accent-newbrooklyn-words-replace-3

- type: accent
  id: TestChanceOne
  replacementChance: 1
  wordReplacements:
    accent-newbrooklyn-words-3: accent-newbrooklyn-words-replace-3
";

    [Test]
    public async Task ReplacementChanceGatesPerWord()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var replacement = server.ResolveDependency<IEntitySystemManager>().GetEntitySystem<ReplacementAccentSystem>();

        await server.WaitAssertion(() =>
        {
            // chance 1: "the" always swaps to "da".
            var one = replacement.ApplyReplacements("pass the wrench", "TestChanceOne");
            Assert.That(one, Is.EqualTo("pass da wrench"), "chance 1 must always replace");

            // chance 0: "the" never swaps, even across many attempts (per-word roll never passes).
            for (var i = 0; i < 50; i++)
            {
                var zero = replacement.ApplyReplacements("pass the wrench", "TestChanceZero");
                Assert.That(zero, Is.EqualTo("pass the wrench"), "chance 0 must never replace");
            }
        });

        await pair.CleanReturnAsync();
    }
}
