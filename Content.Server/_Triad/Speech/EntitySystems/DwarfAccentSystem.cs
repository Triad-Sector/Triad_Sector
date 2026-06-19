/*
 * Triad - This file is licensed under AGPLv3
 * Copyright (c) 2025 Triad Contributors
 * See AGPLv3.txt for details.
 */
using Content.Server._Triad.Speech.Components;
using Content.Server.Speech;
using Content.Server.Speech.EntitySystems;
using Robust.Shared.Random;
using System.Text.RegularExpressions;

namespace Content.Server._Triad.Speech.EntitySystems;

// Triad: the Dwarven Brogue (TF2-Demoman-thick Scots). Merged from the near-identical Scottish + Dwarf
// word lists onto the shared AccentHelpers, plus the signature Scots phonetics. Deliberately does NOT
// touch "th" -- that keeps the brogue distinct from the German/French accents, which both front th->z.
// Trilled 'r' is intentionally not modelled -- it does not survive as text.
public sealed class DwarfAccentSystem : EntitySystem
{
    // Glottal stop on intervocalic t/tt: water -> wa'er, butter -> bu'er. Only fire before a/e/o/y, never
    // i/u, so "nation"/"nature"/"situation" (where t is a /sh/-/ch/ sound) are left alone.
    private static readonly Regex RegexGlottal = new(@"([aeiou])tt?([aeoy])", RegexOptions.IgnoreCase);
    // Scots ch-velar: the "-ight" cluster becomes "-icht" (night -> nicht, right -> richt, fight -> ficht).
    private static readonly Regex RegexIght = new("ight", RegexOptions.IgnoreCase);
    // Scots vocalised L: word-final "-all" becomes "-aw" (all -> aw, call -> caw, wall -> waw, small -> smaw).
    private static readonly Regex RegexAll = new(@"all\b", RegexOptions.IgnoreCase);

    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<DwarfAccentComponent, AccentGetEvent>(OnAccentGet);
    }

    public string Accentuate(string message, DwarfAccentComponent component)
    {
        var slight = component.Strength == AccentStrength.Slight;
        var msg = _replacement.ApplyReplacements(message, slight ? "dwarf_slight" : "dwarf");

        // g-drop is highly intelligible, kept in both tiers.
        msg = AccentHelpers.DropG(msg);

        if (slight)
        {
            // Slight: keep only the glottal stop, gated per-message; the ch-velar (-icht) and vocalised-L
            // (-aw) passes are too mangling for the intelligible tier, so they are excluded.
            if (_random.Prob(component.SlightChance))
                msg = RegexGlottal.Replace(msg, "$1'$2");
        }
        else
        {
            msg = RegexGlottal.Replace(msg, "$1'$2");
            msg = AccentHelpers.ReplaceCasePreserving(msg, RegexIght, "icht");
            msg = AccentHelpers.ReplaceCasePreserving(msg, RegexAll, "aw");
        }

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
