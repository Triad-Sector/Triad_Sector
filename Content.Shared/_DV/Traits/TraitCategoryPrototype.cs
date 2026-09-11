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
    /// Triad: a value of null OR &lt;= 0 is considered unlimited (only the global limit applies). Accepting both
    /// keeps the "omit the field" prototype idiom AND the cvar convention (traits.max_count uses &lt;= 0 = unlimited),
    /// so a category author who writes `maxTraits: 0` expecting "no limit" gets that, not a hard zero cap. Read
    /// through <see cref="HasTraitLimit"/>, never <c>MaxTraits.HasValue</c>.
    /// </summary>
    [DataField]
    public int? MaxTraits;

    /// <summary>
    /// Maximum trait points that can be spent in this category.
    /// Triad: null OR &lt;= 0 is unlimited, same convention as <see cref="MaxTraits"/>. Read through
    /// <see cref="HasPointLimit"/>.
    /// </summary>
    [DataField]
    public int? MaxPoints;

    /// <summary>Triad: true when this category enforces a finite trait-count cap (a positive MaxTraits).</summary>
    public bool HasTraitLimit => MaxTraits is > 0;

    /// <summary>Triad: true when this category enforces a finite point budget (a positive MaxPoints).</summary>
    public bool HasPointLimit => MaxPoints is > 0;

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
