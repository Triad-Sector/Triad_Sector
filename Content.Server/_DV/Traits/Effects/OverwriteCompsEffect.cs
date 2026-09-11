using Content.Shared._DV.Traits.Effects;
using Robust.Shared.Prototypes;

namespace Content.Server._DV.Traits.Effects;

/// <summary>
/// Triad: like <see cref="AddCompsEffect"/>, but REPLACES any existing component of the same type
/// (removeExisting: true) instead of skipping it. Use this when the trait intentionally re-specifies a
/// component the base mob already carries (e.g. SlowOnDamage, which BaseMobSpecies provides). With the
/// plain AddCompsEffect the engine keeps the existing component and silently drops the trait's, so the
/// trait does nothing. Do NOT use it for a component that holds runtime state you'd lose on replace.
/// </summary>
public sealed partial class OverwriteCompsEffect : BaseTraitEffect
{
    /// <summary>
    /// The components to add to the entity, overwriting any existing component of the same type.
    /// </summary>
    [DataField(required: true)]
    public ComponentRegistry Components = new();

    public override void Apply(TraitEffectContext ctx)
    {
        ctx.EntMan.AddComponents(ctx.Player, Components, removeExisting: true);
    }
}
