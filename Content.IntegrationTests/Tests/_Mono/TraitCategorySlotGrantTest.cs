// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Server._DV.Traits;
using Content.Shared._DV.Traits;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Mono;

/// <summary>
/// Triad: a trait can grant extra slots to a category (GrantsCategorySlots), raising that category's
/// effective MaxTraits when selected. Used so Foreigner lets you pick extra languages beyond the base cap.
/// </summary>
[TestFixture]
[TestOf(typeof(TraitSystem))]
public sealed class TraitCategorySlotGrantTest
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: traitCategory
  id: TestSlotGrantCategory
  name: trait-blindness-name
  maxTraits: 1
  maxPoints: null

# Grants +2 slots to the capped category.
- type: trait
  id: TestSlotGranter
  name: trait-blindness-name
  description: trait-blindness-desc
  category: TestSlotGrantCategory
  cost: 0
  grantsCategorySlots:
    TestSlotGrantCategory: 2

- type: trait
  id: TestSlotMemberA
  name: trait-blindness-name
  description: trait-blindness-desc
  category: TestSlotGrantCategory
  cost: 0

- type: trait
  id: TestSlotMemberB
  name: trait-blindness-name
  description: trait-blindness-desc
  category: TestSlotGrantCategory
  cost: 0
";

    /// <summary>
    /// Base cap is 1, but the granter adds +2, so granter + 2 members (3 total) all validate.
    /// </summary>
    [Test]
    public async Task GranterRaisesCategoryCap()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var player = entMan.SpawnEntity(null, MapCoordinates.Nullspace);

            var selected = new HashSet<ProtoId<TraitPrototype>>
            {
                "TestSlotGranter",
                "TestSlotMemberA",
                "TestSlotMemberB",
            };

            var traitSys = entMan.System<TraitSystem>();
            var method = typeof(TraitSystem).GetMethod("ValidateTraits",
                BindingFlags.NonPublic | BindingFlags.Instance);

            var disabled = new Dictionary<ProtoId<TraitPrototype>, List<string>>();
            var valid = (List<TraitPrototype>?)method?.Invoke(traitSys,
                new object?[] { player, selected, null, null, null, null, disabled });

            Assert.That(valid?.Count, Is.EqualTo(3),
                "Granter (+2 slots) should let all 3 traits fit a base-1 category");

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Without the granter, the base cap of 1 holds: only one of the two members validates.
    /// </summary>
    [Test]
    public async Task BaseCapHoldsWithoutGranter()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var player = entMan.SpawnEntity(null, MapCoordinates.Nullspace);

            var selected = new HashSet<ProtoId<TraitPrototype>>
            {
                "TestSlotMemberA",
                "TestSlotMemberB",
            };

            var traitSys = entMan.System<TraitSystem>();
            var method = typeof(TraitSystem).GetMethod("ValidateTraits",
                BindingFlags.NonPublic | BindingFlags.Instance);

            var disabled = new Dictionary<ProtoId<TraitPrototype>, List<string>>();
            var valid = (List<TraitPrototype>?)method?.Invoke(traitSys,
                new object?[] { player, selected, null, null, null, null, disabled });

            Assert.That(valid?.Count, Is.EqualTo(1),
                "Base cap of 1 should hold when no granter is selected");

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }
}
