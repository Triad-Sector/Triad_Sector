using Content.Shared._DV.Traits.Conditions;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._DV.Traits.Conditions;

/// <summary>
/// Condition that checks if the player has a specific component.
/// Use Invert = true to check if the player does NOT have the component.
/// </summary>
public sealed partial class HasCompCondition : BaseTraitCondition
{
    /// <summary>
    /// The component name to check for (e.g., "Pacifism").
    /// </summary>
    [DataField(required: true, customTypeSerializer: typeof(ComponentNameSerializer))]
    public string Component = string.Empty;

    /// <summary>
    /// The tooltip text to display, if any.
    /// </summary>
    [DataField]
    public LocId? Tooltip;

    protected override bool EvaluateImplementation(TraitConditionContext ctx)
    {
        if (string.IsNullOrEmpty(Component))
            return false;

        // Triad: TryGetRegistration over GetRegistration-in-try/catch (matches RemCompsEffect). Avoids
        // exception-as-control-flow on the spawn path and stops swallowing unrelated exceptions as a silent false.
        if (!ctx.CompFactory.TryGetRegistration(Component, out var registration))
        {
            ctx.LogMan.GetSawmill("traits").Error($"Failed to get component registration for '{Component}'");
            return false;
        }

        return ctx.EntMan.HasComponent(ctx.Player, registration.Type);
    }

    public override string GetTooltip(IPrototypeManager proto, ILocalizationManager loc, int depth)
    {
        // If there's a custom tooltip supplied, use that
        if (Tooltip is not null)
            return new string(' ', depth * 2) + "- " + loc.GetString(Tooltip) + Environment.NewLine;

        // No tooltip for this condition since we're dealing with comps
        return string.Empty;
    }
}
