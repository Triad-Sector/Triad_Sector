// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Triad.ShipSize;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad
{
    [TestFixture]
    [TestOf(typeof(ShipSizeSystem))]
    public sealed class ShipSizeTest
    {
        [Test]
        public async Task GetBuiltTileCount_And_GetSizeClass_MatchKnownTileCount()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var entMan = server.EntMan;
            var mapSystem = entMan.System<SharedMapSystem>();
            var shipSizeSystem = entMan.System<ShipSizeSystem>();

            Entity<MapGridComponent> grid = default;

            await server.WaitAssertion(() =>
            {
                mapSystem.CreateMap(out var mapId);
                grid = mapSystem.CreateGridEntity(mapId);

                // Cutter ladder tops out at 96 tiles; 97 built tiles crosses into Corvette.
                for (var i = 0; i < 97; i++)
                {
                    mapSystem.SetTile(grid, new Vector2i(i, 0), new Tile(1));
                }
            });

            await server.WaitIdleAsync();

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(shipSizeSystem.GetBuiltTileCount(grid), Is.EqualTo(97));
                    Assert.That(shipSizeSystem.GetSizeClass(grid), Is.EqualTo(ShipSizeClass.Corvette));
                });
            });

            await pair.CleanReturnAsync();
        }
    }
}
