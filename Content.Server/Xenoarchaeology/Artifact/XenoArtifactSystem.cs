using Content.Server.Cargo.Systems; // Triad: PriceCalculationEvent is still server-side here
using Content.Server.Kitchen.Components; // Triad: BeingMicrowavedEvent relay
using Content.Shared.Xenoarchaeology.Artifact;
using Content.Shared.Xenoarchaeology.Artifact.Components;

namespace Content.Server.Xenoarchaeology.Artifact;

/// <inheritdoc cref="SharedXenoArtifactSystem"/>
public sealed partial class XenoArtifactSystem : SharedXenoArtifactSystem
{
    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<XenoArtifactComponent, MapInitEvent>(OnArtifactMapInit);
        SubscribeLocalEvent<XenoArtifactComponent, PriceCalculationEvent>(OnCalculatePrice);

        // Triad: BeingMicrowavedEvent is a server-side class event here, so it cannot go through the
        // shared by-ref relay. Relay it by hand for XATMicrowaveSystem.
        SubscribeLocalEvent<XenoArtifactComponent, BeingMicrowavedEvent>(OnMicrowaved);
    }

    private void OnMicrowaved(EntityUid uid, XenoArtifactComponent comp, BeingMicrowavedEvent args)
    {
        RelayEventToNodes((uid, comp), ref args);
    }

    private void OnArtifactMapInit(Entity<XenoArtifactComponent> ent, ref MapInitEvent args)
    {
        if (ent.Comp.IsGenerationRequired)
            GenerateArtifactStructure(ent);
    }

    private void OnCalculatePrice(Entity<XenoArtifactComponent> ent, ref PriceCalculationEvent args)
    {
        // Triad: whole-artifact multiplier on top of the per-node sum, shared with the extract path
        var price = 0.0;
        foreach (var node in GetAllNodes(ent))
        {
            if (node.Comp.Locked)
                continue;

            price += node.Comp.ResearchValue * ent.Comp.PriceMultiplier;
        }

        args.Price += price * GetArtifactPayoutMultiplier(ent);
    }
}
