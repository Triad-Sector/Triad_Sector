// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Server._DV.Traits;
using Content.Server._DV.Traits.Effects;
using Content.Server._EinsteinEngines.Language;
using Content.Server._Mono.Traits.Effects;
using Content.Server.Hands.Systems;
using Content.Shared._DV.Traits;
using Content.Shared._DV.Traits.Conditions;
using Content.Shared._DV.Traits.Effects;
using Content.Shared.Body.Components;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown.Mapping;

namespace Content.IntegrationTests.Tests._Mono;

// A throwaway marker with one scalar field, registered only in the test assembly, so the comp add/override/remove
// effects have a component they fully control to assert against without coupling to balance-sensitive content.
[RegisterComponent]
public sealed partial class TraitEffectsTestMarkerComponent : Component
{
    [DataField]
    public int Value;
}

/// <summary>
/// Unit-level coverage for the trait effect and condition surface that selectable traits actually drive.
/// Every concrete <see cref="BaseTraitEffect"/>.Apply is exercised (the four used by content plus the three that
/// exist but no shipped trait selects), and the three <see cref="BaseTraitCondition"/> implementations that gate
/// real traits (HasTrait incl. invert, IsSpecies, HasComp). Each test builds the context by hand, the same shape
/// <c>TraitSystem.ApplyTrait</c> / <c>ValidateTraits</c> build, so a regression in an effect or guard fails here
/// rather than silently dropping a player's trait at spawn.
/// </summary>
[TestFixture]
[TestOf(typeof(BaseTraitEffect))]
[TestOf(typeof(BaseTraitCondition))]
public sealed class TraitEffectsTest
{
    // A trait + category selectable only in tests, so HasTraitCondition has a real (proto-resolvable) trait to find
    // in a profile's preferences. GetValidTraits drops traits whose category does not resolve, hence the category.
    [TestPrototypes]
    private const string Prototypes = @"
- type: traitCategory
  id: TraitEffectsTestCategory
  name: trait-blindness-name
  maxTraits: null
  maxPoints: null

- type: trait
  id: TraitEffectsTestSelected
  name: trait-blindness-name
  description: trait-blindness-desc
  category: TraitEffectsTestCategory
  cost: 0
";

    private static TraitEffectContext MakeEffectCtx(IEntityManager entMan, IPrototypeManager proto, IComponentFactory factory, EntityUid player)
    {
        return new TraitEffectContext
        {
            Player = player,
            EntMan = entMan,
            Proto = proto,
            CompFactory = factory,
            LogMan = IoCManager.Resolve<ILogManager>(),
            Transform = entMan.GetComponent<TransformComponent>(player),
        };
    }

    private static TraitConditionContext MakeConditionCtx(
        IEntityManager entMan,
        IPrototypeManager proto,
        IComponentFactory factory,
        EntityUid player,
        ProtoId<Content.Shared.Humanoid.Prototypes.SpeciesPrototype>? speciesId = null,
        HumanoidCharacterProfile? profile = null)
    {
        return new TraitConditionContext
        {
            Player = player,
            Session = null,
            EntMan = entMan,
            Proto = proto,
            CompFactory = factory,
            LogMan = IoCManager.Resolve<ILogManager>(),
            JobId = null,
            SpeciesId = speciesId,
            Profile = profile,
        };
    }

    // Builds a ComponentRegistry holding a single TraitEffectsTestMarkerComponent with the given Value, the way the
    // YAML deserializer would hand it to an effect. An empty mapping is enough here: AddComponents reads the typed
    // instance, and the test never round-trips through serialization.
    private static ComponentRegistry MarkerRegistry(IComponentFactory factory, int value)
    {
        var name = factory.GetComponentName(typeof(TraitEffectsTestMarkerComponent));
        var comp = (TraitEffectsTestMarkerComponent)factory.GetComponent(name);
        comp.Value = value;

        return new ComponentRegistry
        {
            [name] = new EntityPrototype.ComponentRegistryEntry(comp, new MappingDataNode()),
        };
    }

    #region Effects

    [Test]
    public async Task AddComps_AddsComponent()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var player = entMan.SpawnEntity(null, MapCoordinates.Nullspace);

            var effect = new AddCompsEffect { Components = MarkerRegistry(factory, 42) };
            effect.Apply(MakeEffectCtx(entMan, proto, factory, player));

#pragma warning disable NUnit2045 // Interdependent assertions.
            Assert.That(entMan.TryGetComponent(player, out TraitEffectsTestMarkerComponent? marker), Is.True,
                "AddCompsEffect should add the listed component");
            Assert.That(marker!.Value, Is.EqualTo(42));
#pragma warning restore NUnit2045

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AddComps_DoesNotOverwriteExisting()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var player = entMan.SpawnEntity(null, MapCoordinates.Nullspace);

            // Seed an existing marker with a sentinel value, then add a registry that would set a different value.
            entMan.AddComponent<TraitEffectsTestMarkerComponent>(player).Value = 7;

            var effect = new AddCompsEffect { Components = MarkerRegistry(factory, 42) };
            effect.Apply(MakeEffectCtx(entMan, proto, factory, player));

            Assert.That(entMan.GetComponent<TraitEffectsTestMarkerComponent>(player).Value, Is.EqualTo(7),
                "AddCompsEffect uses removeExisting:false, so an already-present component must survive untouched");

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task OverrideComps_ReplacesExisting()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var player = entMan.SpawnEntity(null, MapCoordinates.Nullspace);

            entMan.AddComponent<TraitEffectsTestMarkerComponent>(player).Value = 7;

            var effect = new OverrideCompsEffect { Components = MarkerRegistry(factory, 42) };
            effect.Apply(MakeEffectCtx(entMan, proto, factory, player));

            Assert.That(entMan.GetComponent<TraitEffectsTestMarkerComponent>(player).Value, Is.EqualTo(42),
                "OverrideCompsEffect uses removeExisting:true, so the new value must replace the old one");

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RemComps_RemovesPresentComponent()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var player = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            entMan.AddComponent<TraitEffectsTestMarkerComponent>(player);

            var name = factory.GetComponentName(typeof(TraitEffectsTestMarkerComponent));
            var effect = new RemCompsEffect { Components = new() { name } };
            effect.Apply(MakeEffectCtx(entMan, proto, factory, player));

            Assert.That(entMan.HasComponent<TraitEffectsTestMarkerComponent>(player), Is.False,
                "RemCompsEffect should remove a component that is present");

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RemComps_UnknownComponentNameDoesNotThrow()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var player = entMan.SpawnEntity(null, MapCoordinates.Nullspace);

            // The effect logs a warning and continues on an unknown name; the call must not throw.
            var effect = new RemCompsEffect { Components = new() { "ThisComponentDoesNotExist" } };
            Assert.DoesNotThrow(() => effect.Apply(MakeEffectCtx(entMan, proto, factory, player)),
                "RemCompsEffect must skip unknown component names, not throw");

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RemoveBodyPart_RemovesArmFromRealBody()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();
        var bodySys = entMan.System<SharedBodySystem>();

        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var player = entMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, map.MapId));

            // Sanity: the human starts with a left arm before the effect runs.
            var body = entMan.GetComponent<BodyComponent>(player);
            var leftArmsBefore = bodySys.GetBodyChildrenOfType(player, BodyPartType.Arm, body, BodyPartSymmetry.Left).Count();
            Assert.That(leftArmsBefore, Is.GreaterThan(0), "MobHuman should spawn with a left arm");

            var effect = new RemoveBodyPartEffect { Part = BodyPartType.Arm, Symmetry = BodyPartSymmetry.Left };
            effect.Apply(MakeEffectCtx(entMan, proto, factory, player));

            var leftArmsAfter = bodySys.GetBodyChildrenOfType(player, BodyPartType.Arm, body, BodyPartSymmetry.Left).Count();
            Assert.That(leftArmsAfter, Is.EqualTo(0), "RemoveBodyPartEffect should delete the left arm subtree");

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RemoveBodyPart_NoBodyDoesNotThrow()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            // A bodyless entity hits the first guard (no BodyComponent) and must early-return cleanly.
            var player = entMan.SpawnEntity(null, MapCoordinates.Nullspace);

            var effect = new RemoveBodyPartEffect { Part = BodyPartType.Arm, Symmetry = BodyPartSymmetry.Left };
            Assert.DoesNotThrow(() => effect.Apply(MakeEffectCtx(entMan, proto, factory, player)),
                "RemoveBodyPartEffect must no-op on an entity with no body, not throw");

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SpawnItemInHand_PutsItemInHand()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();
        var handsSys = entMan.System<HandsSystem>();

        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var player = entMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, map.MapId));

            var effect = new SpawnItemInHandEffect { Item = "Crowbar" };
            effect.Apply(MakeEffectCtx(entMan, proto, factory, player));

            var holdingCrowbar = handsSys.EnumerateHeld(player)
                .Any(e => entMan.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID == "Crowbar");

            Assert.That(holdingCrowbar, Is.True, "SpawnItemInHandEffect should place the spawned item into a hand");

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SpawnItemInHand_NoHandsDoesNotThrow()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();

        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            // No HandsComponent: the effect logs and returns before spawning. It must not throw.
            var player = entMan.SpawnEntity(null, new MapCoordinates(Vector2.Zero, map.MapId));

            var effect = new SpawnItemInHandEffect { Item = "Crowbar" };
            Assert.DoesNotThrow(() => effect.Apply(MakeEffectCtx(entMan, proto, factory, player)),
                "SpawnItemInHandEffect must no-op on a handless entity, not throw");

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AddLanguages_GrantsSpeechAndUnderstanding()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();
        var langSys = entMan.System<LanguageSystem>();

        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var player = entMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, map.MapId));

            Assert.That(langSys.CanSpeak(player, "Cat"), Is.False, "MobHuman should not speak Cat by default");

            var effect = new AddLanguagesEffect { Languages = new() { "Cat" }, Spoken = true, Understood = true };
            effect.Apply(MakeEffectCtx(entMan, proto, factory, player));

            Assert.Multiple(() =>
            {
                Assert.That(langSys.CanSpeak(player, "Cat"), Is.True, "AddLanguagesEffect should grant speech");
                Assert.That(langSys.CanUnderstand(player, "Cat"), Is.True, "AddLanguagesEffect should grant understanding");
            });

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RemoveLanguages_RevokesSpeechAndUnderstanding()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();
        var langSys = entMan.System<LanguageSystem>();

        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var player = entMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, map.MapId));

            // Grant first so the removal has something to revoke (RemoveLanguage no-ops without LanguageKnowledge).
            langSys.AddLanguage(player, "Cat", true, true);
            Assert.That(langSys.CanSpeak(player, "Cat"), Is.True, "precondition: Cat was granted");

            var effect = new RemoveLanguagesEffect { Languages = new() { "Cat" }, Spoken = true, Understood = true };
            effect.Apply(MakeEffectCtx(entMan, proto, factory, player));

            Assert.Multiple(() =>
            {
                Assert.That(langSys.CanSpeak(player, "Cat"), Is.False, "RemoveLanguagesEffect should revoke speech");
                Assert.That(langSys.CanUnderstand(player, "Cat"), Is.False, "RemoveLanguagesEffect should revoke understanding");
            });

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }

    #endregion

    #region Conditions

    [Test]
    public async Task IsSpecies_MatchesAndInverts()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var player = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var humanCtx = MakeConditionCtx(entMan, proto, factory, player, speciesId: "Human");

            Assert.Multiple(() =>
            {
                Assert.That(new IsSpeciesCondition { Species = "Human" }.Evaluate(humanCtx), Is.True,
                    "IsSpeciesCondition should pass when the species matches");
                Assert.That(new IsSpeciesCondition { Species = "Human", Invert = true }.Evaluate(humanCtx), Is.False,
                    "Invert should flip a match to a fail");
                Assert.That(new IsSpeciesCondition { Species = "Vox" }.Evaluate(humanCtx), Is.False,
                    "IsSpeciesCondition should fail when the species differs");
                Assert.That(new IsSpeciesCondition { Species = "Vox", Invert = true }.Evaluate(humanCtx), Is.True,
                    "Invert should flip a non-match to a pass (the mutual-exclusion guard pattern)");
            });

            // No species known on the context: the implementation returns false, so a bare condition fails and the
            // inverted form passes.
            var noSpeciesCtx = MakeConditionCtx(entMan, proto, factory, player, speciesId: null);
            Assert.That(new IsSpeciesCondition { Species = "Human" }.Evaluate(noSpeciesCtx), Is.False,
                "IsSpeciesCondition should fail when no species is resolvable");

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HasComp_DetectsPresenceAndInverts()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var player = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            var ctx = MakeConditionCtx(entMan, proto, factory, player);
            var name = factory.GetComponentName(typeof(TraitEffectsTestMarkerComponent));

            Assert.That(new HasCompCondition { Component = name }.Evaluate(ctx), Is.False,
                "HasCompCondition should fail when the component is absent");

            entMan.AddComponent<TraitEffectsTestMarkerComponent>(player);

            Assert.Multiple(() =>
            {
                Assert.That(new HasCompCondition { Component = name }.Evaluate(ctx), Is.True,
                    "HasCompCondition should pass once the component is present");
                Assert.That(new HasCompCondition { Component = name, Invert = true }.Evaluate(ctx), Is.False,
                    "Invert should flip a present component to a fail");
            });

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HasTrait_FindsSelectedTraitAndInverts()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var player = entMan.SpawnEntity(null, MapCoordinates.Nullspace);

            var profile = new HumanoidCharacterProfile().WithTraitPreference("TraitEffectsTestSelected", proto);
            var ctx = MakeConditionCtx(entMan, proto, factory, player, profile: profile);

            Assert.Multiple(() =>
            {
                Assert.That(new HasTraitCondition { Trait = "TraitEffectsTestSelected" }.Evaluate(ctx), Is.True,
                    "HasTraitCondition should pass when the profile has the trait selected");
                Assert.That(new HasTraitCondition { Trait = "TraitEffectsTestSelected", Invert = true }.Evaluate(ctx), Is.False,
                    "Invert should flip a selected trait to a fail (mutual-exclusion guard)");
                Assert.That(new HasTraitCondition { Trait = "TraitEffectsTestSelected" }.Evaluate(
                        MakeConditionCtx(entMan, proto, factory, player, profile: null)), Is.False,
                    "HasTraitCondition should fail when no profile is available");
            });

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }

    #endregion

    #region Full apply path

    /// <summary>
    /// End-to-end over a real shipped trait: ArmAmputeeLeft selected on a profile must validate through
    /// <c>ValidateTraits</c> and, once applied through <c>ApplyTrait</c>, actually remove the left arm. Proves the
    /// validate -> order -> apply -> effect pipeline is wired, not just the effect in isolation.
    /// </summary>
    [Test]
    public async Task FullApplyPath_RealAmputeeTrait_RemovesArm()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var bodySys = entMan.System<SharedBodySystem>();

        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var player = entMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, map.MapId));
            var body = entMan.GetComponent<BodyComponent>(player);

            Assert.That(bodySys.GetBodyChildrenOfType(player, BodyPartType.Arm, body, BodyPartSymmetry.Left).Count(),
                Is.GreaterThan(0), "precondition: MobHuman has a left arm");

            var traitSys = entMan.System<TraitSystem>();
            var validate = typeof(TraitSystem).GetMethod("ValidateTraits", BindingFlags.NonPublic | BindingFlags.Instance);
            var apply = typeof(TraitSystem).GetMethod("ApplyTrait", BindingFlags.NonPublic | BindingFlags.Instance);

            var selected = new HashSet<ProtoId<TraitPrototype>> { "ArmAmputeeLeft" };
            var disabled = new Dictionary<ProtoId<TraitPrototype>, List<string>>();

            // ValidateTraits(player, selectedTraits, session, jobId, speciesId, profile, disabledTraits)
            var valid = (List<TraitPrototype>?)validate?.Invoke(traitSys,
                new object?[] { player, selected, null, null, null, null, disabled });

            Assert.That(valid, Is.Not.Null.And.Count.EqualTo(1), "ArmAmputeeLeft should validate with no limits in play");

            foreach (var trait in valid!)
                apply?.Invoke(traitSys, new object?[] { player, trait });

            Assert.That(bodySys.GetBodyChildrenOfType(player, BodyPartType.Arm, body, BodyPartSymmetry.Left).Count(),
                Is.EqualTo(0), "Applying ArmAmputeeLeft end-to-end should remove the left arm");

            entMan.DeleteEntity(player);
        });

        await pair.CleanReturnAsync();
    }

    #endregion
}
