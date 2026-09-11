using Content.Shared._Common.Consent;
using Content.Shared._DV.Holosign;
using Content.Shared.Administration.Logs;
using Content.Shared.Database;
using Content.Shared.Examine;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.Holosign;

public sealed partial class LabelHolosignOnProjectedSystem : EntitySystem
{
    [Dependency] private ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private SharedConsentSystem _consent = default!;

    private readonly ProtoId<ConsentTogglePrototype> _nsfwDescriptionsConsent = "NSFWDescriptions";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LabelHolosignOnProjectedComponent, ChargeHolosignPlacedEvent>(OnHolosignPlaced);
        SubscribeLocalEvent<LabelHolosignOnProjectedComponent, LabelHolosignProjectorDescriptionMessage>(OnHolosignDescriptionChanged);

        SubscribeLocalEvent<LabeledHoloSignComponent, ExaminedEvent>(OnSignExamine);
    }

    private void OnHolosignPlaced(Entity<LabelHolosignOnProjectedComponent> ent, ref ChargeHolosignPlacedEvent args)
    {
        var user = args.User;
        var description = ent.Comp.Description;
        var isExplicit = ent.Comp.Explicit;
        var holoSign = args.Sign;

        var labelComp = EnsureComp<LabeledHoloSignComponent>(holoSign);
        labelComp.Description = description;
        labelComp.IsExplicit = isExplicit;
        Dirty(holoSign, labelComp);

        _adminLogger.Add(LogType.Action, LogImpact.Low, $"{ToPrettyString(user):user} placed a {ToPrettyString(holoSign):holosign} with {isExplicit} description {description}");;
    }

    private void OnHolosignDescriptionChanged(Entity<LabelHolosignOnProjectedComponent> ent, ref LabelHolosignProjectorDescriptionMessage args)
    {
        var description = args.Description.Trim();
        ent.Comp.Description = description[..Math.Min(ent.Comp.MaxDescriptionCharacters, description.Length)];
        ent.Comp.Explicit = args.Explicit;
        Dirty(ent);
    }

    private void OnSignExamine(Entity<LabeledHoloSignComponent> ent, ref ExaminedEvent args)
    {
        if (ent.Comp.Description.Length == 0)
            return;

        if (ent.Comp.IsExplicit && !_consent.HasConsent(args.Examiner, _nsfwDescriptionsConsent))
        {
            args.PushMarkup(Loc.GetString("holoprojector-label-consent-hidden"));
            return;
        }

        args.PushMarkup(ent.Comp.Description);
    }
}
