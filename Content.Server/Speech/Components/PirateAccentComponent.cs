using Content.Server.Speech.EntitySystems;

namespace Content.Server.Speech.Components;

// Triad: enriched the Pirate accent (the buccaneer / "Freeport Corsair" display name). Replaced the
// prefix-only YarrChance/PirateWords shape with the shared Prefixes/Suffixes tic pools + probs so it
// gains suffix tics and the salty g-drop. Kept the upstream PirateAccent name for clean cherry-picking.
[RegisterComponent]
[Access(typeof(PirateAccentSystem))]
public sealed partial class PirateAccentComponent : Component
{
    // Prefix tics are the "Arrr" interjections (no greetings). Suffix tics are salty address/affirmation
    // (no insults aimed at the listener). Probs sit in the 1-3% "special flair" band.
    [DataField]
    public List<string> Prefixes { get; set; } = new()
    {
        "accent-pirate-prefix-1", "accent-pirate-prefix-2",
        "accent-pirate-prefix-3", "accent-pirate-prefix-4",
    };

    [DataField]
    public float PrefixProb { get; set; } = 0.01f;

    [DataField]
    public List<string> Suffixes { get; set; } = new()
    {
        "accent-pirate-suffix-1", "accent-pirate-suffix-2",
        "accent-pirate-suffix-3", "accent-pirate-suffix-4",
        "accent-pirate-suffix-5",
    };

    [DataField]
    public float SuffixProb { get; set; } = 0.02f;
}
