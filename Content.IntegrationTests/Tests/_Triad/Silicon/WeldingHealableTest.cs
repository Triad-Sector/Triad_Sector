// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using Content.Server._EinsteinEngines.Silicon.WeldingHealable;
using Content.Server._EinsteinEngines.Silicon.WeldingHealing;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Triad.Silicon;

/// <summary>
/// Regression test for the damage container mismatch in WeldingHealableSystem.HasDamage. The method
/// indexes the target's damage by keys taken from the healing spec, and a target whose container does
/// not carry one of them threw KeyNotFound out of the repair do-after. In production this was the
/// welder heal listing Radiation against a silicon container that omits it, and it was the largest
/// single source of caught entsys exceptions.
/// </summary>
[TestFixture]
[TestOf(typeof(WeldingHealableSystem))]
public sealed class WeldingHealableTest
{
    /// <summary>
    /// A key no container defines. Using a synthetic type rather than Radiation keeps the test
    /// deterministic: it cannot start passing because someone added Radiation to a container, and the
    /// mechanism under test is only "key absent from the dictionary".
    /// </summary>
    private const string AbsentDamageType = "TriadTestAbsentDamageType";

    /// <summary>
    /// The whole-entity pass was already guarded. This covers the body-part pass, which is reached
    /// only when the user has a targeting component, and which repeats the lookup against a part's
    /// own container, a narrower set again.
    /// </summary>
    [Test]
    public async Task HasDamage_AbsentDamageTypeOnBodyPart_DoesNotThrow()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();

        await server.WaitAssertion(() =>
        {
            var target = entMan.SpawnEntity("MobHuman", MapCoordinates.Nullspace);
            var user = entMan.SpawnEntity("MobHuman", MapCoordinates.Nullspace);

            // Targeting is what routes HasDamage into the per-part branch.
            entMan.EnsureComponent<TargetingComponent>(user);

            var healingHolder = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var healing = entMan.AddComponent<WeldingHealingComponent>(healingHolder);
            healing.Damage = new DamageSpecifier();
            healing.Damage.DamageDict[AbsentDamageType] = FixedPoint2.New(-5);
            healing.DamageContainers = new List<string>();

            var damageable = entMan.GetComponent<DamageableComponent>(target);
            var system = entMan.System<WeldingHealableSystem>();

            bool result = false;
            Assert.DoesNotThrow(
                () => result = system.HasDamage((target, damageable), healing, user),
                "a healing spec listing a damage type the target's body parts cannot take must not throw");

            Assert.That(result, Is.False,
                "a damage type the target cannot take is not damage, so there is nothing to heal");

            entMan.DeleteEntity(healingHolder);
            entMan.DeleteEntity(user);
            entMan.DeleteEntity(target);
        });

        await pair.CleanReturnAsync();
    }
}
