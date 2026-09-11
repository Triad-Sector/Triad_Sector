using Content.Client.Construction;
using Content.Client.RPD;
using Content.Shared.Atmos.Components;
using Content.Shared.Atmos.EntitySystems;
using Content.Shared.Hands.Components;
using Content.Shared.Input;
using Content.Shared.Interaction;
using Content.Shared.Maps;
using Content.Shared.RCD;
using Content.Shared.RCD.Components;
using Content.Shared.RCD.Systems;
using Content.Shared.RPD;
using Content.Shared.RPD.Components;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Placement;
using Robust.Client.Player;
using Robust.Shared.Enums;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client.RCD;

public sealed partial class RCDConstructionGhostSystem : EntitySystem
{
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private IPrototypeManager _protoManager = default!;
    [Dependency] private IPlacementManager _placementManager = default!;
    [Dependency] private ITileDefinitionManager _tileDefs = default!;
    [Dependency] private RCDSystem _rcdSystem = default!;
    [Dependency] private IEyeManager _eyeManager = default!;
    [Dependency] private IOverlayManager _overlayManager = default!;
    // Triad: deconstruct mode computes its own cursor-aimed layer (no placement mode runs), so it needs cursor +
    // grid access the construct placement mode gets for free.
    [Dependency] private IInputManager _inputManager = default!;
    [Dependency] private SharedMapSystem _mapSystem = default!;
    [Dependency] private SharedTransformSystem _transformSystem = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedAtmosPipeLayersSystem _pipeLayers = default!;

    private string _placementMode = typeof(AlignRCDConstruction).Name;
    // Triad: RPD port from funky-station — pipe-layer-aware ghost for RPDs + mirror-prototype flip toggle.
    private readonly string _rpdPlacementMode = typeof(AlignRPDAtmosPipeLayers).Name;
    private bool _useMirrorPrototype;
    // Tracks the held RCD/RPD so we can re-sync _useMirrorPrototype to the tool's networked state on swap
    // (otherwise the local "flip on" state from the previous tool leaks onto a freshly equipped one).
    private EntityUid? _lastHeldRcd;
    // End Triad

    // Triad: the direction and deconstruct-layer streams reconcile against the tool's networked state instead of a
    // fire-and-forget "last sent" cache: a value is sent once when the ghost changes it, then again every
    // ResendInterval while the tool still disagrees, so a select the server dropped heals instead of sticking until
    // the operator changes it again. Both stream from FrameUpdate, never Update: an event raised inside a tick shares
    // the tick with that tick's input commands and sorts ahead of them on the server, so it raced the very hand
    // swap that made the tool active (the seed-from-networked-state cache this replaces sent on that exact tick).
    private static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(0.5);
    private Direction? _lastSentDirection;
    private TimeSpan _nextDirectionResend;
    private AtmosPipeLayer? _lastSentLayer;
    private TimeSpan _nextLayerResend;
    // End Triad

    // Triad: RPD port from funky-station — bind R (EditorFlipObject) to toggle the mirrored variant of the
    // currently selected RCD recipe (e.g. gas filter flipped). Mirror state is networked to the server via
    // RCDConstructionGhostFlipEvent so the next placement spawns the right entity.
    //
    // BindBefore(ConstructionSystem): ConstructionSystem also binds EditorFlipObject and returns true
    // unconditionally on KeyDown (see ConstructionSystem.HandleFlip), which would swallow R before this
    // handler ever ran. Without an ordering declaration the engine resolves to registration order, so R
    // working with an RPD was previously luck. Each decline path here returns false so non-flippable RCD
    // recipes still fall through to ConstructionSystem (which no-ops when no construction ghost is active).
    public override void Initialize()
    {
        base.Initialize();

        CommandBinds.Builder
            .BindBefore(ContentKeyFunctions.EditorFlipObject,
                new PointerInputCmdHandler(HandleFlip, outsidePrediction: true),
                typeof(ConstructionSystem))
            .Register<RCDConstructionGhostSystem>();

        // Triad: the layer-aim guide dots for deconstruct mode (construct draws its own via the placement mode).
        _overlayManager.AddOverlay(new RPDDeconstructLayerGuideOverlay());
    }

    public override void Shutdown()
    {
        CommandBinds.Unregister<RCDConstructionGhostSystem>();
        _overlayManager.RemoveOverlay<RPDDeconstructLayerGuideOverlay>();
        base.Shutdown();
    }

    private bool HandleFlip(in PointerInputCmdHandler.PointerInputCmdArgs args)
    {
        if (args.State != BoundKeyState.Down)
            return false;

        if (!_placementManager.IsActive || _placementManager.Eraser)
            return false;

        var placerEntity = _placementManager.CurrentPermission?.MobUid;
        if (!TryComp<RCDComponent>(placerEntity, out var rcd))
            return false;

        var prototype = _protoManager.Index(rcd.ProtoId);
        if (prototype.MirrorPrototype is not { } mirror)
            return false;

        // Toggle the local field rather than reading rcd.UseMirrorPrototype: the networked field lags by a
        // round-trip, so two fast R presses would both read the same pre-roundtrip value and send identical
        // payloads, leaving the operator stuck on the flipped variant.
        _useMirrorPrototype = !_useMirrorPrototype;
        RaiseNetworkEvent(new RCDConstructionGhostFlipEvent(GetNetEntity(placerEntity.Value), _useMirrorPrototype));

        // Force the next Update() pass to rebuild the placer with the flipped prototype.
        _placementManager.Clear();
        return true;
    }
    // End Triad

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Get current placer data
        var placerEntity = _placementManager.CurrentPermission?.MobUid;
        var placerProto = _placementManager.CurrentPermission?.EntityType;
        var placerIsRCD = HasComp<RCDComponent>(placerEntity);

        // Exit if erasing or the current placer is not an RCD (build mode is active)
        if (_placementManager.Eraser || (placerEntity != null && !placerIsRCD))
            return;

        // Determine if player is carrying an RCD in their active hand
        var player = _playerManager.LocalSession?.AttachedEntity;

        if (!TryComp<HandsComponent>(player, out var hands))
            return;

        var heldEntity = hands.ActiveHand?.HeldEntity;

        if (!TryComp<RCDComponent>(heldEntity, out var rcd))
        {
            // If the player was holding an RCD, but is no longer, cancel placement
            if (placerIsRCD)
                _placementManager.Clear();

            // Triad: drop the cached flip state so we don't leak it onto whatever tool the player picks up next.
            _lastHeldRcd = null;
            _useMirrorPrototype = false;
            _lastSentDirection = null;
            _lastSentLayer = null;
            // End Triad
            return;
        }

        // Triad: on tool swap, sync the local flip flag to the new tool's networked state. Within a single tool
        // we keep our own field as the source of truth (see HandleFlip race comment). The stream caches are
        // cleared so the fresh tool is not held to a resend window the previous one started; what it actually
        // needs is decided against its own networked state in FrameUpdate.
        if (_lastHeldRcd != heldEntity)
        {
            _lastHeldRcd = heldEntity;
            _useMirrorPrototype = rcd.UseMirrorPrototype;
            _lastSentDirection = null;
            _lastSentLayer = null;
        }
        // End Triad

        var prototype = _protoManager.Index(rcd.ProtoId);

        // Triad: the RPD deconstructs an existing pipe on click (via AfterInteract), so there's nothing to preview
        // in Deconstruct mode, and the construct-style whole-tile ghost reads as targeting the tile rather than the
        // pipe under it. Suppress the placer here; RCD deconstruct and RPD construct keep their ghost. The aimed
        // layer for deconstruct streams from FrameUpdate.
        if (HasComp<RPDComponent>(heldEntity) && prototype.Mode == RcdMode.Deconstruct)
        {
            if (placerIsRCD)
                _placementManager.Clear();

            return;
        }
        // End Triad

        // Triad: respect the flipped variant when the operator has toggled mirror (and the recipe defines one).
        var objectPrototype = (_useMirrorPrototype && prototype.MirrorPrototype is { } mirror)
            ? mirror.Id
            : prototype.Prototype ?? string.Empty;
        // End Triad

        var placementTileId = prototype.Mode == RcdMode.ConstructTile
            ? _rcdSystem.GetConstructTileTypeId(prototype, _placementManager.Direction)
            : objectPrototype;

        var placementTileNumeric = 0;
        if (prototype.Mode == RcdMode.ConstructTile &&
            !string.IsNullOrEmpty(placementTileId) &&
            _tileDefs.TryGetDefinition(placementTileId, out var placeDef))
        {
            placementTileNumeric = placeDef.TileId;
        }

        // If the placer has not changed, exit (tile ghosts must refresh when direction picks a different tile id)
        if (heldEntity == placerEntity && PlacerMatches(placementTileId, placerProto) && // Triad: layer alternatives count as the same placer
            _placementManager.CurrentPermission?.TileType == placementTileNumeric)
            return;

        // Create a new placer
        // Triad: RPD pipe-layer-aware placement when the held tool has the RPDComponent and the recipe is layer-capable.
        var placementMode = (HasComp<RPDComponent>(heldEntity) && !prototype.NoLayers) ? _rpdPlacementMode : _placementMode;
        // End Triad
        var newObjInfo = new PlacementInformation
        {
            MobUid = heldEntity.Value,
            PlacementOption = placementMode,
            EntityType = placementTileId,
            TileType = placementTileNumeric,
            Range = (int) Math.Ceiling(SharedInteractionSystem.InteractionRange),
            IsTile = (prototype.Mode == RcdMode.ConstructTile),
            UseEditorContext = false,
        };

        _placementManager.Clear();
        _placementManager.BeginPlacing(newObjInfo);
    }

    // Triad: the pipe-layer placement mode rewrites the placer's prototype to the aimed layer's alternative, so the
    // placer for a recipe is "up" when it shows the recipe's base prototype or any of that prototype's layer
    // alternatives. Comparing against the base alone tore the placer down and rebuilt it every tick while the
    // operator aimed off Primary: a layer select per tick per client, and the mode's state reset each time.
    private bool PlacerMatches(string placementTileId, string? placerProto)
    {
        if (placementTileId == placerProto)
            return true;

        if (string.IsNullOrEmpty(placerProto))
            return false;

        if (!_protoManager.TryIndex<EntityPrototype>(placementTileId, out var baseProto) ||
            !baseProto.TryComp<AtmosPipeLayersComponent>(out var layers, EntityManager.ComponentFactory))
        {
            return false;
        }

        foreach (var layer in Enum.GetValues<AtmosPipeLayer>())
        {
            if (_pipeLayers.TryGetAlternativePrototype(layers, layer, out var alt) && alt.Id == placerProto)
                return true;
        }

        return false;
    }

    // Triad: streams run here, after the frame's input commands have been stamped, and reconcile against the
    // tool's networked state; see the field comment.
    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        var player = _playerManager.LocalSession?.AttachedEntity;

        if (!TryComp<HandsComponent>(player, out var hands) ||
            hands.ActiveHand?.HeldEntity is not { } heldEntity ||
            !TryComp<RCDComponent>(heldEntity, out var rcd) ||
            !_protoManager.TryIndex(rcd.ProtoId, out var prototype))
        {
            return;
        }

        if (HasComp<RPDComponent>(heldEntity) && prototype.Mode == RcdMode.Deconstruct)
        {
            StreamDeconstructLayer(heldEntity);
            return;
        }

        // The placement manager's direction is what the ghost draws, but only while the placer belongs to this
        // tool; otherwise it is someone else's (a construction-menu ghost, admin placement).
        if (!_placementManager.IsActive || _placementManager.Eraser ||
            _placementManager.CurrentPermission?.MobUid != heldEntity)
        {
            return;
        }

        var wanted = _placementManager.Direction;
        if (rcd.ConstructionDirection == wanted)
            return;

        if (_lastSentDirection == wanted && _timing.RealTime < _nextDirectionResend)
            return;

        _lastSentDirection = wanted;
        _nextDirectionResend = _timing.RealTime + ResendInterval;
        RaiseNetworkEvent(new RCDConstructionGhostRotationEvent(GetNetEntity(heldEntity), wanted));
    }

    // Triad: deconstruct runs no placement mode, so compute the cursor-aimed pipe layer here (mirroring the construct
    // placement mode's math) and push it while the tool's networked layer disagrees. The server uses it to pick which
    // covered pipe to chew.
    private void StreamDeconstructLayer(EntityUid heldEntity)
    {
        if (!TryComp<RPDComponent>(heldEntity, out var rpd))
            return;

        var mouseScreen = _inputManager.MouseScreenPosition;
        if (!mouseScreen.IsValid)
            return;

        var mouseMap = _eyeManager.PixelToMap(mouseScreen.Position);
        if (!_mapSystem.TryFindGridAt(mouseMap, out var gridUid, out var grid))
            return;

        var localPos = System.Numerics.Vector2.Transform(mouseMap.Position, _transformSystem.GetInvWorldMatrix(gridUid));
        var tileSize = grid.TileSize;
        var indices = new Vector2i((int) MathF.Floor(localPos.X / tileSize), (int) MathF.Floor(localPos.Y / tileSize));
        var tileCenterLocal = new System.Numerics.Vector2((indices.X + 0.5f) * tileSize, (indices.Y + 0.5f) * tileSize);
        var mouseDiff = localPos - tileCenterLocal;

        var gridRotation = _transformSystem.GetWorldRotation(gridUid);
        var layer = RPDLayerMath.PickLayer(mouseDiff, _eyeManager.CurrentEye.Rotation, gridRotation);

        if (rpd.CurrentLayer == layer)
            return;

        if (_lastSentLayer == layer && _timing.RealTime < _nextLayerResend)
            return;

        _lastSentLayer = layer;
        _nextLayerResend = _timing.RealTime + ResendInterval;
        RaiseNetworkEvent(new RPDLayerSelectEvent(GetNetEntity(heldEntity), layer));
    }
}
