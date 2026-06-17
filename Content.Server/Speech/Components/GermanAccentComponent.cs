// Triad: enriched German accent. Identifiers kept upstream-named for clean cherry-picking;
using Content.Server.Speech.EntitySystems;

namespace Content.Server.Speech.Components;

[RegisterComponent]
[Access(typeof(GermanAccentSystem))]
public sealed partial class GermanAccentComponent : Component
{
    // Tics, data-driven. Prefixes are interjections (never greetings -- greetings are word-swaps);
    // suffixes are address/affirmation only (no insults). Probs sit in the 1-3% "special flair" band.
    [DataField]
    public List<string> Prefixes { get; set; } = new()
    {
        "accent-german-prefix-1", "accent-german-prefix-2",
        "accent-german-prefix-3", "accent-german-prefix-4",
    };

    [DataField]
    public float PrefixProb { get; set; } = 0.01f;

    [DataField]
    public List<string> Suffixes { get; set; } = new()
    {
        "accent-german-suffix-1", "accent-german-suffix-2",
        "accent-german-suffix-3", "accent-german-suffix-4",
    };

    [DataField]
    public float SuffixProb { get; set; } = 0.02f;

    // The "the" -> "das" chance and the per-vowel umlaut chance, previously hardcoded (0.3 / 0.1).
    [DataField]
    public float DasProb { get; set; } = 0.3f;

    [DataField]
    public float UmlautProb { get; set; } = 0.1f;
}
