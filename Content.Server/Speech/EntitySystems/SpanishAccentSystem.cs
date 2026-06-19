// Triad: enriched Spanish accent. Identifiers kept upstream-named for clean cherry-picking;
// Enriched onto the shared AccentHelpers: word-swaps + data-driven prefix/suffix tics on top of the
// original phonetics (es-insertion before s, inverted ¿/¡ punctuation).
using System.Text;
using System.Text.RegularExpressions;
using Content.Server.Speech;
using Content.Server.Speech.Components;
using Robust.Shared.Random;
using Content.Server._Triad.Speech; // Triad: AccentStrength relocated to _Triad
using Content.Server._Triad.Speech.EntitySystems; // Triad: AccentHelpers relocated to _Triad

namespace Content.Server.Speech.EntitySystems;

public sealed class SpanishAccentSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<SpanishAccentComponent, AccentGetEvent>(OnAccent);
    }

    // Spanish replacement outputs that start with S. The es-insertion phonetic models a Spanish speaker
    // adding E before an ENGLISH s-word (station -> estation); it must NOT re-prefix our own Spanish swaps
    // (si -> esi, señor -> eseñor), so InsertS skips any word already in this set. Keep in sync with the
    // s-initial `replace` values in word_replacements.yml (terralatino).
    private static readonly HashSet<string> SpanishSWords =
        new(StringComparer.OrdinalIgnoreCase) { "si", "señor", "siento" };

    // Spanish realizes English /v/ as /b/ (very -> bery) and English "j" as /x/ ~ "h" (just -> hust).
    private static readonly Regex RegexV = new("v", RegexOptions.IgnoreCase);
    private static readonly Regex RegexJ = new("j", RegexOptions.IgnoreCase);

    public string Accentuate(string message, SpanishAccentComponent component)
    {
        var slight = component.Strength == AccentStrength.Slight;
        var chance = slight ? component.SlightChance : 1f;

        // j -> h on raw English BEFORE swaps (protects "carajo"). Thick-only: hust-for-just hurts slight.
        var msg = slight
            ? message
            : AccentHelpers.ReplaceCasePreserving(message, RegexJ, "h");

        msg = _replacement.ApplyReplacements(msg, slight ? "spanish_slight" : "spanish");

        // v -> b, per-match chance (1 for thick).
        msg = AccentHelpers.ReplaceCasePreserving(msg, RegexV, "b", _random, chance);

        // Phonetic es-insertion before word-initial s+consonant. Per-message gate in slight.
        if (!slight || _random.Prob(component.SlightChance))
            msg = InsertS(msg);

        if (!string.IsNullOrWhiteSpace(msg))
        {
            msg = AccentHelpers.FixArticles(msg);

            if (component.Prefixes.Count > 0 && _random.Prob(component.PrefixProb))
                msg = AccentHelpers.PrependPrefix(msg, Loc.GetString(_random.Pick(component.Prefixes)));

            if (component.Suffixes.Count > 0 && _random.Prob(component.SuffixProb))
                msg = AccentHelpers.AppendSuffix(msg, Loc.GetString(_random.Pick(component.Suffixes)));
        }

        // Inverted opening punctuation runs last; it keys off the sentence's trailing ?/!.
        msg = ReplacePunctuation(msg);
        return msg;
    }

    // A word starting with s/S, captured with its full run of letters so we can skip Spanish swaps.
    private static readonly Regex SWord = new(@"(?<![\w'])([sS])([a-zA-ZñÑ']*)", RegexOptions.Compiled);

    private static string InsertS(string message)
    {
        // Prefix an accented é before a word-initial s/S (station -> éstation). The é reads as the Spanish
        // epenthetic vowel ("eh-station") rather than a typo'd "estation". Never re-accent our own Spanish
        // swaps (si/señor/siento) -- those are already Spanish.
        return SWord.Replace(message, m =>
        {
            var word = m.Groups[1].Value + m.Groups[2].Value;
            if (SpanishSWords.Contains(word))
                return word;

            // Epenthesis only happens before s+CONSONANT (escuela, estación), never s+vowel: "see"/"sun"
            // stay put. This also makes the SpanishSWords skip-set mostly redundant (si/señor are s+vowel).
            var rest = m.Groups[2].Value;
            if (rest.Length == 0 || "aeiouáéíóúüAEIOUÁÉÍÓÚÜ".IndexOf(rest[0]) >= 0)
                return word;

            // Move a leading capital to the inserted vowel and lowercase the now-second letter:
            // "Stop" -> "Éstop" (not "ÉStop"); "stop" -> "éstop".
            if (char.IsUpper(m.Groups[1].Value[0]))
                return "É" + char.ToLowerInvariant(m.Groups[1].Value[0]) + m.Groups[2].Value;

            return "é" + word;
        });
    }

    private static string ReplacePunctuation(string message)
    {
        var sentences = AccentSystem.SentenceRegex.Split(message);
        var msg = new StringBuilder();
        foreach (var s in sentences)
        {
            var toInsert = new StringBuilder();
            for (var i = s.Length - 1; i >= 0 && "?!‽".Contains(s[i]); i--)
            {
                toInsert.Append(s[i] switch
                {
                    '?' => '¿',
                    '!' => '¡',
                    '‽' => '⸘',
                    _ => ' '
                });
            }

            if (toInsert.Length == 0)
                msg.Append(s);
            else
                msg.Append(s.Insert(s.Length - s.TrimStart().Length, toInsert.ToString()));
        }

        return msg.ToString();
    }

    private void OnAccent(EntityUid uid, SpanishAccentComponent component, AccentGetEvent args)
    {
        args.Message = Accentuate(args.Message, component);
    }
}
