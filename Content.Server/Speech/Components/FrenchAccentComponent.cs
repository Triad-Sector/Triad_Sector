// Triad: enriched French accent. Identifiers kept upstream-named for clean cherry-picking;
using Content.Server._Triad.Speech; // Triad: AccentStrength relocated to _Triad
using Content.Server.Speech;
using Content.Server.Speech.EntitySystems;

namespace Content.Server.Speech.Components;

/// <summary>
/// Terra Gallic accent. Thick (default): th->z, word-initial h dropped, j->zh, plus French-style
/// spacing before ! ? : and ;. Slight: only a gated th->z (by SlightChance) over an iconic-only word
/// list; h-drop and j->zh are omitted.
/// </summary>
[RegisterComponent]
[Access(typeof(FrenchAccentSystem))]
public sealed partial class FrenchAccentComponent : Component
{
    // Tics, data-driven. Prefixes are interjections (never greetings -- greetings are word-swaps);
    // suffixes are address/affirmation only (no insults). Probs sit in the 1-3% "special flair" band.
    [DataField]
    public List<string> Prefixes { get; set; } = new()
    {
        "accent-french-prefix-1", "accent-french-prefix-2",
        "accent-french-prefix-3", "accent-french-prefix-4",
    };

    [DataField]
    public float PrefixProb { get; set; } = 0.01f;

    [DataField]
    public List<string> Suffixes { get; set; } = new()
    {
        "accent-french-suffix-1", "accent-french-suffix-2",
        "accent-french-suffix-3", "accent-french-suffix-4",
    };

    [DataField]
    public float SuffixProb { get; set; } = 0.02f;

    /// <summary>Triad: Thick (default) runs the full accent; Slight runs the lighter, intelligible variant.</summary>
    [DataField]
    public AccentStrength Strength { get; set; } = AccentStrength.Thick;

    /// <summary>Triad: per-match chance th->z fires when Strength is Slight. Thick ignores it.</summary>
    [DataField]
    public float SlightChance { get; set; } = 0.3f;
}
