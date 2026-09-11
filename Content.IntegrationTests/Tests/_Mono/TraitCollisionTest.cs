// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server._DV.Traits;
using Content.Shared._DV.Traits;
using Content.Shared._DV.Traits.Effects;
using Content.Shared._Mono.Traits.Physical;
using Content.Shared.Damage.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Mono;

/// <summary>
/// Regression tests for the trait-collision class of bug: two traits whose effects
/// add the same component type silently drop one of them, because the engine's
/// AddComponents(removeExisting: false) skips a component the entity already has.
/// The fix conforms our content to DeltaV's "one trait = one unique component" rule.
/// </summary>
[TestFixture]
[TestOf(typeof(TraitSystem))]
public sealed class TraitCollisionTest
{
    /// <summary>
    /// DermalArmor and HardenedLymphocytes are meant to stack (plating vs immune system).
    /// Both must survive being applied together, on distinct components.
    /// </summary>
    [Test]
    public async Task DermalArmorAndHardenedLymphocytes_BothApply()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var player = entMan.SpawnEntity(null, MapCoordinates.Nullspace);

            ApplyTrait(entMan, protoMan, factory, player, "DermalArmor");
            ApplyTrait(entMan, protoMan, factory, player, "HardenedLymphocytes");

            Assert.Multiple(() =>
            {
                Assert.That(entMan.HasComponent<DamageProtectionBuffComponent>(player),
                    Is.True,
                    "DermalArmor should grant DamageProtectionBuffComponent");
                Assert.That(entMan.HasComponent<HardenedLymphocytesComponent>(player),
                    Is.True,
                    "HardenedLymphocytes should grant its own component, not be skipped as a duplicate DamageProtectionBuff");
            });

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }

    private static void ApplyTrait(
        IEntityManager entMan,
        IPrototypeManager protoMan,
        IComponentFactory factory,
        EntityUid player,
        string traitId)
    {
        var trait = protoMan.Index(new ProtoId<TraitPrototype>(traitId));
        var ctx = new TraitEffectContext
        {
            Player = player,
            EntMan = entMan,
            Proto = protoMan,
            CompFactory = factory,
            LogMan = IoCManager.Resolve<ILogManager>(),
            Transform = entMan.GetComponent<TransformComponent>(player),
        };

        foreach (var effect in trait.Effects)
            effect.Apply(ctx);
    }
}
