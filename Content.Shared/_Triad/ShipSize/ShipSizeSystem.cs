// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Map.Components;

namespace Content.Shared._Triad.ShipSize;

/// <summary>
/// Classifies ship hulls into a <see cref="ShipSizeClass"/> based on their built (non-empty) tile count.
/// </summary>
public sealed partial class ShipSizeSystem : EntitySystem
{
    [Dependency] private SharedMapSystem _map = default!;

    /// <summary>
    /// Maps a built tile count to its <see cref="ShipSizeClass"/>.
    /// Breakpoints are inclusive on the upper bound; 0 tiles maps to <see cref="ShipSizeClass.Cutter"/>.
    /// </summary>
    public static ShipSizeClass ClassFromTileCount(int tiles)
    {
        return tiles switch
        {
            <= 96 => ShipSizeClass.Cutter,
            <= 192 => ShipSizeClass.Corvette,
            <= 384 => ShipSizeClass.Frigate,
            <= 768 => ShipSizeClass.Cruiser,
            <= 1536 => ShipSizeClass.Capital,
            _ => ShipSizeClass.SuperCapital,
        };
    }

    /// <summary>
    /// Counts the built (non-empty) tiles on a grid.
    /// </summary>
    public int GetBuiltTileCount(Entity<MapGridComponent> grid)
    {
        return _map.GetFilledTileCount(grid);
    }

    /// <summary>
    /// Determines the <see cref="ShipSizeClass"/> of a grid from its built tile count.
    /// </summary>
    public ShipSizeClass GetSizeClass(Entity<MapGridComponent> grid)
    {
        return ClassFromTileCount(GetBuiltTileCount(grid));
    }
}
