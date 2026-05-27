// SPDX-FileCopyrightText: 2025 Steve <marlumpy@gmail.com>
// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Client.Gameplay;
using Content.Shared.Atmos.Components;
using Content.Shared.Atmos.EntitySystems;
using Content.Shared.Hands.Components;
using Content.Shared.Interaction;
using Content.Shared.RCD.Components;
using Content.Shared.RCD.Systems;
using Content.Shared.RPD;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Placement;
using Robust.Client.Player;
using Robust.Client.State;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using static Robust.Client.Placement.PlacementManager;

namespace Content.Client.RPD;

/// <summary>
/// Placement mode for the Rapid Piping Device. Cursor position inside the tile picks the
/// <see cref="AtmosPipeLayer"/> for the placement (see <see cref="RPDLayerMath"/>); the chosen layer is applied to
/// the placement ghost via <c>TryGetAlternativePrototype</c>, and the same math runs server-side from the
/// streamed eye rotation when the player commits. Three guide circles render on the cursor tile so the operator
/// can see which layer they're aiming at.
/// </summary>
public sealed class AlignRPDAtmosPipeLayers : PlacementMode
{
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IPrototypeManager _protoManager = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IStateManager _stateManager = default!;
    [Dependency] private readonly IEyeManager _eyeManager = default!;
    [Dependency] private readonly IEntityNetworkManager _entityNetwork = default!;

    private readonly SharedMapSystem _mapSystem;
    private readonly SharedTransformSystem _transformSystem;
    private readonly SharedAtmosPipeLayersSystem _pipeLayersSystem;
    private readonly SpriteSystem _spriteSystem;
    private readonly RCDSystem _rcdSystem;

    private const float SearchBoxSize = 2f;
    private const float PlaceColorBaseAlpha = 0.5f;
    private const float GuideRadius = 0.1f;

    // 7 px from tile center on a 32 px tile — leaves room for three guide circles without overlapping the
    // central one.
    private const float GuideOffsetInTileUnits = 7f / 32f;

    // Per-instance (not static) so a tool swap doesn't leave stale layer/rotation state behind. A new placement
    // session starts at Primary with no eye rotation cached, forcing the first send.
    private EntityCoordinates _mouseCoordsRaw = default;
    private AtmosPipeLayer _currentLayer = AtmosPipeLayer.Primary;
    private float? _currentEyeRotation = null;
    private readonly Color _guideColor = new(0, 0, 0.5785f);

    public AlignRPDAtmosPipeLayers(PlacementManager pMan) : base(pMan)
    {
        IoCManager.InjectDependencies(this);
        _mapSystem = _entityManager.System<SharedMapSystem>();
        _transformSystem = _entityManager.System<SharedTransformSystem>();
        _spriteSystem = _entityManager.System<SpriteSystem>();
        _rcdSystem = _entityManager.System<RCDSystem>();
        _pipeLayersSystem = _entityManager.System<SharedAtmosPipeLayersSystem>();
        ValidPlaceColor = ValidPlaceColor.WithAlpha(PlaceColorBaseAlpha);
    }

    public override void Render(in OverlayDrawArgs args)
    {
        if (_playerManager.LocalSession?.AttachedEntity is not { } player ||
            !_entityManager.TryGetComponent<TransformComponent>(player, out var xform) ||
            !_transformSystem.InRange(xform.Coordinates, MouseCoords, SharedInteractionSystem.InteractionRange))
        {
            return;
        }

        var gridUid = _transformSystem.GetGrid(MouseCoords);
        if (gridUid == null || !_entityManager.TryGetComponent<MapGridComponent>(gridUid, out var grid))
            return;

        if (pManager.PlacementType == PlacementTypes.None)
        {
            // Draw three guide dots: center (Primary), and two flanking dots along the camera-relative cardinal
            // axis (Secondary on the NE/E side, Tertiary on SW/W). The flip via `multi` keeps the guides on a
            // consistent screen-relative axis as the grid rotates.
            var gridRotation = _transformSystem.GetWorldRotation(gridUid.Value);
            var worldPosition = _mapSystem.LocalToWorld(gridUid.Value, grid, MouseCoords.Position);
            var direction = (_eyeManager.CurrentEye.Rotation + gridRotation + Math.PI / 2).GetCardinalDir();
            var multi = (direction == Direction.North || direction == Direction.South) ? -1f : 1f;
            var offset = gridRotation.RotateVec(new Vector2(multi * GuideOffsetInTileUnits, GuideOffsetInTileUnits));

            args.WorldHandle.DrawCircle(worldPosition, GuideRadius, _guideColor);
            args.WorldHandle.DrawCircle(worldPosition + offset, GuideRadius, _guideColor);
            args.WorldHandle.DrawCircle(worldPosition - offset, GuideRadius, _guideColor);
        }

        base.Render(args);
    }

    public override void AlignPlacementMode(ScreenCoordinates mouseScreen)
    {
        _mouseCoordsRaw = ScreenToCursorGrid(mouseScreen);
        MouseCoords = _mouseCoordsRaw.AlignWithClosestGridTile(SearchBoxSize, _entityManager, _mapManager);

        var gridId = _transformSystem.GetGrid(MouseCoords);
        if (!_entityManager.TryGetComponent<MapGridComponent>(gridId, out var mapGrid))
            return;

        CurrentTile = _mapSystem.GetTileRef(gridId.Value, mapGrid, MouseCoords);

        float tileSize = mapGrid.TileSize;
        GridDistancing = tileSize;

        if (pManager.CurrentPermission!.IsTile)
        {
            MouseCoords = new EntityCoordinates(MouseCoords.EntityId, new Vector2(CurrentTile.X + tileSize / 2,
                CurrentTile.Y + tileSize / 2));
        }
        else
        {
            MouseCoords = new EntityCoordinates(MouseCoords.EntityId, new Vector2(CurrentTile.X + tileSize / 2 + pManager.PlacementOffset.X,
                CurrentTile.Y + tileSize / 2 + pManager.PlacementOffset.Y));
        }

        var mouseDiff = _mouseCoordsRaw.Position - MouseCoords.Position;
        var gridRotation = _transformSystem.GetWorldRotation(gridId.Value);
        var newLayer = RPDLayerMath.PickLayer(mouseDiff, _eyeManager.CurrentEye.Rotation, gridRotation);

        if (_playerManager.LocalSession?.AttachedEntity is { } player &&
            _entityManager.TryGetComponent<TransformComponent>(player, out var xform) &&
            _transformSystem.InRange(xform.Coordinates, MouseCoords, SharedInteractionSystem.InteractionRange) &&
            _entityManager.TryGetComponent<HandsComponent>(player, out var hands) &&
            hands.ActiveHand?.HeldEntity is { } heldEntity &&
            _entityManager.TryGetComponent<RCDComponent>(heldEntity, out _))
        {
            if (newLayer != _currentLayer)
                _currentLayer = newLayer;
            UpdateEyeRotation(heldEntity, _eyeManager.CurrentEye.Rotation);
        }

        UpdatePlacer(_currentLayer);
    }

    /// <summary>
    /// Sends eye rotation to the server only when it changes. Because <see cref="_currentEyeRotation"/> is an
    /// instance field initialized to <c>null</c>, the first call in a new placement session always sends — fixing
    /// the stale-eye-rotation-after-tool-swap window the static version had.
    /// </summary>
    private void UpdateEyeRotation(EntityUid heldEntity, Angle eyeRotation)
    {
        if (_currentEyeRotation == eyeRotation.Theta)
            return;
        _currentEyeRotation = (float) eyeRotation.Theta;
        _entityNetwork.SendSystemNetworkMessage(new RPDEyeRotationEvent(_entityManager.GetNetEntity(heldEntity), _currentEyeRotation));
    }

    private void UpdatePlacer(AtmosPipeLayer layer)
    {
        if (pManager.CurrentPermission?.EntityType == null)
            return;

        if (!_protoManager.TryIndex<EntityPrototype>(pManager.CurrentPermission.EntityType, out var currentProto))
            return;

        if (!currentProto.TryGetComponent<AtmosPipeLayersComponent>(out var atmosPipeLayers, _entityManager.ComponentFactory))
            return;

        if (!_pipeLayersSystem.TryGetAlternativePrototype(atmosPipeLayers, layer, out var newProtoId))
            return;

        if (!_protoManager.TryIndex<EntityPrototype>(newProtoId, out var newProto))
            return;

        pManager.CurrentPermission.EntityType = newProtoId;

        if (!newProto.TryGetComponent<SpriteComponent>(out var sprite, _entityManager.ComponentFactory))
            return;

        var textures = new List<IDirectionalTextureProvider>();
        foreach (var spriteLayer in sprite.AllLayers)
        {
            if (spriteLayer.ActualRsi?.Path != null && spriteLayer.RsiState.Name != null)
                textures.Add(_spriteSystem.RsiStateLike(new SpriteSpecifier.Rsi(spriteLayer.ActualRsi.Path, spriteLayer.RsiState.Name)));
        }
        pManager.CurrentTextures = textures;
    }

    public override bool IsValidPosition(EntityCoordinates position)
    {
        var player = _playerManager.LocalSession?.AttachedEntity;

        if (!_entityManager.TryGetComponent<TransformComponent>(player, out var xform))
            return false;

        if (!_transformSystem.InRange(xform.Coordinates, position, SharedInteractionSystem.InteractionRange))
        {
            InvalidPlaceColor = InvalidPlaceColor.WithAlpha(0);
            return false;
        }

        InvalidPlaceColor = InvalidPlaceColor.WithAlpha(PlaceColorBaseAlpha);

        if (!_entityManager.TryGetComponent<HandsComponent>(player, out var hands))
            return false;

        var heldEntity = hands.ActiveHand?.HeldEntity;
        if (!_entityManager.TryGetComponent<RCDComponent>(heldEntity, out var rcd))
            return false;

        if (!_rcdSystem.TryGetMapGridData(position, player, out var mapGridData))
            return false;

        if (_stateManager.CurrentState is not GameplayStateBase screen)
            return false;

        var target = screen.GetClickedEntity(_transformSystem.ToMapCoordinates(_mouseCoordsRaw));

        if (!_rcdSystem.IsRCDOperationStillValid(heldEntity.Value, rcd, mapGridData.Value, target, player.Value, false))
            return false;

        return true;
    }
}
