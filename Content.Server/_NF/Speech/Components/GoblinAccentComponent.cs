using Content.Server._NF.Speech.EntitySystems;

namespace Content.Server._NF.Speech.Components;

[RegisterComponent]
[Access(typeof(GoblinAccentSystem))]
public sealed partial class GoblinAccentComponent : Component
{
    // Triad: cockney/cant tic pools, data-driven. Prefixes are interjections (not greetings); the 18+
    // options ("Bloody 'ell,", ", ya tosser") are a minority so they surface occasionally, not every line.
    [DataField]
    public List<string> Prefixes { get; set; } = new()
    {
        "accent-goblin-prefix-1", "accent-goblin-prefix-2", "accent-goblin-prefix-3",
        "accent-goblin-prefix-4", "accent-goblin-prefix-5", "accent-goblin-prefix-6",
        "accent-goblin-prefix-7",
    };

    [DataField]
    public float PrefixProb { get; set; } = 0.01f;

    [DataField]
    public List<string> Suffixes { get; set; } = new()
    {
        "accent-goblin-suffix-1", "accent-goblin-suffix-2", "accent-goblin-suffix-3",
        "accent-goblin-suffix-4", "accent-goblin-suffix-5", "accent-goblin-suffix-6",
    };

    [DataField]
    public float SuffixProb { get; set; } = 0.02f;
}
