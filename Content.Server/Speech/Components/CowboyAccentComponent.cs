using Content.Server.Speech.EntitySystems;

namespace Content.Server.Speech.Components;

// Triad: Cowboy promoted from a data-only ReplacementAccent word list to a dedicated component on the
// shared DrawlAccentSystem, so its rich Western vocab finally gets the drawl + prefix/suffix tics.
[RegisterComponent]
[Access(typeof(DrawlAccentSystem))]
public sealed partial class CowboyAccentComponent : Component, IDrawlAccentComponent
{
    [DataField]
    public string Accent { get; set; } = "cowboy";

    [DataField]
    public List<string> Prefixes { get; set; } = new()
    {
        "accent-cowboy-prefix-1",
        "accent-cowboy-prefix-2",
        "accent-cowboy-prefix-3",
        "accent-cowboy-prefix-4",
        "accent-cowboy-prefix-5",
        "accent-cowboy-prefix-6",
        "accent-cowboy-prefix-7",
    };

    [DataField]
    public float PrefixProb { get; set; } = 0.12f;

    [DataField]
    public List<string> Suffixes { get; set; } = new()
    {
        "accent-cowboy-suffix-1",
        "accent-cowboy-suffix-2",
        "accent-cowboy-suffix-3",
        "accent-cowboy-suffix-4",
        "accent-cowboy-suffix-5",
        "accent-cowboy-suffix-6",
    };

    [DataField]
    public float SuffixProb { get; set; } = 0.12f;
}
