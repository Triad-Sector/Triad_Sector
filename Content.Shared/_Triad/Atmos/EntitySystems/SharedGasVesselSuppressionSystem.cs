using Content.Shared.Examine;
using Content.Shared._Triad.Atmos.Components;

namespace Content.Shared._Triad.Atmos.EntitySystems;

public abstract partial class SharedGasVesselSuppressionSystem : EntitySystem
{
    [SubscribeLocalEvent]
    private void OnExamined(Entity<SafeGasCanComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        var label = ent.Comp.Enabled ? ent.Comp.EnabledLabel : ent.Comp.DisabledLabel;
        args.PushMarkup(Loc.GetString(label), ent.Comp.ExaminePriority);
    }
}
