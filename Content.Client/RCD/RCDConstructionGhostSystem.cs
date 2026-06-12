using Content.Client.Construction;
using Content.Client.RPD;
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
using Robust.Client.Placement;
using Robust.Client.Player;
using Robust.Shared.Enums;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Client.RCD;

public sealed class RCDConstructionGhostSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPrototypeManager _protoManager = default!;
    [Dependency] private readonly IPlacementManager _placementManager = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefs = default!;
    [Dependency] private readonly RCDSystem _rcdSystem = default!;
    [Dependency] private readonly IEyeManager _eyeManager = default!;
    [Dependency] private readonly IOverlayManager _overlayManager = default!;

    private string _placementMode = typeof(AlignRCDConstruction).Name;
    // Triad: RPD port from funky-station — pipe-layer-aware ghost for RPDs + mirror-prototype flip toggle.
    private readonly string _rpdPlacementMode = typeof(AlignRPDAtmosPipeLayers).Name;
    private bool _useMirrorPrototype;
    // Tracks the held RCD/RPD so we can re-sync _useMirrorPrototype to the tool's networked state on swap
    // (otherwise the local "flip on" state from the previous tool leaks onto a freshly equipped one).
    private EntityUid? _lastHeldRcd;
    // End Triad
    private Direction _placementDirection = default;
    // Triad: last eye rotation streamed while deconstructing; null forces a resend on tool swap / re-equip.
    private float? _lastSentEyeRotation;

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
            _lastSentEyeRotation = null;
            // End Triad
            return;
        }

        // Triad: on tool swap, sync the local flip flag to the new tool's networked state. Within a single tool
        // we keep our own field as the source of truth (see HandleFlip race comment).
        if (_lastHeldRcd != heldEntity)
        {
            _lastHeldRcd = heldEntity;
            _useMirrorPrototype = rcd.UseMirrorPrototype;
            _lastSentEyeRotation = null; // Triad: force a fresh eye-rotation send for the newly held tool.
        }
        // End Triad

        var prototype = _protoManager.Index(rcd.ProtoId);

        // Triad: the RPD deconstructs an existing pipe on click (via AfterInteract), so there's nothing to preview
        // in Deconstruct mode, and the construct-style whole-tile ghost reads as targeting the tile rather than the
        // pipe under it. Suppress the placer here; RCD deconstruct and RPD construct keep their ghost.
        if (HasComp<RPDComponent>(heldEntity) && prototype.Mode == RcdMode.Deconstruct)
        {
            if (placerIsRCD)
                _placementManager.Clear();

            // Triad: no placement mode runs in deconstruct mode, so stream eye rotation here the way the construct
            // placement mode does — the server reproduces the operator's cursor-aimed pipe layer from it to pick
            // which covered pipe to chew. On change only, to stay off the per-frame network path.
            StreamEyeRotation(heldEntity.Value);
            return;
        }
        // End Triad

        // Update the direction the RCD prototype based on the placer direction
        if (_placementDirection != _placementManager.Direction)
        {
            _placementDirection = _placementManager.Direction;
            RaiseNetworkEvent(new RCDConstructionGhostRotationEvent(GetNetEntity(heldEntity.Value), _placementDirection));
        }

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
        if (heldEntity == placerEntity && placementTileId == placerProto &&
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

    // Triad: mirror of AlignRPDAtmosPipeLayers.UpdateEyeRotation for deconstruct mode, which runs no placement mode.
    // Streams the local eye rotation to the server (on change) so RPDSystem can compute the cursor-aimed pipe layer
    // when resolving which covered pipe to deconstruct.
    private void StreamEyeRotation(EntityUid heldEntity)
    {
        var rotation = (float) _eyeManager.CurrentEye.Rotation.Theta;
        if (_lastSentEyeRotation == rotation)
            return;

        _lastSentEyeRotation = rotation;
        RaiseNetworkEvent(new RPDEyeRotationEvent(GetNetEntity(heldEntity), rotation));
    }
}
