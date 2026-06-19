// Triad: enriched Russian accent. Identifiers kept upstream-named for clean cherry-picking;
// Enriched onto the shared AccentHelpers: article-drop (the signature Slavic cue), a/an fixup, and
// data-driven prefix/suffix tics, with the faux-Cyrillic letter-sub kept LAST so the helpers and
// caps logic operate on Latin text, never on the substituted glyphs.
using System.Text;
using System.Text.RegularExpressions;
using Content.Server.Speech;
using Content.Server.Speech.Components;
using Robust.Shared.Random;
using Content.Server._Triad.Speech; // Triad: AccentStrength relocated to _Triad
using Content.Server._Triad.Speech.EntitySystems; // Triad: AccentHelpers relocated to _Triad

namespace Content.Server.Speech.EntitySystems;

public sealed class RussianAccentSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;

    // Slavic English drops articles: "pass me the wrench" -> "pass me wrench". Match a standalone
    // the/a/an as a whole word (with its following space) and strip it. Sentence-initial capital is
    // re-stamped onto the next word so "The captain" -> "Captain", not "captain".
    private static readonly Regex ArticleDrop =
        new(@"\b([Tt]he|[Aa]n?)\s+", RegexOptions.Compiled);
    private static readonly Regex CopulaDrop =
        new(@"\b(is|are|am|was|were|be)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public override void Initialize()
    {
        SubscribeLocalEvent<RussianAccentComponent, AccentGetEvent>(OnAccent);
    }

    public string Accentuate(string message, RussianAccentComponent component)
    {
        var slight = component.Strength == AccentStrength.Slight;
        var dropMult = slight ? component.SlightChance : 1f;
        var msg = _replacement.ApplyReplacements(message, slight ? "russian_slight" : "russian");

        // Drop articles and copulas, preserving a leading capital by handing it to the now-first word.
        // The signature Slavic cues, rolled per-word so they stay occasional slips, not a constant clip.
        // dropMult scales both rates down in the slight tier so clipping is lighter than thick.
        msg = DropWords(msg, ArticleDrop, component.ArticleDropProb * dropMult);
        msg = DropWords(msg, CopulaDrop, component.CopulaDropProb * dropMult);

        if (string.IsNullOrWhiteSpace(msg))
            return msg;

        if (component.Prefixes.Count > 0 && _random.Prob(component.PrefixProb))
            msg = AccentHelpers.PrependPrefix(msg, Loc.GetString(_random.Pick(component.Prefixes)));

        if (component.Suffixes.Count > 0 && _random.Prob(component.SuffixProb))
            msg = AccentHelpers.AppendSuffix(msg, Loc.GetString(_random.Pick(component.Suffixes)));

        // Faux-Cyrillic glyph swap runs LAST in BOTH tiers (it is the Russian identity). Slight only
        // lightens the article/copula clipping via dropMult; the glyph swap is unchanged.
        return Cyrillicize(msg);
    }

    private string DropWords(string message, Regex regex, float prob)
    {
        if (prob <= 0f)
            return message;

        var wasSentenceCap = message.Length > 0 && char.IsUpper(message[0]);

        // Roll per match: keep it (return the whole match) or drop it (return empty) on a hit.
        var result = regex.Replace(message, m => _random.Prob(prob) ? string.Empty : m.Value);

        // If a sentence-initial word was the one dropped, re-capitalize the new first letter.
        if (wasSentenceCap && result.Length > 0 && char.IsLower(result[0]))
            result = char.ToUpperInvariant(result[0]) + result[1..];

        return result;
    }

    private static string Cyrillicize(string message)
    {
        var sb = new StringBuilder(message);
        for (var i = 0; i < sb.Length; i++)
        {
            sb[i] = sb[i] switch
            {
                'A' => 'Д',
                'b' => 'в',
                'N' => 'И',
                'n' => 'и',
                'K' => 'К',
                'k' => 'к',
                'm' => 'м',
                'h' => 'н',
                't' => 'т',
                'R' => 'Я',
                'r' => 'я',
                'Y' => 'У',
                'W' => 'Ш',
                'w' => 'ш',
                _ => sb[i]
            };
        }

        return sb.ToString();
    }

    private void OnAccent(EntityUid uid, RussianAccentComponent component, AccentGetEvent args)
    {
        args.Message = Accentuate(args.Message, component);
    }
}
