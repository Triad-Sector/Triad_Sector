// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Content.Server._Triad.Shipyard;
using Content.Server.KillTracking;
using Content.Server.NPC.Components;
using Content.Shared._Shitmed.Body.Part;
using Content.Shared.DeviceLinking;
using Content.Shared.Humanoid;
using Content.Shared.Payload.Components;
using Content.Shared.SmartFridge;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Utility;
using Robust.UnitTesting.Pool;

namespace Content.IntegrationTests.Tests._Triad.Shipyard;

/// <summary>
/// Exercises the ship-grid save against the runtime state that was killing it in production. The save
/// runs with the engine default <see cref="EntityExceptionBehaviour.Rethrow"/>, so one component the
/// YAML writer cannot express takes the whole save with it.
///
/// Three things are pinned. Device links to sinks off the grid are cleared before serialization
/// instead of colliding as a duplicate "invalid" key. A stocked SmartFridge comes back stocked and
/// listed after a save and load, which depends on MapInit firing on load, which depends on the
/// sanitizer stripping the mapInit flag. And the runtime-only state the fix stopped persisting no
/// longer reaches the serializer, while a planted control proves the collision being guarded against
/// is still real.
/// </summary>
[TestFixture]
[TestOf(typeof(ShipyardGridSaveSystem))]
public sealed class ShipSaveSerializationTest
{
    private const string SignalButtonProtoId = "SignalButtonDirectional";
    private const string SmartFridgeProtoId = "SmartFridge";
    private const string ProduceProtoId = "FoodAmbrosiaVulgaris";
    private const string SinkProtoId = "ShipSaveTestSink";
    private const string PressedPort = "Pressed";
    private const string TogglePort = "Toggle";

    [TestPrototypes]
    private const string TestPrototypes = $@"
- type: entity
  id: {SinkProtoId}
  components:
  - type: DeviceLinkSink
    ports:
    - {TogglePort}
";

    /// <summary>
    /// The production case, reduced: a docking button on the ship wired to shutters on the station.
    /// Two links whose sinks are alive but not on the grid each serialize as the string "invalid",
    /// and the second one is a duplicate dictionary key. The cleanup used to test whether the sink
    /// existed, which a live station entity passes, so it never saw this class at all.
    /// </summary>
    [Test]
    public async Task OffGridDeviceLinksAreClearedAndTheSaveRoundTrips()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var saveSystem = entMan.System<ShipyardGridSaveSystem>();
        var deviceLink = entMan.System<SharedDeviceLinkSystem>();
        var transform = entMan.System<SharedTransformSystem>();
        var mapLoader = entMan.System<MapLoaderSystem>();

        var map = await pair.CreateTestMap();

        EntityUid button = default, onGridSink = default, offGridA = default, offGridB = default;
        await server.WaitPost(() =>
        {
            button = entMan.SpawnEntity(SignalButtonProtoId, map.GridCoords);

            onGridSink = entMan.SpawnEntity(SinkProtoId, map.GridCoords);
            transform.AnchorEntity(onGridSink);

            // Off the grid's one tile, so the engine parents these to the map rather than the grid.
            offGridA = entMan.SpawnEntity(SinkProtoId, new MapCoordinates(new Vector2(4, 4), map.MapId));
            offGridB = entMan.SpawnEntity(SinkProtoId, new MapCoordinates(new Vector2(5, 5), map.MapId));

            foreach (var sink in new[] { onGridSink, offGridA, offGridB })
                deviceLink.SaveLinks(null, button, sink, [(PressedPort, TogglePort)]);
        });

        await server.WaitAssertion(() =>
        {
            var source = entMan.GetComponent<DeviceLinkSourceComponent>(button);
            Assert.Multiple(() =>
            {
                Assert.That(source.LinkedPorts.Keys, Is.EquivalentTo(new[] { onGridSink, offGridA, offGridB }),
                    "Fixture: all three links must be in place before the save, or nothing below is tested.");
                Assert.That(entMan.GetComponent<TransformComponent>(offGridA).GridUid, Is.Not.EqualTo(map.Grid.Owner),
                    "Fixture: the off-grid sinks landed on the grid, so the test proves nothing.");
                Assert.That(entMan.GetComponent<TransformComponent>(onGridSink).GridUid, Is.EqualTo(map.Grid.Owner),
                    "Fixture: the on-grid sink is not on the grid.");
            });
        });

        string? yaml = null;
        await server.WaitAssertion(() =>
        {
            Assert.That(saveSystem.TryBuildShipSaveYaml(map.Grid.Owner, out yaml, out _), Is.True,
                "The save threw or refused; with two off-grid links that is the production failure.");
        });

        await server.WaitAssertion(() =>
        {
            var source = entMan.GetComponent<DeviceLinkSourceComponent>(button);
            Assert.Multiple(() =>
            {
                Assert.That(source.LinkedPorts.Keys, Is.EquivalentTo(new[] { onGridSink }),
                    "Off-grid links must be cleared from the live source and the on-grid link must survive.");
                Assert.That(entMan.EntityExists(offGridA) && entMan.EntityExists(offGridB), Is.True,
                    "Clearing a link must not delete the sink on the other end.");
            });
        });

        var loaded = await LoadShipYaml(server, mapLoader, yaml!, map);

        await server.WaitAssertion(() =>
        {
            var sources = loaded.Entities.Where(e => entMan.HasComponent<DeviceLinkSourceComponent>(e)).ToList();
            Assert.That(sources, Has.Count.EqualTo(1), "Exactly one link source should come back: the button.");

            var source = entMan.GetComponent<DeviceLinkSourceComponent>(sources[0]);
            Assert.That(source.LinkedPorts, Has.Count.EqualTo(1), "Only the on-grid link should round-trip.");

            var sink = source.LinkedPorts.Keys.Single();
            Assert.Multiple(() =>
            {
                Assert.That(sink, Is.Not.EqualTo(EntityUid.Invalid), "An \"invalid\" key reached the file.");
                Assert.That(loaded.Entities, Does.Contain(sink), "The surviving link must resolve to the loaded sink.");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The listing (<see cref="SmartFridgeComponent.ContainedEntries"/>) is no longer persisted, because
    /// its key is a data definition and cannot be written as a YAML mapping key. It is rebuilt from the
    /// container on MapInit instead. This proves the rebuild runs on a real ship load and agrees with
    /// what is physically inside.
    /// </summary>
    [Test]
    public async Task SmartFridgeStockSurvivesSaveAndLoad()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var saveSystem = entMan.System<ShipyardGridSaveSystem>();
        var containers = entMan.System<SharedContainerSystem>();
        var mapLoader = entMan.System<MapLoaderSystem>();

        var map = await pair.CreateTestMap();
        var fridge = await StockAFridge(server, map);

        string? yaml = null;
        await server.WaitAssertion(() =>
        {
            Assert.That(saveSystem.TryBuildShipSaveYaml(map.Grid.Owner, out yaml, out _), Is.True,
                "The save threw; a stocked fridge was one of the production failures.");
        });

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<SmartFridgeComponent>(fridge);
            var container = containers.GetContainer(fridge, comp.Container);
            Assert.Multiple(() =>
            {
                Assert.That(container.ContainedEntities, Has.Count.EqualTo(2),
                    "The purge must keep machine contents, or the round trip below is testing an empty fridge.");
                Assert.That(yaml, Does.Not.Contain("containedEntries"),
                    "The listing must not be persisted at all; the writer cannot express it.");
            });
        });

        var loaded = await LoadShipYaml(server, mapLoader, yaml!, map);

        await server.WaitAssertion(() =>
        {
            var fridges = loaded.Entities.Where(e => entMan.HasComponent<SmartFridgeComponent>(e)).ToList();
            Assert.That(fridges, Has.Count.EqualTo(1));

            var uid = fridges[0];
            var comp = entMan.GetComponent<SmartFridgeComponent>(uid);
            var container = containers.GetContainer(uid, comp.Container);
            var stocked = container.ContainedEntities.Select(e => entMan.GetNetEntity(e)).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(stocked, Has.Count.EqualTo(2), "The stock itself must survive the round trip.");
                Assert.That(comp.Entries, Has.Count.EqualTo(1), "The menu entry is persisted and must survive.");
                Assert.That(comp.ContainedEntries.GetValueOrDefault(comp.Entries[0]) ?? new HashSet<NetEntity>(),
                    Is.EquivalentTo(stocked),
                    "The listing must be rebuilt from the container on load. If this is empty, MapInit did not fire.");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Characterizes why the test above passes, so the dependency cannot rot silently. The engine's own
    /// grid save keeps the per-entity mapInit flag, so on load the entity is flagged map-initialized
    /// without the event ever being raised, and a load raises no container-insert events either. The
    /// listing therefore comes back EMPTY over a full container. The Triad save passes only because the
    /// sanitizer strips that flag. If this test ever fails, the engine has started raising MapInit on
    /// post-init loads and that strip is no longer load-bearing.
    /// </summary>
    [Test]
    public async Task ListingRebuildDependsOnTheSanitizerStrippingMapInit()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var containers = entMan.System<SharedContainerSystem>();
        var mapLoader = entMan.System<MapLoaderSystem>();

        var map = await pair.CreateTestMap();
        await StockAFridge(server, map);

        var path = new ResPath("/ship-save-test-engine-path.yml");
        Entity<MapGridComponent>? loadedGrid = null;

        await server.WaitAssertion(() =>
        {
            Assert.That(mapLoader.TrySaveGrid(map.Grid.Owner, path), Is.True, "Engine save failed.");
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(mapLoader.TryLoadGrid(map.MapId, path, out loadedGrid, offset: new Vector2(100, 100)), Is.True,
                "Engine load failed.");
        });

        await server.WaitAssertion(() =>
        {
            var query = entMan.EntityQueryEnumerator<SmartFridgeComponent, TransformComponent>();
            SmartFridgeComponent? comp = null;
            EntityUid uid = default;
            while (query.MoveNext(out var candidate, out var fridge, out var xform))
            {
                if (xform.GridUid != loadedGrid!.Value.Owner)
                    continue;

                comp = fridge;
                uid = candidate;
            }

            Assert.That(comp, Is.Not.Null, "No fridge came back on the loaded grid.");

            var container = containers.GetContainer(uid, comp!.Container);
            Assert.Multiple(() =>
            {
                Assert.That(container.ContainedEntities, Has.Count.EqualTo(2), "Contents survive the engine path too.");
                Assert.That(comp.ContainedEntries.Values.Sum(set => set.Count), Is.Zero,
                    "The engine path rebuilt the listing. MapInit now fires on post-init loads, so the " +
                    "sanitizer's mapInit strip is no longer what makes the Triad round trip work.");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Each of the remaining fixed components, with the exact runtime state that used to throw, run
    /// through the serializer with the live save's options. None may throw, and the dropped fields may
    /// not appear. BodyPartAppearance keeps its value under the renamed key.
    /// </summary>
    [Test]
    public async Task RuntimeOnlyStateNoLongerReachesTheSerializer()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var mapLoader = entMan.System<MapLoaderSystem>();

        var map = await pair.CreateTestMap();

        EntityUid offA = default, offB = default;
        EntityUid payload = default, npc = default, tracker = default, limb = default;
        await server.WaitPost(() =>
        {
            offA = entMan.SpawnEntity(null, new MapCoordinates(new Vector2(4, 4), map.MapId));
            offB = entMan.SpawnEntity(null, new MapCoordinates(new Vector2(5, 5), map.MapId));

            payload = entMan.SpawnEntity(null, map.GridCoords);
            entMan.EnsureComponent<PayloadTriggerComponent>(payload).GrantedComponents.Add(typeof(TransformComponent));

            npc = entMan.SpawnEntity(null, map.GridCoords);
            var memories = entMan.EnsureComponent<NPCRetaliationComponent>(npc);
#pragma warning disable RA0002
            memories.AttackMemories[offA] = TimeSpan.Zero;
            memories.AttackMemories[offB] = TimeSpan.Zero;
#pragma warning restore RA0002

            tracker = entMan.SpawnEntity(null, map.GridCoords);
            var kills = entMan.EnsureComponent<KillTrackerComponent>(tracker);
#pragma warning disable RA0002
            kills.LifetimeDamage[new KillNpcSource(offA)] = 1;
            kills.LifetimeDamage[new KillNpcSource(offB)] = 2;
#pragma warning restore RA0002

            limb = entMan.SpawnEntity(null, map.GridCoords);
            entMan.EnsureComponent<BodyPartAppearanceComponent>(limb).Type = HumanoidVisualLayers.LArm;
        });

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                AssertSerializesWithout(mapLoader, payload, "PayloadTrigger", "grantedComponents");
                AssertSerializesWithout(mapLoader, npc, "NPCRetaliation", "attackMemories");
                AssertSerializesWithout(mapLoader, tracker, "KillTracker", "lifetimeDamage");

                var limbNode = FindComponentNode(Serialize(mapLoader, limb), "BodyPartAppearance");
                Assert.That(limbNode, Is.Not.Null, "BodyPartAppearance with a non-default layer type should be written.");
                Assert.That(limbNode!.TryGet<ValueDataNode>("layerType", out var layer) && layer.Value == "LArm", Is.True,
                    "The layer type must survive under its renamed key.");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Control. A clean run above is only meaningful if the serializer still fails on the shape being
    /// guarded against, so this plants it: a persisted dictionary keyed by EntityUid with two keys off
    /// the grid. One such key is harmless and writes a single "invalid"; the second is the duplicate
    /// key that killed saves. This is also the argument for a round-trip gate in CI: any new component
    /// with this shape still kills the save, and nothing in the fix prevents that.
    /// </summary>
    [Test]
    public async Task ControlTwoOffGridKeysInAPersistedDictionaryStillCollide()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var mapLoader = entMan.System<MapLoaderSystem>();

        var map = await pair.CreateTestMap();

        EntityUid offA = default, offB = default, oneKey = default, twoKeys = default;
        await server.WaitPost(() =>
        {
            offA = entMan.SpawnEntity(null, new MapCoordinates(new Vector2(4, 4), map.MapId));
            offB = entMan.SpawnEntity(null, new MapCoordinates(new Vector2(5, 5), map.MapId));

            oneKey = entMan.SpawnEntity(null, map.GridCoords);
            entMan.EnsureComponent<ShipSaveOffGridKeyControlComponent>(oneKey).Refs[offA] = 1;

            twoKeys = entMan.SpawnEntity(null, map.GridCoords);
            var refs = entMan.EnsureComponent<ShipSaveOffGridKeyControlComponent>(twoKeys).Refs;
            refs[offA] = 1;
            refs[offB] = 2;
        });

        await server.WaitAssertion(() =>
        {
            Assert.DoesNotThrow(() => Serialize(mapLoader, oneKey),
                "One off-grid key writes a single \"invalid\" and must not throw; the bug is the collision.");
        });

        // The engine logs an error on its way to rethrowing, and the pool fails any test that logged an
        // error when the pair is returned. Here that log IS the expected outcome. The handler's JudgeLog
        // hook would let us accept exactly that line, but its signature names a Serilog type this
        // project does not reference, so instead the failure threshold is lifted for exactly the
        // duration of the one synchronous call that produces it, and restored after.
        var failureLevel = pair.ServerLogHandler.FailureLevel;
        pair.ServerLogHandler.FailureLevel = null;
        try
        {
            await server.WaitAssertion(() =>
            {
                var ex = Assert.Throws<ArgumentException>(() => Serialize(mapLoader, twoKeys),
                    "Two off-grid keys no longer collide. Either the engine changed how it writes missing " +
                    "references, or the control is broken; either way the tests above are no longer proving anything.");
                Assert.That(ex!.Message, Does.Contain("invalid"));
            });
        }
        finally
        {
            pair.ServerLogHandler.FailureLevel = failureLevel;
        }

        await pair.CleanReturnAsync();
    }

    private static async Task<EntityUid> StockAFridge(Robust.UnitTesting.RobustIntegrationTest.ServerIntegrationInstance server, TestMapData map)
    {
        var entMan = server.EntMan;
        var containers = entMan.System<SharedContainerSystem>();

        EntityUid fridge = default;
        await server.WaitPost(() =>
        {
            fridge = entMan.SpawnEntity(SmartFridgeProtoId, map.GridCoords);
            var comp = entMan.GetComponent<SmartFridgeComponent>(fridge);
            var container = containers.GetContainer(fridge, comp.Container);

            for (var i = 0; i < 2; i++)
            {
                var item = entMan.SpawnEntity(ProduceProtoId, map.GridCoords);
                containers.Insert(item, container);
            }
        });

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<SmartFridgeComponent>(fridge);
            Assert.Multiple(() =>
            {
                Assert.That(entMan.GetComponent<TransformComponent>(fridge).Anchored, Is.True,
                    "Fixture: the fridge must be anchored or the purge deletes it.");
                Assert.That(comp.Entries, Has.Count.EqualTo(1), "Fixture: two of one item should make one entry.");
                Assert.That(comp.ContainedEntries[comp.Entries[0]], Has.Count.EqualTo(2),
                    "Fixture: live inserts must populate the listing.");
            });
        });

        return fridge;
    }

    private static async Task<LoadResult> LoadShipYaml(
        Robust.UnitTesting.RobustIntegrationTest.ServerIntegrationInstance server,
        MapLoaderSystem mapLoader,
        string yaml,
        TestMapData map)
    {
        LoadResult? loaded = null;
        await server.WaitAssertion(() =>
        {
            // Merge onto the live test map, offset so the loaded grid does not sit on the original.
            var opts = new MapLoadOptions { MergeMap = map.MapId, Offset = new Vector2(100, 100) };
            Assert.That(mapLoader.TryLoadGeneric(new StringReader(yaml), "ship-save-test", out loaded, opts), Is.True,
                "The saved YAML did not load back.");
        });

        return loaded!;
    }

    private static MappingDataNode Serialize(MapLoaderSystem mapLoader, EntityUid uid)
    {
        var (node, _) = mapLoader.SerializeEntitiesRecursive(
            new HashSet<EntityUid> { uid },
            ShipyardGridSaveSystem.ShipSaveSerializationOptions);
        return node;
    }

    private static void AssertSerializesWithout(MapLoaderSystem mapLoader, EntityUid uid, string component, string droppedKey)
    {
        MappingDataNode? node = null;
        Assert.DoesNotThrow(() => node = Serialize(mapLoader, uid), $"{component} with runtime state populated still throws.");

        var componentNode = FindComponentNode(node!, component);
        if (componentNode == null)
            return; // Nothing non-default left to write, which is the point.

        Assert.That(componentNode.Has(droppedKey), Is.False, $"{component}.{droppedKey} was written; it cannot be.");
    }

    /// <summary>
    /// Walks the serializer's output: a root with an "entities" sequence of prototype groups, each with
    /// its own "entities" sequence, each entity carrying a "components" sequence keyed by "type".
    /// </summary>
    private static MappingDataNode? FindComponentNode(MappingDataNode root, string componentType)
    {
        if (!root.TryGet<SequenceDataNode>("entities", out var groups))
            return null;

        foreach (var group in groups.OfType<MappingDataNode>())
        {
            if (!group.TryGet<SequenceDataNode>("entities", out var entities))
                continue;

            foreach (var entity in entities.OfType<MappingDataNode>())
            {
                if (!entity.TryGet<SequenceDataNode>("components", out var components))
                    continue;

                foreach (var component in components.OfType<MappingDataNode>())
                {
                    if (component.TryGet<ValueDataNode>("type", out var type) && type.Value == componentType)
                        return component;
                }
            }
        }

        return null;
    }
}

/// <summary>
/// The shape that was killing saves, kept alive as a control: a persisted dictionary keyed by
/// EntityUid whose keys can point off the grid. Sits at namespace scope because a registered component
/// must be partial.
/// </summary>
[RegisterComponent]
public sealed partial class ShipSaveOffGridKeyControlComponent : Component
{
    [DataField]
    public Dictionary<EntityUid, int> Refs = new();
}
