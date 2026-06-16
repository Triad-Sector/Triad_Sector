using Content.Server._DV.Speech.EntitySystems;

namespace Content.Server._DV.Speech.Components;

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
}
