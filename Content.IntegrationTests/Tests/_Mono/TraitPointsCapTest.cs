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
/// Triad: the global trait POINTS pool (traits.max_points) is removed when set to 0 (or any non-positive value),
/// the same escape hatch the count cap already has. Points then allocate per-category via each category's MaxPoints,
/// instead of from one shared budget.
/// </summary>
[TestFixture]
[TestOf(typeof(TraitSystem))]
public sealed class TraitPointsCapTest
{
    // maxPoints: null on the category so the ONLY point limit in play is the global one under test. Costs are
    // positive and sum to 15, which would overflow any small positive global budget.
    [TestPrototypes]
    private const string Prototypes = @"
- type: traitCategory
  id: TestPointsCapCategory
  name: trait-blindness-name
  maxTraits: null
  maxPoints: null

- type: trait
  id: TestPointsCapA
  name: trait-blindness-name
  description: trait-blindness-desc
  category: TestPointsCapCategory
  cost: 5

- type: trait
  id: TestPointsCapB
  name: trait-blindness-name
  description: trait-blindness-desc
  category: TestPointsCapCategory
  cost: 5

- type: trait
  id: TestPointsCapC
  name: trait-blindness-name
  description: trait-blindness-desc
  category: TestPointsCapCategory
  cost: 5
";

    /// <summary>
    /// With max_points set to 0 (unlimited), selecting traits whose total cost (15) would blow a small positive
    /// budget still validates all of them. Count is also unlimited so only the points gate is exercised.
    /// </summary>
    [Test]
    public async Task ZeroMaxPoints_MeansUnlimited()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();

        await server.WaitAssertion(() =>
        {
            cfg.SetCVar(DCCVars.MaxTraitCount, 0); // unlimited count, isolate the points gate
            cfg.SetCVar(DCCVars.MaxTraitPoints, 0); // unlimited points

            var player = entMan.SpawnEntity(null, MapCoordinates.Nullspace);

            var selected = new HashSet<ProtoId<TraitPrototype>>
            {
                "TestPointsCapA",
                "TestPointsCapB",
                "TestPointsCapC",
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
                    "All traits should validate when max_points is 0 (unlimited), even though their cost sums to 15");
                Assert.That(disabled, Is.Empty,
                    "No trait should be rejected for the points limit when unlimited");
            });

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Sanity that the gate still bites when it is a positive budget: a budget of 5 admits one cost-5 trait and
    /// rejects the rest. Guards against the unlimited change accidentally disabling the gate for positive values.
    /// </summary>
    [Test]
    public async Task PositiveMaxPoints_StillCaps()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var cfg = server.ResolveDependency<IConfigurationManager>();

        await server.WaitAssertion(() =>
        {
            cfg.SetCVar(DCCVars.MaxTraitCount, 0); // unlimited count, isolate the points gate
            cfg.SetCVar(DCCVars.MaxTraitPoints, 5); // room for exactly one cost-5 trait

            var player = entMan.SpawnEntity(null, MapCoordinates.Nullspace);

            var selected = new HashSet<ProtoId<TraitPrototype>>
            {
                "TestPointsCapA",
                "TestPointsCapB",
                "TestPointsCapC",
            };

            var traitSys = entMan.System<TraitSystem>();
            var method = typeof(TraitSystem).GetMethod("ValidateTraits",
                BindingFlags.NonPublic | BindingFlags.Instance);

            var disabled = new Dictionary<ProtoId<TraitPrototype>, List<string>>();
            var valid = (List<TraitPrototype>?)method?.Invoke(traitSys,
                new object?[] { player, selected, null, null, null, null, disabled });

            Assert.That(valid?.Count, Is.EqualTo(1),
                "A positive points budget of 5 should admit exactly one cost-5 trait");

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }
}
