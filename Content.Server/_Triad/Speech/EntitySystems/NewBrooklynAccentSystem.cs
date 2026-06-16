// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

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
    // -ing -> -in' (thinkin'), but only after two letters so short -ing words are spared.
    private static readonly Regex RegexIng = new(@"(?<=\w\w)(in)g(?!\w)", RegexOptions.IgnoreCase);
    // or -> uh, ar -> ah mid-word (fuhget, tahget), case-preserving.
    private static readonly Regex RegexLowerOr = new(@"(?<=\w)o[Rr](?=\w)");
    private static readonly Regex RegexUpperOr = new(@"(?<=\w)O[Rr](?=\w)");
    private static readonly Regex RegexLowerAr = new(@"(?<=\w)a[Rr](?=\w)");
    private static readonly Regex RegexUpperAr = new(@"(?<=\w)A[Rr](?=\w)");

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

        msg = RegexIng.Replace(msg, "$1'");
        msg = RegexLowerOr.Replace(msg, "uh");
        msg = RegexUpperOr.Replace(msg, "UH");
        msg = RegexLowerAr.Replace(msg, "ah");
        msg = RegexUpperAr.Replace(msg, "AH");

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
