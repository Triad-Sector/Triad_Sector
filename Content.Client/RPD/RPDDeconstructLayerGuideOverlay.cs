// SPDX-FileCopyrightText: 2025 Steve <marlumpy@gmail.com>
// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared.Hands.Components;
using Content.Shared.Interaction;
using Content.Shared.RCD;
using Content.Shared.RCD.Components;
using Content.Shared.RPD;
using Content.Shared.RPD.Components;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.Enums;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.Client.RPD;

/// <summary>
/// Draws the three pipe-layer guide dots on the cursor tile while the operator holds an RPD in Deconstruct mode,
/// mirroring the construct placement preview in <see cref="AlignRPDAtmosPipeLayers"/>. Deconstruct runs no placement
/// mode (the tile ghost is suppressed in <c>RCDConstructionGhostSystem</c>), so the guide can't ride on the placer —
/// this overlay supplies it. Center dot = Primary, the two flanking dots = Secondary (NE/E side) and Tertiary (SW/W),
/// camera-relative just like construct, so the operator aims the same way they place.
/// </summary>
public sealed class RPDDeconstructLayerGuideOverlay : Overlay
{
    private readonly IEntityManager _entMan;
    private readonly IInputManager _input;
    private readonly IEyeManager _eye;
    private readonly IMapManager _mapManager;
    private readonly IPlayerManager _player;
    private readonly IPrototypeManager _proto;
    private readonly SharedMapSystem _mapSystem;
    private readonly SharedTransformSystem _transform;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    public RPDDeconstructLayerGuideOverlay()
    {
        _entMan = IoCManager.Resolve<IEntityManager>();
        _input = IoCManager.Resolve<IInputManager>();
        _eye = IoCManager.Resolve<IEyeManager>();
        _mapManager = IoCManager.Resolve<IMapManager>();
        _player = IoCManager.Resolve<IPlayerManager>();
        _proto = IoCManager.Resolve<IPrototypeManager>();
        _mapSystem = _entMan.System<SharedMapSystem>();
        _transform = _entMan.System<SharedTransformSystem>();
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (_player.LocalSession?.AttachedEntity is not { } player)
            return;

        // Only while holding an RPD whose current recipe is a layer-capable deconstruct.
        if (!_entMan.TryGetComponent<HandsComponent>(player, out var hands) ||
            hands.ActiveHand?.HeldEntity is not { } held)
            return;

        if (!_entMan.HasComponent<RPDComponent>(held) ||
            !_entMan.TryGetComponent<RCDComponent>(held, out var rcd))
            return;

        if (!_proto.TryIndex(rcd.ProtoId, out var recipe) || recipe.Mode != RcdMode.Deconstruct || recipe.NoLayers)
            return;

        var mouseScreen = _input.MouseScreenPosition;
        if (!mouseScreen.IsValid)
            return;

        var mouseMap = _eye.PixelToMap(mouseScreen.Position);
        if (mouseMap.MapId != args.MapId)
            return;

        if (!_mapManager.TryFindGridAt(mouseMap, out var gridUid, out var grid))
            return;

        // Hide the guide when the cursor is out of reach, matching the construct preview's range gate.
        var playerWorld = _transform.GetWorldPosition(player);
        if ((mouseMap.Position - playerWorld).Length() > SharedInteractionSystem.InteractionRange)
            return;

        // Snap the cursor to the tile center it sits over.
        var localPos = Vector2.Transform(mouseMap.Position, _transform.GetInvWorldMatrix(gridUid));
        var tileSize = grid.TileSize;
        var indices = new Vector2i((int) MathF.Floor(localPos.X / tileSize), (int) MathF.Floor(localPos.Y / tileSize));
        var tileCenterLocal = new Vector2((indices.X + 0.5f) * tileSize, (indices.Y + 0.5f) * tileSize);
        var worldPosition = _mapSystem.LocalToWorld(gridUid, grid, tileCenterLocal);

        // Three guide dots showing the cursor-quadrant layer aim; see RPDLayerGuide.
        var gridRotation = _transform.GetWorldRotation(gridUid);
        RPDLayerGuide.Draw(args.WorldHandle, worldPosition, gridRotation, _eye.CurrentEye.Rotation);
    }
}
