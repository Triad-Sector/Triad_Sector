// Triad: enriched Russian accent. Identifiers kept upstream-named for clean cherry-picking;
using Content.Server._Triad.Speech; // Triad: AccentStrength relocated to _Triad
using Content.Server.Speech;
using Content.Server.Speech.EntitySystems;

namespace Content.Server.Speech.Components;

[RegisterComponent]
[Access(typeof(RussianAccentSystem))]
public sealed partial class RussianAccentComponent : Component
{
    // Tics, data-driven. Prefixes are interjections (never greetings -- greetings are word-swaps);
    // suffixes are address/affirmation only (no insults). Probs sit in the 1-3% "special flair" band.
    [DataField]
    public List<string> Prefixes { get; set; } = new()
    {
        "accent-russian-prefix-1", "accent-russian-prefix-2",
        "accent-russian-prefix-3", "accent-russian-prefix-4",
    };

    [DataField]
    public float PrefixProb { get; set; } = 0.01f;

    [DataField]
    public List<string> Suffixes { get; set; } = new()
    {
        "accent-russian-suffix-1", "accent-russian-suffix-2",
        "accent-russian-suffix-3", "accent-russian-suffix-4",
    };

    [DataField]
    public float SuffixProb { get; set; } = 0.02f;

    // Per-ARTICLE chance to drop a the/a/an. Rolled independently for each article so most lines keep
    // them and the Slavic clipping reads as an occasional slip, not a constant disjointed stutter.
    [DataField]
    public float ArticleDropProb { get; set; } = 0.05f;

    // Per-COPULA chance to drop a standalone is/are/am/was/were/be ("he is strong" -> "he strong").
    // The other classic Slavic-English cue; rolled per-word like ArticleDropProb so it stays occasional.
    [DataField]
    public float CopulaDropProb { get; set; } = 0.05f;

    /// <summary>Triad: Thick (default) runs the full accent; Slight runs the lighter, intelligible variant.</summary>
    [DataField]
    public AccentStrength Strength { get; set; } = AccentStrength.Thick;

    /// <summary>Triad: when Strength is Slight, scales the article/copula drop rate down (thick uses the full rate).</summary>
    [DataField]
    public float SlightChance { get; set; } = 0.3f;
}
