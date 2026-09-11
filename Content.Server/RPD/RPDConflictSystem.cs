using Content.Server.Atmos.EntitySystems;
using Content.Shared.Atmos.Components;
using Content.Shared.Atmos.EntitySystems;
using Content.Shared.Popups;
using Content.Shared.RCD;
using Content.Shared.RPD.Components;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.Server.RPD;

/// <summary>
/// Server-side RPD half: answers <see cref="RCDConstructionAttemptEvent"/> with the layer-aware pipe overlap rule
/// (<see cref="PipeRestrictOverlapSystem.WouldPlacementOverlap"/>), so a same-(direction, layer) placement is
/// rejected before spawn. Server-side because the overlap query reads server pipe-node data. The layer comes from
/// the event (captured at commit), not from <c>RPDComponent.CurrentLayer</c>.
/// </summary>
public sealed partial class RPDConflictSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private PipeRestrictOverlapSystem _overlap = default!;
    [Dependency] private SharedAtmosPipeLayersSystem _pipeLayers = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RPDComponent, RCDConstructionAttemptEvent>(OnConstructAttempt);
    }

    private void OnConstructAttempt(Entity<RPDComponent> ent, ref RCDConstructionAttemptEvent args)
    {
        if (args.Recipe.NoLayers || string.IsNullOrEmpty(args.Recipe.Prototype))
            return;

        if (!_proto.TryIndex<EntityPrototype>(args.Recipe.Prototype, out var baseProto)
            || !baseProto.TryComp<AtmosPipeLayersComponent>(out var pipeLayers, EntityManager.ComponentFactory))
            return;

        // Resolve the layer-specific prototype (base proto IS the Primary variant). args.Layer is the layer captured
        // at the commit click, so every re-validation during the do-after judges the same placement the click did.
        var proto = args.Recipe.Prototype!;
        if (_pipeLayers.TryGetAlternativePrototype(pipeLayers, args.Layer, out var alt))
            proto = alt.Id;

        if (_overlap.WouldPlacementOverlap((args.MapGridData.GridUid, args.MapGridData.Component), args.MapGridData.Position, proto, args.Direction.ToAngle(), args.Layer))
        {
            if (args.ShowPopups)
                _popup.PopupEntity(Loc.GetString("rcd-component-cannot-build-on-occupied-tile-message"), ent, args.User);
            args.Cancelled = true;
        }
    }
}
