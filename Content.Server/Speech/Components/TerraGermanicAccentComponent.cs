// Triad: renamed from GermanAccent to keep real-world nations vague in the post-corporate setting.
using Content.Server.Speech.EntitySystems;

namespace Content.Server.Speech.Components;

[RegisterComponent]
[Access(typeof(TerraGermanicAccentSystem))]
public sealed partial class TerraGermanicAccentComponent : Component
{
    // Tics, data-driven. Prefixes are interjections (never greetings -- greetings are word-swaps);
    // suffixes are address/affirmation only (no insults). Probs sit in the 1-3% "special flair" band.
    [DataField]
    public List<string> Prefixes { get; set; } = new()
    {
        "accent-terragermanic-prefix-1", "accent-terragermanic-prefix-2",
        "accent-terragermanic-prefix-3", "accent-terragermanic-prefix-4",
    };

    [DataField]
    public float PrefixProb { get; set; } = 0.01f;

    [DataField]
    public List<string> Suffixes { get; set; } = new()
    {
        "accent-terragermanic-suffix-1", "accent-terragermanic-suffix-2",
        "accent-terragermanic-suffix-3", "accent-terragermanic-suffix-4",
    };

    [DataField]
    public float SuffixProb { get; set; } = 0.02f;

    // The "the" -> "das" chance and the per-vowel umlaut chance, previously hardcoded (0.3 / 0.1).
    [DataField]
    public float DasProb { get; set; } = 0.3f;

    [DataField]
    public float UmlautProb { get; set; } = 0.1f;
}
