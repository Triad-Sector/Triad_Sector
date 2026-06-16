// Triad: renamed from SpanishAccent to keep real-world nations vague in the post-corporate setting.
using Content.Server.Speech.EntitySystems;

namespace Content.Server.Speech.Components;

[RegisterComponent]
[Access(typeof(TerraLatinoAccentSystem))]
public sealed partial class TerraLatinoAccentComponent : Component
{
    // Tics, data-driven. Prefixes are interjections (never greetings -- greetings are word-swaps);
    // suffixes are address/affirmation only (no insults). Probs sit in the 1-3% "special flair" band.
    [DataField]
    public List<string> Prefixes { get; set; } = new()
    {
        "accent-terralatino-prefix-1", "accent-terralatino-prefix-2",
        "accent-terralatino-prefix-3", "accent-terralatino-prefix-4",
    };

    [DataField]
    public float PrefixProb { get; set; } = 0.01f;

    [DataField]
    public List<string> Suffixes { get; set; } = new()
    {
        "accent-terralatino-suffix-1", "accent-terralatino-suffix-2",
        "accent-terralatino-suffix-3", "accent-terralatino-suffix-4",
    };

    [DataField]
    public float SuffixProb { get; set; } = 0.02f;
}
