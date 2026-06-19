/*
 * Triad - This file is licensed under AGPLv3
 * Copyright (c) 2025 Triad Contributors
 * See AGPLv3.txt for details.
 */
using Content.Server._Triad.Speech.EntitySystems;

namespace Content.Server._Triad.Speech.Components;

[RegisterComponent]
[Access(typeof(DwarfAccentSystem))]
public sealed partial class DwarfAccentComponent : Component
{
    // Triad: Dwarven Brogue tics, data-driven. Prefixes are interjections (gruff/fantasy, not greetings);
    // suffixes are address/affirmation only (no insults). The 18+ "Shite," prefix is a minority.
    [DataField]
    public List<string> Prefixes { get; set; } = new()
    {
        "accent-dwarf-prefix-1", "accent-dwarf-prefix-2", "accent-dwarf-prefix-3",
        "accent-dwarf-prefix-4", "accent-dwarf-prefix-5", "accent-dwarf-prefix-6",
    };

    [DataField]
    public float PrefixProb { get; set; } = 0.01f;

    [DataField]
    public List<string> Suffixes { get; set; } = new()
    {
        "accent-dwarf-suffix-1", "accent-dwarf-suffix-2", "accent-dwarf-suffix-3",
        "accent-dwarf-suffix-4", "accent-dwarf-suffix-5", "accent-dwarf-suffix-6",
    };

    [DataField]
    public float SuffixProb { get; set; } = 0.02f;

    /// <summary>Triad: Thick (default) runs the full brogue; Slight runs the lighter, intelligible variant.</summary>
    [DataField]
    public AccentStrength Strength { get; set; } = AccentStrength.Thick;

    /// <summary>Triad: per-pass chance the glottal stop fires when Strength is Slight. Thick ignores it.</summary>
    [DataField]
    public float SlightChance { get; set; } = 0.3f;
}
