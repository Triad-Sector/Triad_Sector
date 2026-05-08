using Content.Shared.Atmos.Components;
using Content.Shared.SprayPainter;
using Content.Shared.SprayPainter.Components;
using Content.Shared.SprayPainter.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.SprayPainter;


/// <summary>
///       This handles a system where spray paintable entities are auto-painted on map init.
///       We do this so spray paintable entities during ship save keep their paint.
/// </summary>
public sealed class SharedSprayPainterSystem : EntitySystem
{
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SprayPaintOnMapInitComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<PaintableComponent, EntityPaintedEvent>(OnPainted);
    }

    private void OnMapInit(Entity<SprayPaintOnMapInitComponent> ent, ref MapInitEvent args)
    {
        SprayPaint(ent, ent.Comp.Style);
    }

    private void OnPainted(Entity<PaintableComponent> ent, ref EntityPaintedEvent args)
    {
        // Pipes are handled seperately in AtmosPipeColorSystem
        if (HasComp<PipeAppearanceComponent>(ent))
            return;

        var paintOnMapInit = EnsureComp<SprayPaintOnMapInitComponent>(ent);
        paintOnMapInit.Style = args.Prototype.Id;
    }

    public void SprayPaint(EntityUid target, EntProtoId style)
    {
        if (!TryComp<PaintableComponent>(target, out var paintable))
            return;

        if (paintable.Group is not { } groupId)
            return;

        _appearance.SetData(target, PaintableVisuals.Prototype, style.Id);

        // I don't like how I have to copypaste this event but there's no generic event
        // in the spray painter system, so this will have to do
        var ev = new EntityPaintedEvent(
            User: null,
            Tool: target, // why is tool not nullable?
            Prototype: style.Id,
            Group: groupId);
        RaiseLocalEvent(target, ref ev);
    }
}