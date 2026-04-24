using Content.Shared.Contraband;
using Content.Shared.Examine;

namespace Content.Shared._Triad.Shipyard;

public sealed class SavingContrabandSystem : EntitySystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<SavingContrabandComponent, ExaminedEvent>(OnExamined);
    }

    private void OnExamined(EntityUid uid, SavingContrabandComponent comp, ExaminedEvent args)
    {
        if (!TryComp<ContrabandComponent>(uid, out var contraband))
            return;

        args.PushMarkup(Loc.GetString(comp.ExamineText));
    }
}
