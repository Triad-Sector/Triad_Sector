/*
 * Triad - This file is licensed under AGPLv3
 * Copyright (c) 2025 Triad Contributors
 * See AGPLv3.txt for details.
 */

using Content.Server._Triad.Speech.EntitySystems;

namespace Content.Server._Triad.Speech.Components;

/// <summary>
///     Freeport Corsair: the spacelane buccaneer. A corsair out of some lawless free port. Pirate vocab
///     (ahoy / aye / booty / cap'n / doubloons) plus salty phonetics (-ing -> -in', my -> me) and the
///     classic "Arrr" interjections. Built Triad-original, inspired by the upstream Pirate accent.
/// </summary>
[RegisterComponent]
[Access(typeof(FreeportCorsairAccentSystem))]
public sealed partial class FreeportCorsairAccentComponent : Component
{
    // Prefix tics are the "Arrr" interjections (no greetings). Suffix tics are salty address/affirmation
    // (no insults aimed at the listener). Probs sit in the 1-3% "special flair" band.
    [DataField]
    public List<string> Prefixes { get; set; } = new()
    {
        "accent-freeportcorsair-prefix-1", "accent-freeportcorsair-prefix-2",
        "accent-freeportcorsair-prefix-3", "accent-freeportcorsair-prefix-4",
    };

    [DataField]
    public float PrefixProb { get; set; } = 0.01f;

    [DataField]
    public List<string> Suffixes { get; set; } = new()
    {
        "accent-freeportcorsair-suffix-1", "accent-freeportcorsair-suffix-2",
        "accent-freeportcorsair-suffix-3", "accent-freeportcorsair-suffix-4",
        "accent-freeportcorsair-suffix-5",
    };

    [DataField]
    public float SuffixProb { get; set; } = 0.02f;
}
