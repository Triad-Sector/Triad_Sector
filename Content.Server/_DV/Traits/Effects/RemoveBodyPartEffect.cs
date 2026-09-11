using System.Linq;
using Content.Server.Body.Systems;
using Content.Shared._DV.Traits.Effects;
using Content.Shared.Body.Components;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;

namespace Content.Server._DV.Traits.Effects;

/// <summary>
/// Effect that removes body parts of the player body.
/// </summary>
public sealed partial class RemoveBodyPartEffect : BaseTraitEffect
{

    [DataField(required: true)]
    public BodyPartType Part = default;

    [DataField(required: true)]
    public BodyPartSymmetry Symmetry = default!;

    public override void Apply(TraitEffectContext ctx)
    {
        if (!ctx.EntMan.TryGetComponent(ctx.Player, out BodyComponent? body))
            return;

        if (!ctx.EntMan.TrySystem<SharedBodySystem>(out var bodySys))
            return;

        if (!ctx.EntMan.TrySystem<BloodstreamSystem>(out var bloodstreamSys))
            return;

        if (bodySys.GetRootPartOrNull(ctx.Player, body) is not { } root)
            return;

        // Triad: materialize before deleting. GetBodyChildrenOfType and GetBodyPartChildren are lazy iterators over
        // the live body graph, so deleting parts mid-enumeration could invalidate them. GetBodyPartChildren yields
        // the part itself first, so deleting its results removes the whole subtree (the old explicit part delete was
        // a double-delete of part.Id).
        var targetParts = bodySys.GetBodyChildrenOfType(ctx.Player, Part, body, Symmetry).ToList();
        if (targetParts.Count == 0)
            return;

        foreach (var part in targetParts)
        {
            foreach (var child in bodySys.GetBodyPartChildren(part.Id, part.Component).ToList())
                ctx.EntMan.DeleteEntity(child.Id);
        }

        // Removing a limb can leave the bloodstream bleeding; clamp it back to zero so a trait-chosen amputee does
        // not spawn already bleeding out (the negative amount is clamped to 0).
        bloodstreamSys.TryModifyBleedAmount(ctx.Player, -100f);
    }
}
