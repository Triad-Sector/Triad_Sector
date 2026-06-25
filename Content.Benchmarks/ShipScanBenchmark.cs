// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using Content.IntegrationTests;
using Content.IntegrationTests.Pair;
using Robust.Shared;
using Robust.Shared.Analyzers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.UnitTesting.Pool;

namespace Content.Benchmarks;

/// <summary>
/// Measures the cost of the NPC ship steering obstacle scan's dominant operation
/// (<c>FindGridsIntersecting</c> over a rotated AABB), as a function of how many grids are in the
/// sector. ShipSteeringSystem ran this every physics frame per NPC ship; the Triad throttle reuses
/// the cached result on a short interval, so the per-ship steering-scan rate drops by roughly the
/// frame-rate / interval ratio (e.g. ~6x at 60fps with a 0.1s interval). This benchmark quantifies
/// the per-scan cost that throttling avoids; GridCount mirrors a sparse-to-busy sector.
/// </summary>
[Virtual]
public class ShipScanBenchmark
{
    [Params(10, 50, 150)]
    public int GridCount { get; set; }

    private TestPair _pair = default!;
    private IEntityManager _entMan = default!;
    private IMapManager _mapMan = default!;
    private MapId _mapId;
    private Box2Rotated _queryBounds;
    private List<Entity<MapGridComponent>> _grids = new();

    [GlobalSetup]
    public void Setup()
    {
        ProgramShared.PathOffset = "../../../../";
        PoolManager.Startup();

        _pair = PoolManager.GetServerClient(testContext: new ExternalTestContext(nameof(ShipScanBenchmark), TextWriter.Null))
            .GetAwaiter().GetResult();
        _entMan = _pair.Server.ResolveDependency<IEntityManager>();
        _mapMan = _pair.Server.ResolveDependency<IMapManager>();
        var mapSys = _entMan.System<SharedMapSystem>();
        var xformSys = _entMan.System<SharedTransformSystem>();

        var map = _pair.CreateTestMap().GetAwaiter().GetResult();
        _mapId = map.MapId;

        _pair.Server.WaitPost(() =>
        {
            // Start from a clean map so we control the grid count exactly.
            _entMan.DeleteEntity(map.Grid);

            // A small 2x2 grid block so each grid has a real AABB in the grid broadphase tree.
            var tiles = new List<(Vector2i, Tile)>
            {
                (new Vector2i(0, 0), new Tile(1)),
                (new Vector2i(1, 0), new Tile(1)),
                (new Vector2i(0, 1), new Tile(1)),
                (new Vector2i(1, 1), new Tile(1)),
            };

            var perRow = (int) Math.Ceiling(Math.Sqrt(GridCount));
            const float spacing = 40f;
            for (var i = 0; i < GridCount; i++)
            {
                var grid = _mapMan.CreateGridEntity(_mapId);
                mapSys.SetTiles(grid.Owner, grid.Comp, tiles);
                var pos = new Vector2((i % perRow) * spacing, (i / perRow) * spacing);
                xformSys.SetWorldPosition(grid.Owner, pos);
            }

            // A rotated scan box that covers the whole grid cluster, mirroring the steering scan shape.
            var extent = perRow * spacing + 100f;
            var box = new Box2(-100f, -100f, extent, extent);
            _queryBounds = new Box2Rotated(box, Angle.FromDegrees(30), Vector2.Zero);
        }).Wait();

        // Let the grids register in the map's grid broadphase tree.
        _pair.Server.WaitRunTicks(2).Wait();
    }

    [GlobalCleanup]
    public async System.Threading.Tasks.Task Cleanup()
    {
        await _pair.DisposeAsync();
        PoolManager.Shutdown();
    }

    [Benchmark]
    public int FindGrids()
    {
        _grids.Clear();
        _mapMan.FindGridsIntersecting(_mapId, _queryBounds, ref _grids, approx: true, includeMap: false);
        return _grids.Count;
    }
}
