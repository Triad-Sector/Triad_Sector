// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Serialization;

namespace Content.Shared._Triad.ShipSize;

/// <summary>
/// Size classification for a ship's hull, derived from its built (non-empty) tile count.
/// </summary>
[Serializable, NetSerializable]
public enum ShipSizeClass : byte
{
    Cutter,
    Corvette,
    Frigate,
    Cruiser,
    Capital,
    SuperCapital,
}
