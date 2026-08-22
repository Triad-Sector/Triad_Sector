// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

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

namespace Content.IntegrationTests.Tests._Triad.Shipyard;

/// <summary>
/// Establishes whether the flatpacker is actually unsaveable, or merely listed as unsaveable.
///
/// It carries SavingContraband, so the ship save deletes it before serializing. It has carried some
/// form of that exclusion since HardLight PR #876 (2026-04-07), where it was added to a list captioned
/// "obvious non-ship entities" under the heading "Uplinks and bundled items", inside a commit about an
/// unrelated MindContainer bug. It was migrated to the component by 227d5f6ab5, whose own comment says
/// the list mixes entries that are "illegal to own" with entries "causing problems with ship saving"
/// and does not say which any given entry is. No technical reason is recorded anywhere.
///
/// So this asks the machine directly: strip the marker, put the thing in the messiest realistic state
/// it can reach, and run the real save path over it.
/// </summary>
[TestFixture]
[TestOf(typeof(ShipyardGridSaveSystem))]
public sealed class FlatpackerSaveTest
{
    private const string FlatpackerProtoId = "MachineFlatpacker";
    private const string BoardProtoId = "FlatpackerMachineCircuitboard";
    private const string SiloProtoId = "MachineMaterialSilo";

    /// <summary>
    /// The flatpacker is deleted by the purge before serialization even starts, and this pins the
    /// mechanism: the SavingContraband test in IsInvalidEntity runs BEFORE the anchored and
    /// static-body checks that would otherwise preserve a machine, so being anchored does not save it.
    /// </summary>
    [Test]
    public async Task FlatpackerIsPurgedFromShipSavesToday()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var saveSystem = entMan.System<ShipyardGridSaveSystem>();

        var map = await pair.CreateTestMap();

        EntityUid flatpacker = default;
        await server.WaitPost(() => flatpacker = SpawnAnchored(entMan, FlatpackerProtoId, map));

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(entMan.HasComponent<SavingContrabandComponent>(flatpacker), Is.True,
                    "Fixture: the flatpacker is expected to carry the marker. If this fails it has already been re-enabled.");
                Assert.That(entMan.GetComponent<TransformComponent>(flatpacker).Anchored, Is.True,
                    "Fixture: anchored, which is what would normally preserve a machine through the purge.");
            });
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(saveSystem.TryBuildShipSaveYaml(map.Grid.Owner, out var yaml, out _), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(entMan.Deleted(flatpacker), Is.True,
                    "The marker should delete it despite being anchored: the contraband test precedes the anchor test.");
                Assert.That(yaml, Does.Not.Contain(FlatpackerProtoId),
                    "A purged flatpacker must not appear in the save.");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The actual question. With the marker removed, a flatpacker holding a board, carrying stored
    /// materials, mid-pack, and pointed at an ore silo on another grid is saved and loaded back. Every
    /// one of those is a state the machine reaches in normal play, and the ore silo link is the one
    /// with real teeth: it is an EntityUid DataField that can point off the grid, the same shape that
    /// made DeviceLinkSource the top cause of failed saves.
    /// </summary>
    [Test]
    public async Task FlatpackerSavesCleanlyOnceTheMarkerIsRemoved()
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

            // Re-enabling the flatpacker means deleting the SavingContraband lines from its prototype.
            // Doing it at runtime keeps the test honest about what the change would actually be.
            entMan.RemoveComponent<SavingContrabandComponent>(flatpacker);

            // A board sitting in the slot, which is how the machine spends most of its working life.
            // The board carries its OWN marker and needs its own removal; see the test below.
            board = entMan.SpawnEntity(BoardProtoId, map.GridCoords);
            entMan.RemoveComponent<SavingContrabandComponent>(board);
            var slot = containers.GetContainer(flatpacker, "board_slot");
            containers.Insert(board, slot);

            // Stored materials.
            materials.TryChangeMaterialAmount(flatpacker, "Steel", 900);
            materials.TryChangeMaterialAmount(flatpacker, "Glass", 300);

            // Mid-pack, so the runtime packing state is non-default when the writer sees it.
            var creator = entMan.GetComponent<FlatpackCreatorComponent>(flatpacker);

            // An ore silo on the map rather than the grid: a live EntityUid the save set cannot contain.
            silo = entMan.SpawnEntity(SiloProtoId, new MapCoordinates(new Vector2(6, 6), map.MapId));

            // These four members are [Access]-restricted to their owning systems. Reaching past that is
            // the point here: the test has to put the component into a state the writer will see, and
            // going through the systems would mean driving a real pack job and a real silo link.
#pragma warning disable RA0002
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
                Assert.That(entMan.GetComponent<TransformComponent>(silo).GridUid, Is.Not.EqualTo(map.Grid.Owner),
                    "Fixture: the silo must be off the grid for the reference to be the interesting kind.");
                Assert.That(containers.GetContainer(flatpacker, "board_slot").ContainedEntities, Is.Not.Empty,
                    "Fixture: the board did not go into the slot.");
                Assert.That(entMan.GetComponent<MaterialStorageComponent>(flatpacker).Storage, Is.Not.Empty,
                    "Fixture: no materials were stored.");
            });
        });

        string? yaml = null;
        await server.WaitAssertion(() =>
        {
            Assert.That(saveSystem.TryBuildShipSaveYaml(map.Grid.Owner, out yaml, out _), Is.True,
                "The save refused the grid outright.");
            Assert.That(entMan.Deleted(flatpacker), Is.False,
                "Without the marker the flatpacker should survive the purge: it is anchored and static-bodied.");
            Assert.That(yaml, Does.Contain(FlatpackerProtoId),
                "The flatpacker should be present in the save now that it is not contraband.");
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

    /// <summary>
    /// The gotcha for anyone re-enabling this: FlatpackerMachineCircuitboard carries its own
    /// SavingContraband, declared in the same commit with the same examine text. Un-marking only the
    /// machine gets you a flatpacker that saves and loads with an empty board slot, because the
    /// contraband test in IsInvalidEntity precedes the IsInsidePersistentStorage check that would
    /// otherwise preserve a machine's slot contents. Re-enabling the flatpacker means un-marking both.
    /// </summary>
    [Test]
    public async Task BoardIsPurgedSeparatelyWhenOnlyTheMachineIsUnmarked()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var saveSystem = entMan.System<ShipyardGridSaveSystem>();
        var containers = entMan.System<SharedContainerSystem>();

        var map = await pair.CreateTestMap();

        EntityUid flatpacker = default, board = default;
        await server.WaitPost(() =>
        {
            flatpacker = SpawnAnchored(entMan, FlatpackerProtoId, map);
            entMan.RemoveComponent<SavingContrabandComponent>(flatpacker);

            // Deliberately left marked.
            board = entMan.SpawnEntity(BoardProtoId, map.GridCoords);
            containers.Insert(board, containers.GetContainer(flatpacker, "board_slot"));
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<SavingContrabandComponent>(board), Is.True,
                "Fixture: the board is expected to carry its own marker.");
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(saveSystem.TryBuildShipSaveYaml(map.Grid.Owner, out _, out _), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(entMan.Deleted(flatpacker), Is.False, "The un-marked machine should survive.");
                Assert.That(entMan.Deleted(board), Is.True,
                    "The board should still be purged out of the slot on its own marker.");
                Assert.That(containers.GetContainer(flatpacker, "board_slot").ContainedEntities, Is.Empty,
                    "Which leaves the surviving flatpacker with an empty slot.");
            });
        });

        await pair.CleanReturnAsync();
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
