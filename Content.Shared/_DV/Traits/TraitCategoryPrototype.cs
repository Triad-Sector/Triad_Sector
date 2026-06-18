using Robust.Shared.Prototypes;

namespace Content.Shared._DV.Traits;

/// <summary>
/// Prototype for a category of traits.
/// Categories organize traits and can impose their own limits.
/// </summary>
[Prototype]
public sealed partial class TraitCategoryPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// Localization key for the category's display name.
    /// </summary>
    [DataField(required: true)]
    public LocId Name;

    /// <summary>
    /// Display order priority. Lower values appear first.
    /// </summary>
    [DataField]
    public int Priority = 1;

    /// <summary>
    /// Maximum number of traits that can be selected from this category.
    /// Null means unlimited (only global limit applies).
    /// </summary>
    [DataField]
    public int? MaxTraits;

    /// <summary>
    /// Maximum trait points that can be spent in this category.
    /// Null means unlimited (only global limit applies).
    /// </summary>
    [DataField]
    public int? MaxPoints;

    /// <summary>
    /// Color hex for the category header accent.
    /// </summary>
    [DataField]
    public Color AccentColor = Color.FromHex("#4a9eff");

    /// <summary>
    /// Whether this category starts expanded or collapsed.
    /// </summary>
    // Triad: default collapsed so the per-category headers (and their point caps) sit adjacent and read at a
    // glance; a big expanded list pushes the next category's cap off-screen and reads like one global budget.
    // Per-category opt-in to expanded is still available via `defaultExpanded: true` in YAML.
    [DataField]
    public bool DefaultExpanded = false;
}
