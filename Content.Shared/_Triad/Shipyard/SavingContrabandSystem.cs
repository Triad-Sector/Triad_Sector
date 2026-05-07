using Content.Shared.Examine;
using Content.Shared.Verbs;
using Robust.Shared.Utility;

namespace Content.Shared._Triad.Shipyard;

public sealed class SavingContrabandSystem : EntitySystem
{
    [Dependency] private readonly ExamineSystemShared _examine = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<SavingContrabandComponent, GetVerbsEvent<ExamineVerb>>(OnDetailedExamine);
    }

    private void OnDetailedExamine(Entity<SavingContrabandComponent> ent, ref GetVerbsEvent<ExamineVerb> args)
    {
        if (ent.Comp.ExamineText == null)
            return;

        if (!args.CanInteract)
            return;

        var examineText = Loc.GetString(ent.Comp.ExamineText);

        var msg = new FormattedMessage();
        msg.AddMarkupOrThrow(examineText);

        _examine.AddDetailedExamineVerb(args,
            ent.Comp,
            msg,
            Loc.GetString("ship-saving-contraband-examine-verb-text"),
            "/Textures/Interface/VerbIcons/anchor.svg.192dpi.png",
            Loc.GetString("ship-saving-contraband-examine-verb-message"));
    }
}
