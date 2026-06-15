// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Reflection;
using Content.Server._DV.Traits;
using Content.Shared._DV.CCVars;
using Content.Shared._DV.Traits;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Mono;

/// <summary>
/// Triad: the global trait COUNT cap (traits.max_count) is removed when set to 0 (or any non-positive
/// value), leaving per-category dials and the point budget as the only limits.
/// </summary>
[TestFixture]
[TestOf(typeof(TraitSystem))]
public sealed class TraitCountCapTest
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: traitCategory
  id: TestCountCapCategory
  name: trait-blindness-name
  maxTraits: null
  maxPoints: null

- type: trait
  id: TestCountCapA
  name: trait-blindness-name
  description: trait-blindness-desc
  category: TestCountCapCategory
  cost: 0

- type: trait
  id: TestCountCapB
  name: trait-blindness-name
  description: trait-blindness-desc
  category: TestCountCapCategory
  cost: 0

- type: trait
  id: TestCountCapC
  name: trait-blindness-name
  description: trait-blindness-desc
  category: TestCountCapCategory
  cost: 0
";

    /// <summary>
    /// With max_count set to 0 (unlimited), selecting more traits than a small positive cap would allow
    /// still validates all of them.
    /// </summary>
    [Test]
    public async Task ZeroMaxCount_MeansUnlimited()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();

        await server.WaitAssertion(() =>
        {
            cfg.SetCVar(DCCVars.MaxTraitCount, 0); // unlimited

            var player = entMan.SpawnEntity(null, MapCoordinates.Nullspace);

            var selected = new HashSet<ProtoId<TraitPrototype>>
            {
                "TestCountCapA",
                "TestCountCapB",
                "TestCountCapC",
            };

            var traitSys = entMan.System<TraitSystem>();
            var method = typeof(TraitSystem).GetMethod("ValidateTraits",
                BindingFlags.NonPublic | BindingFlags.Instance);

            var disabled = new Dictionary<ProtoId<TraitPrototype>, List<string>>();
            var valid = (List<TraitPrototype>?)method?.Invoke(traitSys,
                new object?[] { player, selected, null, null, null, null, disabled });

            Assert.Multiple(() =>
            {
                Assert.That(valid?.Count, Is.EqualTo(3),
                    "All traits should validate when max_count is 0 (unlimited)");
                Assert.That(disabled, Is.Empty,
                    "No trait should be rejected for the count limit when unlimited");
            });

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }
}
