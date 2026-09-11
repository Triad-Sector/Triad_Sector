/*
 * Triad - This file is licensed under AGPLv3
 * Copyright (c) 2025 Triad Contributors
 * See AGPLv3.txt for details.
 */

using Content.Server._Triad.Speech.EntitySystems;

namespace Content.Server._Triad.Speech.Components;

/// <summary>
///     New Brooklyn: the everyman borough accent. The pizzeria owner, the garage mechanic, the guy on the
///     corner who'll tell ya he's walkin' heeyah. NYC working-class phonetics (dropped R's: or -> uh,
///     ar -> ah; -ing -> -in'; the -> da) plus regular-Joe borough slang. No mob, just the city.
/// </summary>
[RegisterComponent]
[Access(typeof(NewBrooklynAccentSystem))]
public sealed partial class NewBrooklynAccentComponent : Component
{
    // Prefix tics are interjections (no greetings); suffix tics are everyman address/affirmation (no insults).
    [DataField]
    public List<string> Prefixes { get; set; } = new()
    {
        "accent-newbrooklyn-prefix-1", "accent-newbrooklyn-prefix-2",
        "accent-newbrooklyn-prefix-3", "accent-newbrooklyn-prefix-4",
    };

    [DataField]
    public float PrefixProb { get; set; } = 0.01f;

    [DataField]
    public List<string> Suffixes { get; set; } = new()
    {
        "accent-newbrooklyn-suffix-1", "accent-newbrooklyn-suffix-2",
        "accent-newbrooklyn-suffix-3", "accent-newbrooklyn-suffix-4",
        "accent-newbrooklyn-suffix-5",
    };

    [DataField]
    public float SuffixProb { get; set; } = 0.02f;
}
