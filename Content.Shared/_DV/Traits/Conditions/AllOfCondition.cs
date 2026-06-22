using System.Text;
using Content.Shared._DV.Traits.Conditions;
using Robust.Shared.Prototypes;

namespace Content.Shared._DV.Traits.Conditions;

/// <summary>
/// Triad: condition that passes only if ALL of the child conditions pass (logical AND).
/// The trait system already had AnyOf (OR); this is the missing conjunction. Combined with the base
/// Invert (which the parent Evaluate XORs in), an inverted AllOf reads as "not all of these are true"
/// (NOT(A AND B)) — e.g. block a trait only when the player has BOTH of two other traits, such as
/// gating BionicSpinarette out only for a double-arm amputee (no left arm AND no right arm = no hands).
/// </summary>
public sealed partial class AllOfCondition : BaseTraitCondition
{
    /// <summary>
    /// List of conditions to check. Passes only if every condition evaluates to true.
    /// </summary>
    [DataField(required: true)]
    public List<BaseTraitCondition> Conditions = new();

    /// <summary>
    /// Triad: optional plain-language tooltip that replaces the auto-generated "all of the following" text.
    /// Strongly recommended when this AllOf is inverted (NOT(A AND B)), since the auto text lists the children
    /// as if they were requirements, which reads backwards. E.g. an inverted AllOf of two amputee checks should
    /// just say "You must have at least one arm." (mirrors HasCompCondition.Tooltip).
    /// </summary>
    [DataField]
    public LocId? Tooltip;

    protected override bool EvaluateImplementation(TraitConditionContext ctx)
    {
        // Empty list should fail (matches AnyOf's empty-list behavior; an AND of nothing is meaningless here).
        if (Conditions.Count == 0)
            return false;

        // Each child's own Invert is applied inside Evaluate; this returns true only if they all pass.
        foreach (var condition in Conditions)
        {
            if (!condition.Evaluate(ctx))
                return false;
        }

        return true;
    }

    public override string GetTooltip(IPrototypeManager proto, ILocalizationManager loc, int depth)
    {
        // Triad: a custom override wins, used to give inverted AllOf gates readable wording instead of the
        // backwards "all of the following must be true" child dump.
        if (Tooltip is not null)
            return new string(' ', depth * 2) + "- " + loc.GetString(Tooltip) + Environment.NewLine;

        if (Conditions.Count == 0)
            return string.Empty;

        var requirementsTooltip = new StringBuilder();

        foreach (var condition in Conditions)
        {
            var conditionTooltip = condition.GetTooltip(proto, loc, depth + 1);
            if (conditionTooltip.Length > 0)
                requirementsTooltip.Append(conditionTooltip);
        }

        if (requirementsTooltip.Length == 0)
            return string.Empty;

        var tooltip = loc.GetString("trait-condition-all-of", ("requirements", requirementsTooltip));

        return new string(' ', depth * 2) + "- " + tooltip;
    }
}
