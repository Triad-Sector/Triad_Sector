using Content.Shared.Atmos.Components;
using Content.Shared.Atmos.EntitySystems;
using Content.Shared.Atmos.Piping;
using Content.Shared.Hands.Components;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.RCD;
using Content.Shared.RCD.Components;
using Content.Shared.RCD.Systems;
using Content.Shared.RPD.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.Shared.RPD.Systems;

/// <summary>
/// Adds RPD-specific behavior on top of the generic RCD pipeline. Subscribes to <c>RCDSystem</c>'s extensibility
/// events to (a) gate deconstruction to RPD-whitelisted atmos hardware only, (b) swap the spawn prototype to the
/// pipe-layer alternative chosen by cursor quadrant, and (c) stain spawned pipes/atmos hardware with the
/// operator's selected color. The stain is an unconditional <c>PipeColorVisuals.Color</c> appearance write —
/// entities without a <c>PipeColorVisuals</c> visualizer (air alarms, air sensors) absorb it harmlessly.
/// </summary>
public sealed class RPDSystem : EntitySystem
{
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly IPrototypeManager _protoManager = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedAtmosPipeLayersSystem _pipeLayers = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        // Must run before RCDSystem so CurrentLayer is committed to the component before RCDSystem captures
        // the click into a DoAfter and (a few ticks later) raises RCDObjectSpawnAttemptEvent. Without this
        // ordering RCDSystem sets args.Handled first and we bail at the Handled gate, leaving CurrentLayer
        // at its default (Primary) regardless of cursor position.
        SubscribeLocalEvent<RPDComponent, AfterInteractEvent>(OnAfterInteract, before: new[] { typeof(RCDSystem) });
        SubscribeLocalEvent<RPDComponent, RCDDeconstructAttemptEvent>(OnDeconstructAttempt);
        SubscribeLocalEvent<RPDComponent, RCDObjectSpawnAttemptEvent>(OnObjectSpawnAttempt);
        SubscribeLocalEvent<RPDComponent, RCDObjectSpawnedEvent>(OnObjectSpawned);
        SubscribeLocalEvent<RPDComponent, RCDDeconstructTargetResolveEvent>(OnDeconstructTargetResolve);
        SubscribeLocalEvent<RPDComponent, RPDColorChangeMessage>(OnColorChange);

        SubscribeNetworkEvent<RPDEyeRotationEvent>(OnEyeRotation);
    }

    /// <summary>
    /// Computes the target <see cref="AtmosPipeLayer"/> from the cursor's position inside the clicked tile. The
    /// chosen layer is stored on the RPDComponent so the spawn event (which fires after the do-after delay) reads
    /// the layer that was chosen at click time, not whatever the cursor is hovering by then.
    /// </summary>
    private void OnAfterInteract(Entity<RPDComponent> ent, ref AfterInteractEvent args)
    {
        // Layer is consumed at server-side spawn time. Client prediction doesn't need to mutate the component.
        if (!_net.IsServer)
            return;

        if (args.Handled || !args.CanReach)
            return;

        if (!TryComp<RCDComponent>(ent, out var rcd))
            return;

        if (!_protoManager.TryIndex(rcd.ProtoId, out var recipe) || recipe.NoLayers)
        {
            ent.Comp.CurrentLayer = AtmosPipeLayer.Primary;
            return;
        }

        var location = args.ClickLocation;
        if (!location.IsValid(EntityManager))
            return;

        var gridUid = _transform.GetGrid(location);
        if (!TryComp<MapGridComponent>(gridUid, out var grid))
            return;

        var tileRef = _mapSystem.GetTileRef(gridUid.Value, grid, location);
        var tileSize = grid.TileSize;
        // Both terms are in the grid's local frame (tile units); cursor minus tile center yields an offset
        // in [-tileSize/2, tileSize/2] which is what RPDLayerMath.PickLayer expects. Mirror the client-side
        // computation in AlignRPDAtmosPipeLayers.AlignPlacementMode so the ghost and the commit agree.
        var tileCenter = new System.Numerics.Vector2(tileRef.X + tileSize / 2f, tileRef.Y + tileSize / 2f);
        var mouseDiff = location.Position - tileCenter;

        var eye = ent.Comp.LastKnownEyeRotation is { } theta ? new Angle(theta) : Angle.Zero;
        var gridRotation = _transform.GetWorldRotation(gridUid.Value);
        ent.Comp.CurrentLayer = ent.Comp.LastKnownEyeRotation.HasValue
            ? RPDLayerMath.PickLayer(mouseDiff, eye, gridRotation)
            : AtmosPipeLayer.Primary;
    }

    /// <summary>
    /// RPDs never deconstruct floor tiles. For structures, the target must opt in via
    /// <see cref="RCDDeconstructableComponent.RpdDeconstructable"/>.
    /// </summary>
    private void OnDeconstructAttempt(Entity<RPDComponent> ent, ref RCDDeconstructAttemptEvent args)
    {
        if (args.Target is not { } target)
        {
            if (args.ShowPopups)
                _popup.PopupClient(Loc.GetString("rpd-component-deconstruct-target-invalid"), ent, args.User);
            args.Cancelled = true;
            return;
        }

        // RCDSystem already handles the "target lacks RCDDeconstructable" case; we only add the RPD-specific opt-in gate.
        if (TryComp<RCDDeconstructableComponent>(target, out var decon) && !decon.RpdDeconstructable)
        {
            if (args.ShowPopups)
                _popup.PopupClient(Loc.GetString("rpd-component-deconstruct-target-invalid"), ent, args.User);
            args.Cancelled = true;
        }
    }

    /// <summary>
    /// Rewrites the spawn prototype to the AtmosPipeLayer alternative when the recipe is layer-capable and the
    /// target entity defines pipe-layer variants. Falls through to the original prototype otherwise.
    /// </summary>
    private void OnObjectSpawnAttempt(Entity<RPDComponent> ent, ref RCDObjectSpawnAttemptEvent args)
    {
        if (args.Recipe.NoLayers || string.IsNullOrEmpty(args.SpawnProto))
            return;

        if (!_protoManager.TryIndex<EntityPrototype>(args.SpawnProto, out var entityProto))
            return;

        if (!entityProto.TryGetComponent<AtmosPipeLayersComponent>(out var atmosPipeLayers, EntityManager.ComponentFactory))
            return;

        if (_pipeLayers.TryGetAlternativePrototype(atmosPipeLayers, ent.Comp.CurrentLayer, out var layerProto))
            args.SpawnProto = layerProto.Id;
    }

    /// <summary>
    /// Applies the operator's selected pipe-color stain to the freshly spawned entity. The default key skips the
    /// write entirely; otherwise the appearance data is set unconditionally and the <c>PipeColorVisuals</c>
    /// visualizer (on every pipe/pump/vent/valve/mixer/heat-exchanger prototype) picks it up. Entities without
    /// the visualizer (air alarms, air sensors) absorb the appearance bytes harmlessly — cheaper than a per-spawn
    /// component check.
    /// </summary>
    private void OnObjectSpawned(Entity<RPDComponent> ent, ref RCDObjectSpawnedEvent args)
    {
        if (ent.Comp.PipeColor == RPDPalette.DefaultKey)
            return;

        if (RPDPalette.Colors.TryGetValue(ent.Comp.PipeColor, out var pipeColor) && pipeColor is { } color)
            _appearance.SetData(args.Spawned, PipeColorVisuals.Color, color);
    }

    /// <summary>
    /// Client requests a palette change via the RPD BUI. Validated against <see cref="RPDPalette"/> so a
    /// misbehaving client can't store off-palette keys.
    /// </summary>
    private void OnColorChange(Entity<RPDComponent> ent, ref RPDColorChangeMessage args)
    {
        if (!RPDPalette.IsValid(args.PipeColor))
            return;

        ent.Comp.PipeColor = args.PipeColor;
        Dirty(ent);
    }

    /// <summary>
    /// Client streams local eye rotation; stored per-RPD so the server-side layer math can reproduce the
    /// client's cursor-quadrant pick when the placement commits.
    /// </summary>
    private void OnEyeRotation(RPDEyeRotationEvent ev, EntitySessionEventArgs session)
    {
        var uid = GetEntity(ev.NetEntity);

        if (session.SenderSession.AttachedEntity is not { } player)
            return;

        if (!TryComp<HandsComponent>(player, out var hands) || uid != hands.ActiveHand?.HeldEntity)
            return;

        if (!TryComp<RPDComponent>(uid, out var rpd))
            return;

        rpd.LastKnownEyeRotation = ev.EyeRotation;
    }

    /// <summary>
    /// Resolves the covered-pipe deconstruct target the direct click couldn't reach (the pipe sits under a floor tile,
    /// hidden and non-interactable). Picks the RPD-deconstructable entity anchored on the tile whose pipe layer matches
    /// the operator's cursor-aimed layer (<see cref="RPDComponent.CurrentLayer"/>, set in <see cref="OnAfterInteract"/>
    /// from the streamed eye rotation), so deconstruct mirrors construct: aim at a quadrant, pull that layer.
    /// Server-authoritative — CurrentLayer is server-only state and the do-after that does the work only starts
    /// server-side, so the client's placeholder pick (default Primary) is cosmetic and never desyncs the result.
    /// </summary>
    private void OnDeconstructTargetResolve(Entity<RPDComponent> ent, ref RCDDeconstructTargetResolveEvent args)
    {
        args.Target = FindSubfloorRpdDeconstructable(args.MapGridData, ent.Comp.CurrentLayer) ?? args.Target;
    }

    /// <summary>
    /// Searches a tile's anchored entities for the RPD-deconstructable one to chew. Prefers the entity on the
    /// operator's aimed pipe layer; falls back to the lowest-NetEntity candidate when nothing sits on that layer
    /// (single-layer pipe, or a non-layered atmos device such as an air sensor/alarm). Lowest-NetEntity keeps any tie
    /// deterministic.
    /// </summary>
    private EntityUid? FindSubfloorRpdDeconstructable(MapGridData mapGridData, AtmosPipeLayer aimedLayer)
    {
        EntityUid? layerMatch = null;
        var layerMatchId = int.MaxValue;
        EntityUid? fallback = null;
        var fallbackId = int.MaxValue;

        foreach (var anchored in _mapSystem.GetAnchoredEntities(mapGridData.GridUid, mapGridData.Component, mapGridData.Position))
        {
            if (!IsRpdDeconstructable(anchored))
                continue;

            var netId = GetNetEntity(anchored).Id;

            if (netId < fallbackId)
            {
                fallbackId = netId;
                fallback = anchored;
            }

            if (TryComp<AtmosPipeLayersComponent>(anchored, out var layers)
                && layers.CurrentPipeLayer == aimedLayer
                && netId < layerMatchId)
            {
                layerMatchId = netId;
                layerMatch = anchored;
            }
        }

        return layerMatch ?? fallback;
    }

    // Mirror of RCDSystem.IsRpdDeconstructable: the entity opts into RPD deconstruction via RCDDeconstructableComponent.
    private bool IsRpdDeconstructable(EntityUid target)
        => TryComp<RCDDeconstructableComponent>(target, out var decon) && decon.RpdDeconstructable;
}
