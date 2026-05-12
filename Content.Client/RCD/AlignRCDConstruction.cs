using System.Collections.Generic;
using System.Numerics;
using Content.Client.Gameplay;
using Robust.Client.GameObjects;
using Content.Shared.Hands.Components;
using Content.Shared.Interaction;
using Content.Shared.Maps;
using Content.Shared.RCD;
using Content.Shared.RCD.Components;
using Content.Shared.RCD.Systems;
using Robust.Client.Graphics;
using Robust.Client.Placement;
using Robust.Client.Player;
using Robust.Client.State;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client.RCD;

public sealed class AlignRCDConstruction : PlacementMode
{
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IPrototypeManager _protoManager = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefs = default!;
    private readonly SharedMapSystem _mapSystem;
    private readonly RCDSystem _rcdSystem;
    private readonly SpriteSystem _sprite;
    private readonly SharedTransformSystem _transformSystem;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IStateManager _stateManager = default!;

    private const float SearchBoxSize = 2f;
    private const float PlaceColorBaseAlpha = 0.5f;

    private EntityCoordinates _unalignedMouseCoords = default;
    private string? _lastRcdTilePreviewId;

    /// <summary>
    /// This placement mode is not on the engine because it is content specific (i.e., for the RCD)
    /// </summary>
    public AlignRCDConstruction(PlacementManager pMan) : base(pMan)
    {
        IoCManager.InjectDependencies(this);
        _mapSystem = _entityManager.System<SharedMapSystem>();
        _rcdSystem = _entityManager.System<RCDSystem>();
        _sprite = _entityManager.System<SpriteSystem>();
        _transformSystem = _entityManager.System<SharedTransformSystem>();

        ValidPlaceColor = ValidPlaceColor.WithAlpha(PlaceColorBaseAlpha);
    }

    public override void AlignPlacementMode(ScreenCoordinates mouseScreen)
    {
        _unalignedMouseCoords = ScreenToCursorGrid(mouseScreen);
        MouseCoords = _unalignedMouseCoords.AlignWithClosestGridTile(SearchBoxSize, _entityManager, _mapManager);

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

            UpdateRcdTilePlacementPreview();
        }
        else
        {
            _lastRcdTilePreviewId = null;
            MouseCoords = new EntityCoordinates(MouseCoords.EntityId, new Vector2(CurrentTile.X + tileSize / 2 + pManager.PlacementOffset.X,
                CurrentTile.Y + tileSize / 2 + pManager.PlacementOffset.Y));
        }
    }

    /// <summary>
    /// Default tile placement only draws a generic overlay; swap in the real tile texture for RCD tile recipes.
    /// </summary>
    private void UpdateRcdTilePlacementPreview()
    {
        var player = _playerManager.LocalSession?.AttachedEntity;
        if (!_entityManager.TryGetComponent<HandsComponent>(player, out var hands) ||
            hands.ActiveHand?.HeldEntity is not { } held ||
            !_entityManager.TryGetComponent<RCDComponent>(held, out var rcd))
        {
            _lastRcdTilePreviewId = null;
            return;
        }

        var proto = _protoManager.Index(rcd.ProtoId);
        if (proto.Mode != RcdMode.ConstructTile)
            return;

        var tileTypeId = _rcdSystem.GetConstructTileTypeId(proto, pManager.Direction);
        if (tileTypeId == _lastRcdTilePreviewId)
            return;

        if (!_tileDefs.TryGetDefinition(tileTypeId, out var tDef) ||
            tDef is not ContentTileDefinition cTile ||
            cTile.Sprite is not { } spritePath)
        {
            _lastRcdTilePreviewId = null;
            return;
        }

        _lastRcdTilePreviewId = tileTypeId;

        // PlacementManager replaces the overlay sprite list (see CurrentTextures).
        pManager.CurrentTextures = new List<IDirectionalTextureProvider>
        {
            _sprite.RsiStateLike(new SpriteSpecifier.Texture(spritePath))
        };

        if (pManager.CurrentPermission != null)
            pManager.CurrentPermission.TileType = cTile.TileId;
    }

    public override bool IsValidPosition(EntityCoordinates position)
    {
        var player = _playerManager.LocalSession?.AttachedEntity;

        // If the destination is out of interaction range, set the placer alpha to zero
        if (!_entityManager.TryGetComponent<TransformComponent>(player, out var xform))
            return false;

        if (!_transformSystem.InRange(xform.Coordinates, position, SharedInteractionSystem.InteractionRange))
        {
            InvalidPlaceColor = InvalidPlaceColor.WithAlpha(0);
            return false;
        }

        // Otherwise restore the alpha value
        else
        {
            InvalidPlaceColor = InvalidPlaceColor.WithAlpha(PlaceColorBaseAlpha);
        }

        // Determine if player is carrying an RCD in their active hand
        if (!_entityManager.TryGetComponent<HandsComponent>(player, out var hands))
            return false;

        var heldEntity = hands.ActiveHand?.HeldEntity;

        if (!_entityManager.TryGetComponent<RCDComponent>(heldEntity, out var rcd))
            return false;

        // Retrieve the map grid data for the position
        if (!_rcdSystem.TryGetMapGridData(position, player, out var mapGridData))
            return false;

        // Determine if the user is hovering over a target
        var currentState = _stateManager.CurrentState;

        if (currentState is not GameplayStateBase screen)
            return false;
        
        var target = screen.GetClickedEntity(_transformSystem.ToMapCoordinates(_unalignedMouseCoords));

        // Determine if the RCD operation is valid or not
        if (!_rcdSystem.IsRCDOperationStillValid(heldEntity.Value, rcd, mapGridData.Value, target, player.Value, false,
                tilePlacementDirection: pManager.Direction))
            return false;

        return true;
    }
}
