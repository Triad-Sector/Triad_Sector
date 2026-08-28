using Content.Server.Fluids.EntitySystems;
using Content.Server.Xenoarchaeology.Artifact.XAE.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Xenoarchaeology.Artifact;
using Content.Shared.Xenoarchaeology.Artifact.XAE;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server.Xenoarchaeology.Artifact.XAE;

/// <summary>
/// System for xeno artifact effect that creates puddle of chemical reagents under artifact.
/// </summary>
public sealed class XAECreatePuddleSystem: BaseXAESystem<XAECreatePuddleComponent>
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly PuddleSystem _puddle = default!;
    [Dependency] private readonly MetaDataSystem _metaData= default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager= default!;

    /// <inheritdoc />
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<XAECreatePuddleComponent, MapInitEvent>(OnInit);
    }

    private void OnInit(EntityUid uid, XAECreatePuddleComponent component, MapInitEvent _)
    {
        if (component.PossibleChemicals == null || component.PossibleChemicals.Count == 0)
            return;

        if (component.SelectedChemicals == null)
        {
            var chemicalList = new List<ProtoId<ReagentPrototype>>();
            var chemAmount = component.ChemAmount.Next(_random);
            for (var i = 0; i < chemAmount; i++)
            {
                var chemProto = _random.Pick(component.PossibleChemicals);
                chemicalList.Add(chemProto);
            }

            component.SelectedChemicals = chemicalList;
        }

        if (component.ReplaceDescription)
        {
            var reagentNames = new HashSet<string>();
            foreach (var chemProtoId in component.SelectedChemicals)
            {
                var reagent = _prototypeManager.Index(chemProtoId);
                reagentNames.Add(reagent.LocalizedName);
            }

            var reagentNamesStr = string.Join(", ", reagentNames);
            var newEntityDescription = Loc.GetString("xenoarch-effect-puddle", ("reagent", reagentNamesStr));
            _metaData.SetEntityDescription(uid, newEntityDescription);
        }
    }

    /// <inheritdoc />
    protected override void OnActivated(Entity<XAECreatePuddleComponent> ent, ref XenoArtifactNodeActivatedEvent args)
    {
        var component = ent.Comp;
        if (component.SelectedChemicals == null)
            return;

        // Triad: TrySpillAt does not drain the solution you hand it, and AddReagent does not clamp at
        // MaxVolume, so filling the component's own solution on every activation left the previous
        // fill sitting in it and the puddle grew by a full charge each use. Mix the spill fresh and
        // leave the authored solution as the template it reads like. Clone carries maxVol and
        // canReact across; the authored one holds no reagents.
        var spill = component.ChemicalSolution.Clone();
        var amountPerChem = spill.MaxVolume / component.SelectedChemicals.Count;
        foreach (var reagent in component.SelectedChemicals)
        {
            spill.AddReagent(reagent, amountPerChem);
        }

        // Triad: spill under the artifact, not under the node. The node lives in the artifact's
        // container so this resolved to the same tile by accident; say what we mean.
        _puddle.TrySpillAt(args.Artifact.Owner, spill, out _);
    }
}
