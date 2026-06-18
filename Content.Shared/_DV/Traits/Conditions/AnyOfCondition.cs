using System.Text;
using Content.Shared._DV.Traits.Conditions;
using Robust.Shared.Prototypes;

namespace Content.Shared._DV.Traits.Conditions;

/// <summary>
/// Condition that passes if ANY of the child conditions pass.
/// Use this to create "must meet at least one of these requirements" checks.
/// </summary>
public sealed partial class AnyOfCondition : BaseTraitCondition
{
    /// <summary>
    /// List of conditions to check. Passes if any condition evaluates to true.
    /// </summary>
    [DataField(required: true)]
    public List<BaseTraitCondition> Conditions = new();

    protected override bool EvaluateImplementation(TraitConditionContext ctx)
    {
        // Triad: was throwing on Invert, which crashed player spawn (CheckConditions has no try/catch). Inversion
        // is well-defined here: the base Evaluate XORs the result, so an inverted AnyOf means "none of these pass"
        // (NOT(A OR B)), which the client preview already does and inverting the children cannot express. Let it run.

        // Empty list should fail
        if (Conditions.Count == 0)
            return false;

        // Return true if ANY condition passes
        foreach (var condition in Conditions)
        {
            if (condition.Evaluate(ctx))
                return true;
        }

        return false;
    }

    public override string GetTooltip(IPrototypeManager proto, ILocalizationManager loc, int depth)
    {
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

        var tooltip = loc.GetString("trait-condition-any-of", ("requirements", requirementsTooltip));

        return new string(' ', depth * 2) + "- " + tooltip;
    }
}
