using Content.Shared._DV.Traits;
using Robust.Shared.Prototypes;

namespace Content.Shared._DV.Traits.Conditions;

/// <summary>
/// Triad: condition that passes when the player has any selected trait belonging to a given category.
/// Built for the Muted -> Accents lockout: a mute character can't speak, so no accent should apply. Inverting
/// it (invert: true) reads as "must have NO trait in this category", and it auto-covers any accent added later,
/// unlike a hand-maintained list of per-accent HasTraitCondition exclusions that silently rots when someone
/// adds an accent without updating Muted.
/// </summary>
public sealed partial class HasTraitInCategoryCondition : BaseTraitCondition
{
    /// <summary>
    /// The category to test the player's selected traits against.
    /// </summary>
    [DataField(required: true)]
    public ProtoId<TraitCategoryPrototype> Category;

    /// <summary>
    /// Triad: optional plain-language tooltip that replaces the auto-generated text. Strongly recommended when
    /// inverted, since the default "must have a ... trait" wording reads backwards for an exclusion.
    /// </summary>
    [DataField]
    public LocId? Tooltip;

    protected override bool EvaluateImplementation(TraitConditionContext ctx)
    {
        if (ctx.Profile is not { } profile)
            return false;

        foreach (var traitId in profile.GetValidTraits(profile.TraitPreferences, ctx.Proto))
        {
            if (ctx.Proto.TryIndex(traitId, out var trait) && trait.Category == Category)
                return true;
        }

        return false;
    }

    public override string GetTooltip(IPrototypeManager proto, ILocalizationManager loc, int depth)
    {
        if (Tooltip is not null)
            return new string(' ', depth * 2) + "- " + loc.GetString(Tooltip) + Environment.NewLine;

        if (!proto.TryIndex(Category, out var category))
            return string.Empty;

        var tooltip = Invert
            ? loc.GetString("trait-condition-category-has-not", ("category", loc.GetString(category.Name)))
            : loc.GetString("trait-condition-category-has", ("category", loc.GetString(category.Name)));

        return new string(' ', depth * 2) + "- " + tooltip + Environment.NewLine;
    }
}
