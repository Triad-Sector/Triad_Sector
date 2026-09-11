using Content.Server._NF.Speech.EntitySystems;

namespace Content.Server._NF.Speech.Components;

[RegisterComponent]
[Access(typeof(StreetpunkAccentSystem))]
public sealed partial class StreetpunkAccentComponent : Component
{
    // Triad: cyberpunk/shadowrun tic pools, data-driven. Prefixes are interjections (not greetings);
    // the 18+ options ("Shit,", ", ya drekhead") are a minority so they surface occasionally.
    [DataField]
    public List<string> Prefixes { get; set; } = new()
    {
        "accent-streetpunk-prefix-1", "accent-streetpunk-prefix-2", "accent-streetpunk-prefix-3",
        "accent-streetpunk-prefix-4", "accent-streetpunk-prefix-5", "accent-streetpunk-prefix-6",
    };

    [DataField]
    public float PrefixProb { get; set; } = 0.01f;

    [DataField]
    public List<string> Suffixes { get; set; } = new()
    {
        "accent-streetpunk-suffix-1", "accent-streetpunk-suffix-2", "accent-streetpunk-suffix-3",
        "accent-streetpunk-suffix-4", "accent-streetpunk-suffix-5", "accent-streetpunk-suffix-6",
    };

    [DataField]
    public float SuffixProb { get; set; } = 0.02f;
}
