// Triad: renamed from FrenchAccent to keep real-world nations vague in the post-corporate setting.
using Content.Server.Speech.EntitySystems;

namespace Content.Server.Speech.Components;

/// <summary>
/// Terra Gallic accent replaces spoken letters. "th" becomes "'z" and "h" at the start of a word
/// becomes "'", plus French-style spacing before ! ? : and ;.
/// </summary>
[RegisterComponent]
[Access(typeof(TerraGallicAccentSystem))]
public sealed partial class TerraGallicAccentComponent : Component
{
    // Tics, data-driven. Prefixes are interjections (never greetings -- greetings are word-swaps);
    // suffixes are address/affirmation only (no insults). Probs sit in the 1-3% "special flair" band.
    [DataField]
    public List<string> Prefixes { get; set; } = new()
    {
        "accent-terragallic-prefix-1", "accent-terragallic-prefix-2",
        "accent-terragallic-prefix-3", "accent-terragallic-prefix-4",
    };

    [DataField]
    public float PrefixProb { get; set; } = 0.01f;

    [DataField]
    public List<string> Suffixes { get; set; } = new()
    {
        "accent-terragallic-suffix-1", "accent-terragallic-suffix-2",
        "accent-terragallic-suffix-3", "accent-terragallic-suffix-4",
    };

    [DataField]
    public float SuffixProb { get; set; } = 0.02f;
}
