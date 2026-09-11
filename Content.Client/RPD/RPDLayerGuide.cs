// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Maths;

namespace Content.Client.RPD;

/// <summary>
/// Triad: shared draw for the RPD's three pipe-layer aim dots, center = Primary, two flanking = Secondary (NE/E)
/// and Tertiary (SW/W) on a screen-relative axis that flips with grid rotation. Used by the construct placement
/// preview (<see cref="AlignRPDAtmosPipeLayers"/>) and the deconstruct overlay
/// (<see cref="RPDDeconstructLayerGuideOverlay"/>) so both read identically. The upstream hand-placement mode
/// (AlignAtmosPipeLayers) keeps its own copy; we don't modify upstream for this.
/// </summary>
public static class RPDLayerGuide
{
    private const float Radius = 0.1f;

    // 7 px from tile center on a 32 px tile: room for three dots without overlapping the central one.
    private const float OffsetInTileUnits = 7f / 32f;

    private static readonly Color GuideColor = new(0, 0, 0.5785f);

    public static void Draw(DrawingHandleWorld handle, Vector2 tileCenterWorld, Angle gridRotation, Angle eyeRotation)
    {
        var direction = (eyeRotation + gridRotation + Math.PI / 2).GetCardinalDir();
        var multi = (direction == Direction.North || direction == Direction.South) ? -1f : 1f;
        var offset = gridRotation.RotateVec(new Vector2(multi * OffsetInTileUnits, OffsetInTileUnits));

        handle.DrawCircle(tileCenterWorld, Radius, GuideColor);
        handle.DrawCircle(tileCenterWorld + offset, Radius, GuideColor);
        handle.DrawCircle(tileCenterWorld - offset, Radius, GuideColor);
    }
}
