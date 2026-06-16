using Content.Server._DV.Speech.EntitySystems;

namespace Content.Server._DV.Speech.Components;

[RegisterComponent]
[Access(typeof(ScottishAccentSystem))]
public sealed partial class ScottishAccentComponent : Component
{
    // Triad: Scots tic pools, data-driven. Prefixes are interjections (not greetings); suffixes are
    // address/affirmation only (no insults). The 18+ "Shite," prefix is a minority of the pool.
    [DataField]
    public List<string> Prefixes { get; set; } = new()
    {
        "accent-scottish-prefix-1", "accent-scottish-prefix-2", "accent-scottish-prefix-3",
        "accent-scottish-prefix-4", "accent-scottish-prefix-5", "accent-scottish-prefix-6",
    };

    [DataField]
    public float PrefixProb { get; set; } = 0.12f;

    [DataField]
    public List<string> Suffixes { get; set; } = new()
    {
        "accent-scottish-suffix-1", "accent-scottish-suffix-2", "accent-scottish-suffix-3",
        "accent-scottish-suffix-4", "accent-scottish-suffix-5", "accent-scottish-suffix-6",
    };

    [DataField]
    public float SuffixProb { get; set; } = 0.15f;
}
