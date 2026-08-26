using Content.Shared.Examine;
using Content.Shared.Verbs;
using Robust.Shared.Utility;

namespace Content.Shared._Triad.ContrabandPermit;

public abstract partial class SharedContrabandPermitSystem : EntitySystem
{
    [Dependency] private ExamineSystemShared _examine = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ContrabandPermittableComponent, GetVerbsEvent<ExamineVerb>>(OnPermittableItemDetailedExamine);

        InitializeConsole();
        InitializePermitChip();
    }

    private void OnPermittableItemDetailedExamine(Entity<ContrabandPermittableComponent> ent, ref GetVerbsEvent<ExamineVerb> args)
    {
        if (!args.CanInteract)
            return;

        if (!ent.Comp.Permittable)
            return;

        var defaultText = Loc.GetString(ent.Comp.ExamineText);

        var msg = new FormattedMessage();

        if (!TryComp<ContrabandPermitItemComponent>(ent.Owner, out var permit))
        {
            msg.AddMarkupOrThrow(defaultText);
        }
        else if (permit.PermitOwner != null)
        {
            var permitMsg = Loc.GetString("contraband-permittable-examine-permit-format", ("name", permit.PermitOwnerName), ("date", permit.DateGranted));
            msg.AddMarkupOrThrow(permitMsg);
        }

        _examine.AddDetailedExamineVerb(args,
            ent.Comp,
            msg,
            Loc.GetString("contraband-permittable-examine-verb-text"),
            "/Textures/_Triad/Interface/VerbIcons/savecontraband.svg.192dpi.png",
            Loc.GetString("contraband-permittable-examine-verb-message"));
    }
}
