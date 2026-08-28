// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

#nullable enable

using System.Linq;
using System.Numerics;
using Content.Server._Triad.Shipyard;
using Content.Shared.Materials.OreSilo;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Triad.Shipyard;

/// <summary>
/// The ore-silo link used to persist BOTH halves: OreSiloClientComponent.Silo and
/// OreSiloComponent.Clients were each a DataField. A ship save carries whichever half sits on the
/// grid, so a silo saved without its clients, or a client saved without its silo, deserialized a uid
/// that resolves to entity 0. Every downstream lookup then logged a resolve error with a full stack
/// trace, which is where the prod "invalid EntityUid reference ... component: OreSilo" lines came
/// from, and one of the guards written against it recorded millions of errors a day.
///
/// The subsystem had accumulated six defensive Exists() checks at the read sites. Only the client
/// half is persisted now; the silo's client set is rebuilt from it on ComponentStartup, which is the
/// same shape the engine uses for DeviceLinkSinkComponent.LinkedSources. The save additionally clears
/// links whose silo is not coming along, so the file never contains an unresolvable reference at all.
///
/// These two tests pin both halves of that: the normal case still works, and the boundary case is
/// severed cleanly rather than written and cleaned up afterwards.
/// </summary>
[TestFixture]
[TestOf(typeof(SharedOreSiloSystem))]
public sealed class OreSiloSaveTest
{
    private const string SiloProtoId = "MachineMaterialSilo";
    private const string ClientProtoId = "MachineFlatpacker";

    /// <summary>
    /// Silo and client on the same grid: the link is inside the cut, so it must survive intact. This
    /// is the test that would fail if dropping [DataField] from Clients had broken the normal case,
    /// because the set is now rebuilt rather than restored.
    /// </summary>
    [Test]
    public async Task SameGridSiloLinkSurvivesSaveAndLoad()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var saveSystem = entMan.System<ShipyardGridSaveSystem>();
        var mapLoader = entMan.System<MapLoaderSystem>();

        var map = await pair.CreateTestMap();

        EntityUid silo = default, client = default;
        await server.WaitPost(() =>
        {
            silo = SpawnAnchored(entMan, SiloProtoId, map);
            client = SpawnAnchored(entMan, ClientProtoId, map);

#pragma warning disable RA0002
            entMan.EnsureComponent<OreSiloClientComponent>(client).Silo = silo;
            entMan.EnsureComponent<OreSiloComponent>(silo).Clients.Add(client);
#pragma warning restore RA0002
        });

        string? yaml = null;
        await server.WaitAssertion(() =>
        {
            Assert.That(saveSystem.TryBuildShipSaveYaml(map.Grid.Owner, out yaml, out _), Is.True);
            Assert.That(entMan.GetComponent<OreSiloClientComponent>(client).Silo, Is.EqualTo(silo),
                "A link wholly inside the save must not be cleared: both ends are coming along.");
        });

        await server.WaitAssertion(() =>
        {
            var opts = new MapLoadOptions { MergeMap = map.MapId, Offset = new Vector2(100, 100) };
            Assert.That(mapLoader.TryLoadGeneric(new System.IO.StringReader(yaml!), "silo-save-test", out var loaded, opts),
                Is.True, "The saved ship did not load back.");

            var loadedSilos = loaded!.Entities.Where(e => entMan.HasComponent<OreSiloComponent>(e)).ToList();
            var loadedClients = loaded.Entities
                .Where(e => entMan.TryGetComponent<OreSiloClientComponent>(e, out var c) && c.Silo != null)
                .ToList();

            Assert.Multiple(() =>
            {
                Assert.That(loadedSilos, Has.Count.EqualTo(1), "The silo should come back.");
                Assert.That(loadedClients, Has.Count.EqualTo(1), "The client should come back still linked.");
            });

            var loadedSilo = loadedSilos[0];
            var loadedClient = loadedClients[0];

            Assert.Multiple(() =>
            {
                Assert.That(entMan.GetComponent<OreSiloClientComponent>(loadedClient).Silo, Is.EqualTo(loadedSilo),
                    "The client should point at the silo that came back with it.");
                Assert.That(entMan.GetComponent<OreSiloComponent>(loadedSilo).Clients, Does.Contain(loadedClient),
                    "The silo's client set is no longer persisted and must be rebuilt from the client on startup. " +
                    "An empty set here means OnClientStartup did not run or did not find the silo.");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Silo off the grid: the link crosses the boundary of the cut and is severed before serializing,
    /// so the file never contains a reference it cannot resolve.
    ///
    /// Severing loses nothing. SharedOreSiloSystem.CanTransmitMaterials refuses any pair whose grids
    /// differ, so a cross-grid link already transmits nothing while the ship is still sitting there.
    /// </summary>
    [Test]
    public async Task OffGridSiloLinkIsClearedBeforeSerializing()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var saveSystem = entMan.System<ShipyardGridSaveSystem>();
        var mapLoader = entMan.System<MapLoaderSystem>();

        var map = await pair.CreateTestMap();

        EntityUid silo = default, client = default;
        await server.WaitPost(() =>
        {
            client = SpawnAnchored(entMan, ClientProtoId, map);

            // Off the grid's tiles, so the engine parents it to the map instead.
            silo = entMan.SpawnEntity(SiloProtoId, new MapCoordinates(new Vector2(6, 6), map.MapId));

#pragma warning disable RA0002
            entMan.EnsureComponent<OreSiloClientComponent>(client).Silo = silo;
            entMan.EnsureComponent<OreSiloComponent>(silo).Clients.Add(client);
#pragma warning restore RA0002
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<TransformComponent>(silo).GridUid, Is.Not.EqualTo(map.Grid.Owner),
                "Fixture: the silo landed on the grid, so this is not testing a boundary crossing.");
        });

        string? yaml = null;
        await server.WaitAssertion(() =>
        {
            Assert.That(saveSystem.TryBuildShipSaveYaml(map.Grid.Owner, out yaml, out _), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(entMan.GetComponent<OreSiloClientComponent>(client).Silo, Is.Null,
                    "The off-grid link should be cleared from the client before serializing.");
                Assert.That(entMan.GetComponent<OreSiloComponent>(silo).Clients, Does.Not.Contain(client),
                    "Clearing should drop the client from the silo's set too, not just null the client half.");
                Assert.That(entMan.Deleted(silo), Is.False,
                    "Clearing a link must never delete the entity on the other end.");
            });
        });

        await server.WaitAssertion(() =>
        {
            var opts = new MapLoadOptions { MergeMap = map.MapId, Offset = new Vector2(100, 100) };
            Assert.That(mapLoader.TryLoadGeneric(new System.IO.StringReader(yaml!), "silo-offgrid-test", out var loaded, opts),
                Is.True, "The saved ship did not load back.");

            var linked = loaded!.Entities
                .Where(e => entMan.TryGetComponent<OreSiloClientComponent>(e, out var c) && c.Silo != null)
                .ToList();

            Assert.That(linked, Is.Empty,
                "Nothing in the loaded ship should carry a silo link: the only one it had pointed off-grid.");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Both prototypes set Transform.anchored, so they spawn anchored onto the test grid. Anchoring a
    /// second time trips a debug assert inside AddToSnapGridCell rather than no-opping.
    /// </summary>
    private static EntityUid SpawnAnchored(IEntityManager entMan, string protoId, Robust.UnitTesting.Pool.TestMapData map)
    {
        var uid = entMan.SpawnEntity(protoId, map.GridCoords);

        if (!entMan.GetComponent<TransformComponent>(uid).Anchored)
            entMan.System<SharedTransformSystem>().AnchorEntity(uid);

        return uid;
    }
}
