using Content.Server._NF.Speech.EntitySystems;

namespace Content.Server._NF.Speech.Components;

[RegisterComponent]
[Access(typeof(CavemanAccentSystem))]
public sealed partial class CavemanAccentComponent : Component
{
    // Triad: raised 5 -> 8 so everyday words (doctor, weapon, bridge, airlock) survive and only the
    // genuinely long ones (maintenance, atmospherics, coordinates) grunt out -- 5 made it unintelligible.
    // Most common long words are caught by the word-list below before length ever matters.
    [ViewVariables(VVAccess.ReadWrite)]
    public static int MaxWordLength = 8;

    // Triad: filler/grammar words a caveman drops entirely ("me go store"). The keys were mistyped
    // (forbidden-words-N) so they never resolved, and the system never used them -- both fixed now.
    [ViewVariables]
    public static readonly List<string> ForbiddenWords = new()
    {
        "accent-caveman-forbidden-0",
        "accent-caveman-forbidden-1",
        "accent-caveman-forbidden-2",
        "accent-caveman-forbidden-3",
        "accent-caveman-forbidden-4",
        "accent-caveman-forbidden-5",
        "accent-caveman-forbidden-6",
        "accent-caveman-forbidden-7",
        "accent-caveman-forbidden-8",
        "accent-caveman-forbidden-9",
        "accent-caveman-forbidden-10",
        "accent-caveman-forbidden-11",
        "accent-caveman-forbidden-12",
        "accent-caveman-forbidden-13",
        "accent-caveman-forbidden-14",
        "accent-caveman-forbidden-15",
        "accent-caveman-forbidden-16",
        "accent-caveman-forbidden-17",
        "accent-caveman-forbidden-18",
        "accent-caveman-forbidden-19",
        "accent-caveman-forbidden-20",
        "accent-caveman-forbidden-21",
        "accent-caveman-forbidden-22",
        "accent-caveman-forbidden-23",
        "accent-caveman-forbidden-24",
    };

    [ViewVariables]
    public static readonly List<string> Numbers = new()
    {
        "accent-caveman-numbers-0",
        "accent-caveman-numbers-1",
        "accent-caveman-numbers-2",
        "accent-caveman-numbers-3",
        "accent-caveman-numbers-4",
        "accent-caveman-numbers-5",
        "accent-caveman-numbers-6",
        "accent-caveman-numbers-7",
        "accent-caveman-numbers-8",
        "accent-caveman-numbers-9",
        "accent-caveman-numbers-10",
    };

    [ViewVariables]
    public const string LargeNumberString = "accent-caveman-numbers-many";

    [ViewVariables]
    public static readonly List<string> Grunts = new()
    {
        "accent-caveman-grunts-0",
        "accent-caveman-grunts-1",
        "accent-caveman-grunts-2",
        "accent-caveman-grunts-3",
        "accent-caveman-grunts-4",
        "accent-caveman-grunts-5",
        "accent-caveman-grunts-6",
        "accent-caveman-grunts-7",
        "accent-caveman-grunts-8",
        "accent-caveman-grunts-9",
        "accent-caveman-grunts-10",
        "accent-caveman-grunts-11",
        "accent-caveman-grunts-12",
        "accent-caveman-grunts-13",
        "accent-caveman-grunts-14",
    };

}
