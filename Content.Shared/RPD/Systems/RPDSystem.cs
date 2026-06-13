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
using Robust.Shared.Prototypes;

namespace Content.Shared.RPD.Systems;

/// <summary>
/// Adds RPD-specific behavior on top of the generic RCD pipeline. Subscribes to <c>RCDSystem</c>'s extensibility
/// events to (a) gate deconstruction to RPD-whitelisted atmos hardware only and (b) swap the spawn prototype to the
/// pipe-layer alternative chosen by cursor quadrant. The operator's pipe-color stain lives server-side in
/// <c>RPDPipeColorSystem</c>, which owns the canonical <c>AtmosPipeColorComponent</c>.
/// </summary>
public sealed class RPDSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _protoManager = default!;
    [Dependency] private readonly SharedAtmosPipeLayersSystem _pipeLayers = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RPDComponent, RCDDeconstructAttemptEvent>(OnDeconstructAttempt);
        SubscribeLocalEvent<RPDComponent, RCDObjectSpawnAttemptEvent>(OnObjectSpawnAttempt);
        SubscribeLocalEvent<RPDComponent, RCDDeconstructTargetResolveEvent>(OnDeconstructTargetResolve);
        SubscribeLocalEvent<RPDComponent, RPDColorChangeMessage>(OnColorChange);

        SubscribeNetworkEvent<RPDLayerSelectEvent>(OnLayerSelect);
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

        // No RCDDeconstructable at all -> leave it to RCD's own whitelist rejection.
        if (!TryComp<RCDDeconstructableComponent>(target, out var decon))
            return;

        // The RPD admits its own whitelist; RCD doesn't need to know what an RPD is.
        if (decon.RpdDeconstructable)
            args.Admitted = true;
        else
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
    /// Client streams its cursor-aimed pipe layer; stored per-RPD for spawn and deconstruct targeting. Validated
    /// to the sender's active-hand RPD so a client can't set the layer on a tool it isn't holding.
    /// </summary>
    private void OnLayerSelect(RPDLayerSelectEvent ev, EntitySessionEventArgs session)
    {
        var uid = GetEntity(ev.NetEntity);

        if (session.SenderSession.AttachedEntity is not { } player)
            return;

        if (!TryComp<HandsComponent>(player, out var hands) || uid != hands.ActiveHand?.HeldEntity)
            return;

        if (!TryComp<RPDComponent>(uid, out var rpd))
            return;

        SetLayer((uid, rpd), ev.Layer);
    }

    /// <summary>
    /// Sets the RPD's selected pipe layer. Clamps to a defined enum value so a malicious client can't store
    /// garbage; an unsupported-but-valid layer (target has fewer layers) simply no-ops the alternative-prototype
    /// lookup at spawn. Server-only ephemeral state, not networked.
    /// </summary>
    public void SetLayer(Entity<RPDComponent> ent, AtmosPipeLayer layer)
    {
        if (!Enum.IsDefined(layer))
            return;

        ent.Comp.CurrentLayer = layer;
    }

    /// <summary>
    /// Resolves the covered-pipe deconstruct target the direct click couldn't reach (the pipe sits under a floor tile,
    /// hidden and non-interactable). Picks the RPD-deconstructable entity anchored on the tile whose pipe layer matches
    /// the operator's cursor-aimed layer (<see cref="RPDComponent.CurrentLayer"/>, pushed by the client), so
    /// deconstruct mirrors construct: aim at a quadrant, pull that layer.
    /// Server-authoritative — CurrentLayer is server-only state and the do-after that does the work only starts
    /// server-side, so the client's placeholder pick (default Primary) is cosmetic and never desyncs the result.
    /// </summary>
    private void OnDeconstructTargetResolve(Entity<RPDComponent> ent, ref RCDDeconstructTargetResolveEvent args)
    {
        // A directly-clicked non-layered RPD-deconstructable device (air sensor/alarm) is taken as-is. For layered
        // pipes the operator's aimed quadrant wins, so stacked layers on one tile resolve to the pipe under the aim,
        // not whichever one the cursor grabbed.
        if (args.Target is { } t && IsRpdDeconstructable(t) && !HasComp<AtmosPipeLayersComponent>(t))
            return;

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
