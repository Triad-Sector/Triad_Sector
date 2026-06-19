using Content.Shared._DV.Traits.Conditions;
using Content.Shared._DV.Traits.Effects;
using Robust.Shared.Prototypes;

namespace Content.Shared._DV.Traits;

/// <summary>
/// Prototype for a character trait in DeltaV.
/// Traits modify character behavior through condition-checked effects.
/// </summary>
[Prototype]
public sealed partial class TraitPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// Localization key for the trait's display name.
    /// </summary>
    [DataField(required: true)]
    public LocId Name;

    /// <summary>
    /// Localization key for the trait's description.
    /// </summary>
    [DataField(required: true)]
    public LocId Description;

    /// <summary>
    /// The category this trait belongs to.
    /// </summary>
    [DataField(required: true)]
    public ProtoId<TraitCategoryPrototype> Category;

    /// <summary>
    /// How many trait points this trait costs (positive) or grants (negative).
    /// </summary>
    [DataField]
    public int Cost = 1;

    /// <summary>
    /// Conditions that must be met for this trait to be selectable and applied.
    /// All conditions must pass for the trait to be valid.
    /// </summary>
    [DataField]
    public List<BaseTraitCondition> Conditions = new();

    /// <summary>
    /// Effects to apply to the entity when this trait is selected.
    /// Effects are applied in order.
    /// </summary>
    [DataField(serverOnly: true)]
    public List<BaseTraitEffect> Effects = new();

    /// <summary>
    /// The priority of this trait. Higher priority traits are applied first.
    /// Lets order-sensitive effects (e.g. add-vs-remove of the same component) resolve deterministically.
    /// </summary>
    [DataField]
    public int Priority = 0;

    /// <summary>
    /// Triad: extra trait slots this trait grants to a category's MaxTraits when selected.
    /// Lets a trait raise a category's effective cap (e.g. Foreigner granting extra language slots).
    /// Keyed by category id. Grants from all selected, otherwise-valid traits are summed.
    /// </summary>
    [DataField]
    public Dictionary<ProtoId<TraitCategoryPrototype>, int> GrantsCategorySlots = new();
}
