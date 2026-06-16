using Content.Server._DV.Speech.Components;
using Content.Server.Speech;
using Content.Server.Speech.EntitySystems;
using Robust.Shared.Random;
using System.Text.RegularExpressions;

namespace Content.Server._DV.Speech.EntitySystems;

// Triad: the Dwarven Brogue. Merged from the near-identical Scottish + Dwarf word lists onto the shared
// AccentHelpers, plus the signature Scots glottal stop. Used by the Dwarf species and the (renamed)
// brogue trait. Trilled 'r' is intentionally not modelled -- it does not survive as text.
public sealed class DwarfAccentSystem : EntitySystem
{
    // Glottal stop on intervocalic t/tt: water -> wa'er, butter -> bu'er. Only fire before a/e/o/y, never
    // i/u, so "nation"/"nature"/"situation" (where t is a /sh/-/ch/ sound) are left alone.
    private static readonly Regex RegexGlottal = new(@"([aeiou])tt?([aeoy])", RegexOptions.IgnoreCase);

    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<DwarfAccentComponent, AccentGetEvent>(OnAccentGet);
    }

    public string Accentuate(string message, DwarfAccentComponent component)
    {
        var msg = _replacement.ApplyReplacements(message, "dwarf");

        // Phonetics: g-drop (keep-list spares king/ring) then the glottal stop. Vowel shifts (hoose/doon)
        // are word-listed, a blanket ou->oo regex would wreck your/group/four.
        msg = AccentHelpers.DropG(msg);
        msg = RegexGlottal.Replace(msg, "$1'$2");

        if (string.IsNullOrWhiteSpace(msg))
            return msg;

        msg = AccentHelpers.FixArticles(msg);

        if (component.Prefixes.Count > 0 && _random.Prob(component.PrefixProb))
            msg = AccentHelpers.PrependPrefix(msg, Loc.GetString(_random.Pick(component.Prefixes)));

        if (component.Suffixes.Count > 0 && _random.Prob(component.SuffixProb))
            msg = AccentHelpers.AppendSuffix(msg, Loc.GetString(_random.Pick(component.Suffixes)));

        return msg;
    }

    private void OnAccentGet(EntityUid uid, DwarfAccentComponent component, AccentGetEvent args)
    {
        args.Message = Accentuate(args.Message, component);
    }
}
