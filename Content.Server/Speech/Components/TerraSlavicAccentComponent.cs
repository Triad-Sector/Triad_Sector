// Triad: renamed from RussianAccent to keep real-world nations vague in the post-corporate setting.
using Content.Server.Speech.EntitySystems;

namespace Content.Server.Speech.Components;

[RegisterComponent]
[Access(typeof(TerraSlavicAccentSystem))]
public sealed partial class TerraSlavicAccentComponent : Component
{
    // Tics, data-driven. Prefixes are interjections (never greetings -- greetings are word-swaps);
    // suffixes are address/affirmation only (no insults). Probs sit in the 1-3% "special flair" band.
    [DataField]
    public List<string> Prefixes { get; set; } = new()
    {
        "accent-terraslavic-prefix-1", "accent-terraslavic-prefix-2",
        "accent-terraslavic-prefix-3", "accent-terraslavic-prefix-4",
    };

    [DataField]
    public float PrefixProb { get; set; } = 0.01f;

    [DataField]
    public List<string> Suffixes { get; set; } = new()
    {
        "accent-terraslavic-suffix-1", "accent-terraslavic-suffix-2",
        "accent-terraslavic-suffix-3", "accent-terraslavic-suffix-4",
    };

    [DataField]
    public float SuffixProb { get; set; } = 0.02f;

    // Per-ARTICLE chance to drop a the/a/an. Rolled independently for each article so most lines keep
    // them and the Slavic clipping reads as an occasional slip, not a constant disjointed stutter.
    [DataField]
    public float ArticleDropProb { get; set; } = 0.05f;
}
