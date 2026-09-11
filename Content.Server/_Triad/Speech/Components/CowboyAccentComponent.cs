/*
 * Triad - This file is licensed under AGPLv3
 * Copyright (c) 2025 Triad Contributors
 * See AGPLv3.txt for details.
 */
using Content.Server._Triad.Speech.EntitySystems;

namespace Content.Server._Triad.Speech.Components;

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
        "accent-cowboy-prefix-8",
        "accent-cowboy-prefix-9",
        "accent-cowboy-prefix-10",
    };

    // Triad: bumped from 0.01 so the cartoon-Western tics actually land. This is the dial to tune if the
    // yee-haw reads as too frequent.
    [DataField]
    public float PrefixProb { get; set; } = 0.05f;

    [DataField]
    public List<string> Suffixes { get; set; } = new()
    {
        "accent-cowboy-suffix-1",
        "accent-cowboy-suffix-2",
        "accent-cowboy-suffix-3",
        "accent-cowboy-suffix-4",
        "accent-cowboy-suffix-5",
        "accent-cowboy-suffix-6",
        "accent-cowboy-suffix-7",
        "accent-cowboy-suffix-8",
    };

    [DataField]
    public float SuffixProb { get; set; } = 0.05f;
}
