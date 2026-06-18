/*
 * Triad - This file is licensed under AGPLv3
 * Copyright (c) 2025 Triad Contributors
 * See AGPLv3.txt for details.
 */
using System.Text.RegularExpressions;
using Content.Server._Triad.Speech.Components;
using Content.Server.Speech;
using Content.Server.Speech.EntitySystems;
using Robust.Shared.Random;

namespace Content.Server._Triad.Speech.EntitySystems;

/// <summary>
///     New Brooklyn everyman accent. Inspired by the upstream Mobster phonetics but built on the shared
///     AccentHelpers, with a generalized NYC working-class vocabulary (the pizzeria/garage/corner guy,
///     not the wiseguy).
/// </summary>
public sealed class NewBrooklynAccentSystem : EntitySystem
{
    // Non-rhotic coda r: "or"/"ar" drop the r ONLY before a consonant or word-end (work -> wuhk, car ->
    // cah). The (?![aeiour]) lookahead spares both a following vowel (intervocalic r: "sorry", "care")
    // AND a doubled r ("carry", "hurry"), which the old (?=\w) form wrongly mangled into "cahry".
    private static readonly Regex RegexOr = new(@"(?<=\w)or(?![aeiour])", RegexOptions.IgnoreCase);
    private static readonly Regex RegexAr = new(@"(?<=\w)ar(?![aeiour])", RegexOptions.IgnoreCase);
    // NYC th-stopping for words the list doesn't cover. Intervocalic voiced th -> d (brother -> broder),
    // everything else -> t (think -> tink, both -> bot). Voiced function words (the/this/that/them...)
    // are already d-stopped by the word list, which runs first.
    private static readonly Regex RegexThVoiced = new(@"([aeiou])th([aeiou])", RegexOptions.IgnoreCase);
    private static readonly Regex RegexThVoiceless = new("th", RegexOptions.IgnoreCase);

    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<NewBrooklynAccentComponent, AccentGetEvent>(OnAccentGet);
    }

    public string Accentuate(string message, NewBrooklynAccentComponent component)
    {
        // Text manipulations first, then tics (so a swap can't strand a leading capital under the prefix).
        var msg = _replacement.ApplyReplacements(message, "newbrooklyn");

        msg = AccentHelpers.DropG(msg);                                              // -ing -> -in' (keep-list spares string/bring)
        msg = RegexThVoiced.Replace(msg, m =>                                        // intervocalic th -> d
        {
            var th = m.Value.Substring(m.Groups[1].Length, 2);
            var d = char.IsUpper(th[0]) ? "D" : "d";
            return m.Groups[1].Value + d + m.Groups[2].Value;
        });
        msg = AccentHelpers.ReplaceCasePreserving(msg, RegexThVoiceless, "t");       // th -> t
        msg = AccentHelpers.ReplaceCasePreserving(msg, RegexOr, "uh");               // work -> wuhk
        msg = AccentHelpers.ReplaceCasePreserving(msg, RegexAr, "ah");               // car -> cah, hard -> hahd

        if (string.IsNullOrWhiteSpace(msg))
            return msg;

        msg = AccentHelpers.FixArticles(msg);

        if (component.Prefixes.Count > 0 && _random.Prob(component.PrefixProb))
            msg = AccentHelpers.PrependPrefix(msg, Loc.GetString(_random.Pick(component.Prefixes)));

        if (component.Suffixes.Count > 0 && _random.Prob(component.SuffixProb))
            msg = AccentHelpers.AppendSuffix(msg, Loc.GetString(_random.Pick(component.Suffixes)));

        return msg;
    }

    private void OnAccentGet(EntityUid uid, NewBrooklynAccentComponent component, AccentGetEvent args)
    {
        args.Message = Accentuate(args.Message, component);
    }
}
