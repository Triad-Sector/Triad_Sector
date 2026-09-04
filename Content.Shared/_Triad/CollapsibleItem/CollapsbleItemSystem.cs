using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.DoAfter;
using Content.Shared.Tools.Systems;
using Content.Shared.Tools.Components;
using Content.Shared.Coordinates;
using Robust.Shared.Network;

namespace Content.Shared._Triad.CollapsibleItem;

public sealed partial class CollapsibleItemSystem : EntitySystem
{
    [Dependency] private SharedToolSystem _tool = default!;
    [Dependency] private IPrototypeManager _prototype = default!;

    [SubscribeLocalEvent]
    private void OnInteractUsing(Entity<CollapsibleItemComponent> ent, ref InteractUsingEvent args)
    {
        var tool = args.Used;

        if (_tool.GetWelderFuelAndCapacity(tool).fuel < ent.Comp.FuelCost && ent.Comp.FuelCost > 0)
            return;

        if (!TryComp<ToolComponent>(tool, out var toolComp)
                || !_tool.HasQuality(tool, ent.Comp.ToolQuality, toolComp))
            return;

        var ev = new CollapsibleItemToolUseEvent();
        args.Handled = _tool.UseTool
            (tool,
            args.User,
            ent.Owner,
            (float)ent.Comp.DoAfter.TotalSeconds,
            ent.Comp.ToolQuality,
            ev,
            fuel: ent.Comp.FuelCost);
    }

    [SubscribeLocalEvent]
    private void OnToolUse(Entity<CollapsibleItemComponent> ent, ref CollapsibleItemToolUseEvent args)
    {
        if (args.Handled)
            return;

        if (args.Cancelled)
            return;

        PredictedSpawnAtPosition(ent.Comp.CollapseInto, ent.Owner.ToCoordinates());
        args.Handled = true;

        PredictedQueueDel(ent.Owner);
    }

    [SubscribeLocalEvent]
    private void OnExamined(Entity<CollapsibleItemComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        var toolQuality = _prototype.Index(ent.Comp.ToolQuality);
        var qualityName = Loc.GetString(toolQuality.Name);

        var message = Loc.GetString("collapsible-item-component-hint", ("tool", qualityName));
        args.PushMarkup(message);
    }

    [Serializable, NetSerializable]
    public sealed partial class CollapsibleItemToolUseEvent : SimpleDoAfterEvent;
}
