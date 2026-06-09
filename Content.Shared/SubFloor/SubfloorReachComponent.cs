// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;

namespace Content.Shared.SubFloor;

/// <summary>
/// Triad: marks an item whose holder may interact with subfloor entities that are under floor cover (which
/// normally cancel all interaction via <see cref="SubFloorHideComponent.BlockInteractions"/>). The RPD carries
/// this so its built-in t-ray reveal is matched by the ability to actually deconstruct the revealed pipe.
/// Scoped to the held tool on purpose, so it does not broaden subfloor access for every t-ray user.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class SubfloorReachComponent : Component;
