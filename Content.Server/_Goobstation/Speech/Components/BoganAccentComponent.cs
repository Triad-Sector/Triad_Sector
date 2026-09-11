using Content.Server.Speech.EntitySystems;

namespace Content.Server.Speech.Components;

[RegisterComponent]
[Access(typeof(BoganAccentSystem))]
public sealed partial class BoganAccentComponent : Component
{
    // Triad: tic pools + probabilities are now data-driven (were hardcoded probs + index math in the system).
    [DataField]
    public List<string> Prefixes { get; set; } = new()
    {
        "accent-bogan-prefix-1", "accent-bogan-prefix-2", "accent-bogan-prefix-3",
        "accent-bogan-prefix-4", "accent-bogan-prefix-5", "accent-bogan-prefix-6",
        "accent-bogan-prefix-7", "accent-bogan-prefix-8",
    };

    [DataField]
    public float PrefixProb { get; set; } = 0.02f;

    [DataField]
    public List<string> Suffixes { get; set; } = new()
    {
        "accent-bogan-suffix-1", "accent-bogan-suffix-2", "accent-bogan-suffix-3",
        "accent-bogan-suffix-4", "accent-bogan-suffix-5", "accent-bogan-suffix-6",
    };

    [DataField]
    public float SuffixProb { get; set; } = 0.03f;
}
