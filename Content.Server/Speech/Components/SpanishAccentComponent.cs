// Triad: enriched Spanish accent. Identifiers kept upstream-named for clean cherry-picking;
using Content.Server._Triad.Speech; // Triad: AccentStrength relocated to _Triad
using Content.Server.Speech;
using Content.Server.Speech.EntitySystems;

namespace Content.Server.Speech.Components;

[RegisterComponent]
[Access(typeof(SpanishAccentSystem))]
public sealed partial class SpanishAccentComponent : Component
{
    // Tics, data-driven. Prefixes are interjections (never greetings -- greetings are word-swaps);
    // suffixes are address/affirmation only (no insults). Probs sit in the 1-3% "special flair" band.
    [DataField]
    public List<string> Prefixes { get; set; } = new()
    {
        "accent-spanish-prefix-1", "accent-spanish-prefix-2",
        "accent-spanish-prefix-3", "accent-spanish-prefix-4",
    };

    [DataField]
    public float PrefixProb { get; set; } = 0.01f;

    [DataField]
    public List<string> Suffixes { get; set; } = new()
    {
        "accent-spanish-suffix-1", "accent-spanish-suffix-2",
        "accent-spanish-suffix-3", "accent-spanish-suffix-4",
    };

    [DataField]
    public float SuffixProb { get; set; } = 0.02f;

    /// <summary>Triad: Thick (default) runs the full accent; Slight runs the lighter, intelligible variant.</summary>
    [DataField]
    public AccentStrength Strength { get; set; } = AccentStrength.Thick;

    /// <summary>Triad: chance the kept phonetic passes fire when Strength is Slight. Thick ignores it.</summary>
    [DataField]
    public float SlightChance { get; set; } = 0.3f;
}
