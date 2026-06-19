using Content.Server._Triad.Speech.EntitySystems; // Triad: DrawlAccentSystem + IDrawlAccentComponent relocated to _Triad
using Content.Server.Speech.EntitySystems;

namespace Content.Server.Speech.Components;

[RegisterComponent]
[Access(typeof(DrawlAccentSystem))] // Triad: was SouthernAccentSystem; folded into the shared drawl engine
public sealed partial class SouthernAccentComponent : Component, IDrawlAccentComponent // Triad: shared drawl config
{
    // Triad: drawl is now data-driven so Southern and Cowboy can share one system.
    [DataField]
    public string Accent { get; set; } = "southern";

    [DataField]
    public List<string> Prefixes { get; set; } = new()
    {
        "accent-southern-prefix-1",
        "accent-southern-prefix-2",
        "accent-southern-prefix-3",
        "accent-southern-prefix-4",
        "accent-southern-prefix-5",
        "accent-southern-prefix-6",
        "accent-southern-prefix-7",
        "accent-southern-prefix-8",
    };

    // Triad: bumped from 0.01 so the warm-genteel tics actually land. Dial to tune if it reads too often.
    [DataField]
    public float PrefixProb { get; set; } = 0.05f;

    [DataField]
    public List<string> Suffixes { get; set; } = new()
    {
        "accent-southern-suffix-1",
        "accent-southern-suffix-2",
        "accent-southern-suffix-3",
        "accent-southern-suffix-4",
        "accent-southern-suffix-5",
        "accent-southern-suffix-6",
        "accent-southern-suffix-7",
        "accent-southern-suffix-8",
    };

    [DataField]
    public float SuffixProb { get; set; } = 0.05f;
}
