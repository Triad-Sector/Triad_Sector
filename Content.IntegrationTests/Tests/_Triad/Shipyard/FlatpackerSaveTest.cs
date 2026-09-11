// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

#nullable enable

using System.Linq;
using System.Numerics;
using Content.Server._Triad.Shipyard;
using Content.Shared._Triad.Shipyard.Save.Contraband;
using Content.Shared.Construction.Components;
using Content.Shared.Materials;
using Content.Shared.Materials.OreSilo;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.Shipyard;

/// <summary>
/// Guards the flatpacker's re-enablement for ship saving.
///
/// It was excluded with no technical justification on record. It entered the exclusion list in
/// HardLight PR #876 (2e07876006, 2026-04-07) captioned "obvious non-ship entities", under the heading
/// "Uplinks and bundled items", next to mercenary uplinks and criminal-records computers, inside a
/// commit about an unrelated MindContainer bug. 227d5f6ab5 migrated it to the SavingContraband
/// component, and that commit's own comment concedes the list mixes entries that are "illegal to own"
/// with entries "causing problems with ship saving" without recording which any entry is.
///
/// Asking the machine settled it: it saves and loads fine. These tests keep it that way.
/// </summary>
[TestFixture]
[TestOf(typeof(ShipyardGridSaveSystem))]
public sealed class FlatpackerSaveTest
{
    private const string FlatpackerProtoId = "MachineFlatpacker";
    private const string BoardProtoId = "FlatpackerMachineCircuitboard";
    private const string SiloProtoId = "MachineMaterialSilo";

    /// <summary>
    /// The machine and its board must BOTH stay un-marked. The board carried its own SavingContraband
    /// from the same commit, and because the contraband test in IsInvalidEntity precedes the
    /// IsInsidePersistentStorage check that preserves a machine's slot contents, re-marking only the
    /// board is enough to bring back a flatpacker that saves and loads with an empty slot.
    ///
    /// That ordering is deliberate and correct, not a defect: a deny rule has to beat the keep rules,
    /// or contraband could be smuggled by anchoring it or dropping it in a machine. Which is exactly
    /// why the marker must not be on things that are not contraband.
    /// </summary>
    [Test]
    public async Task NeitherFlatpackerNorItsBoardIsSaveContraband()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var protoMan = server.ResolveDependency<IPrototypeManager>();

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(HasContraband(protoMan, FlatpackerProtoId), Is.False,
                    $"{FlatpackerProtoId} is marked SavingContraband again, so it is purged from every ship save.");
                Assert.That(HasContraband(protoMan, BoardProtoId), Is.False,
                    $"{BoardProtoId} is marked SavingContraband again, so a saved flatpacker loads with an empty board slot.");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The round trip, in the messiest realistic state the machine reaches: a board in the slot, stored
    /// materials, mid-pack, and an ore silo link pointing off the grid. Every one of those is normal
    /// play. The silo link is the one with teeth, and it is covered in its own right by OreSiloSaveTest.
    /// </summary>
    [Test]
    public async Task LoadedFlatpackerSurvivesSaveAndLoad()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var saveSystem = entMan.System<ShipyardGridSaveSystem>();
        var containers = entMan.System<SharedContainerSystem>();
        var materials = entMan.System<SharedMaterialStorageSystem>();
        var mapLoader = entMan.System<MapLoaderSystem>();

        var map = await pair.CreateTestMap();

        EntityUid flatpacker = default, board = default, silo = default;
        await server.WaitPost(() =>
        {
            flatpacker = SpawnAnchored(entMan, FlatpackerProtoId, map);

            board = entMan.SpawnEntity(BoardProtoId, map.GridCoords);
            containers.Insert(board, containers.GetContainer(flatpacker, "board_slot"));

            materials.TryChangeMaterialAmount(flatpacker, "Steel", 900);
            materials.TryChangeMaterialAmount(flatpacker, "Glass", 300);

            silo = entMan.SpawnEntity(SiloProtoId, new MapCoordinates(new Vector2(6, 6), map.MapId));

            // Packing state and the silo link are [Access]-restricted to their owning systems. Driving
            // them through those systems would mean running a real pack job and a real link handshake;
            // the state is what matters here, not how it got there.
#pragma warning disable RA0002
            var creator = entMan.GetComponent<FlatpackCreatorComponent>(flatpacker);
            creator.Packing = true;
            creator.PackEndTime = System.TimeSpan.FromMinutes(5);
            entMan.EnsureComponent<OreSiloClientComponent>(flatpacker).Silo = silo;
            entMan.EnsureComponent<OreSiloComponent>(silo).Clients.Add(flatpacker);
#pragma warning restore RA0002
        });

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(entMan.Deleted(flatpacker), Is.False, "Fixture: the flatpacker was already gone.");
                Assert.That(containers.GetContainer(flatpacker, "board_slot").ContainedEntities, Is.Not.Empty,
                    "Fixture: the board did not go into the slot.");
                Assert.That(entMan.GetComponent<TransformComponent>(silo).GridUid, Is.Not.EqualTo(map.Grid.Owner),
                    "Fixture: the silo must be off-grid for the link to be the interesting kind.");
            });
        });

        string? yaml = null;
        await server.WaitAssertion(() =>
        {
            Assert.That(saveSystem.TryBuildShipSaveYaml(map.Grid.Owner, out yaml, out _), Is.True,
                "The save refused the grid outright.");
            Assert.Multiple(() =>
            {
                Assert.That(entMan.Deleted(flatpacker), Is.False,
                    "The flatpacker should survive the purge: it is anchored, static-bodied and no longer contraband.");
                Assert.That(entMan.Deleted(board), Is.False,
                    "The board should survive inside the machine slot.");
                Assert.That(yaml, Does.Contain(FlatpackerProtoId), "The flatpacker should be in the save.");
            });
        });

        await server.WaitAssertion(() =>
        {
            var opts = new MapLoadOptions { MergeMap = map.MapId, Offset = new Vector2(100, 100) };
            Assert.That(mapLoader.TryLoadGeneric(new System.IO.StringReader(yaml!), "flatpacker-save-test", out var loaded, opts),
                Is.True, "The saved ship did not load back.");

            var loadedFlatpackers = loaded!.Entities
                .Where(e => entMan.HasComponent<FlatpackCreatorComponent>(e))
                .ToList();

            Assert.That(loadedFlatpackers, Has.Count.EqualTo(1), "Exactly one flatpacker should come back.");

            var uid = loadedFlatpackers[0];
            Assert.Multiple(() =>
            {
                Assert.That(entMan.GetComponent<MaterialStorageComponent>(uid).Storage["Steel"], Is.EqualTo(900),
                    "Stored materials should survive the round trip.");
                Assert.That(containers.GetContainer(uid, "board_slot").ContainedEntities, Has.Count.EqualTo(1),
                    "The board in the slot should survive the round trip.");
            });
        });

        await pair.CleanReturnAsync();
    }

    private static bool HasContraband(IPrototypeManager protoMan, string protoId)
    {
        Assert.That(protoMan.TryIndex<EntityPrototype>(protoId, out var proto), Is.True,
            $"Prototype {protoId} does not exist, so this guard is testing nothing.");

        return proto!.Components.ContainsKey("SavingContraband");
    }

    /// <summary>
    /// The flatpacker prototype already sets Transform.anchored, so it spawns anchored onto the test
    /// grid. Anchoring it a second time trips a debug assert inside AddToSnapGridCell rather than
    /// no-opping, so only anchor if the spawn did not.
    /// </summary>
    private static EntityUid SpawnAnchored(IEntityManager entMan, string protoId, Robust.UnitTesting.Pool.TestMapData map)
    {
        var uid = entMan.SpawnEntity(protoId, map.GridCoords);

        if (!entMan.GetComponent<TransformComponent>(uid).Anchored)
            entMan.System<SharedTransformSystem>().AnchorEntity(uid);

        return uid;
    }
}
