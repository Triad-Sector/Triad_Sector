/*
 * Triad - This file is licensed under AGPLv3
 * Copyright (c) 2025 Triad Contributors
 * See AGPLv3.txt for details.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Robust.Shared.Random;

namespace Content.Server._Triad.Speech.EntitySystems;

// Triad: shared text helpers for speech accents. The gnarly, reusable bits -- g-dropping with a
// non-gerund keep-list, a/an re-agreement, and caps-aware affix placement -- live here so every
// accent system (DrawlAccentSystem for Southern/Cowboy, BoganAccentSystem, and future ones) composes
// them instead of copy-pasting. Pure string in/out, no DI, so it stays trivially testable.
public static class AccentHelpers
{
    private static readonly Regex IngWord = new(@"\b(\w+?)(ing)\b", RegexOptions.IgnoreCase);

    // Short words that merely END in -ing but are not gerunds; never drop their g.
    private static readonly HashSet<string> KeepIng = new(StringComparer.OrdinalIgnoreCase)
    {
        "king", "ring", "thing", "wing", "spring", "string", "bring",
        "sing", "sting", "swing", "cling", "fling", "sling", "bling", "zing", "ping", "ding",
    };

    private static readonly Regex ArticleRegex = new(@"\b([Aa]n?)(\s+)([\w'\-]+)");

    // Consonant first letter but vowel SOUND -> wants "an".
    private static readonly HashSet<string> AnExceptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "hour", "hourly", "honest", "honestly", "honor", "honour", "honorable", "heir", "heirloom",
    };

    // Vowel first letter but consonant SOUND -> wants "a".
    private static readonly HashSet<string> AExceptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "unicorn", "unique", "union", "united", "universe", "university", "unit", "uniform",
        "useful", "used", "user", "utensil", "european", "ewe", "one", "once",
    };

    private static readonly Regex FirstWord = new(@"^(\S+)");

    // Word-initial h plus the letter after it, so a dropped capital ("Hello") can promote its case onto
    // the next letter ("'Ello") instead of leaving a lowercase opener.
    private static readonly Regex InitialH = new(@"(?<!\w)h(\w)?", RegexOptions.IgnoreCase);

    /// <summary>Drops the g from -ing gerunds ("running" -> "runnin'") while sparing short -ing nouns.</summary>
    public static string DropG(string message)
    {
        return IngWord.Replace(message, m =>
        {
            if (KeepIng.Contains(m.Value))
                return m.Value;

            // Preserve the stem's case; cap the dropped suffix only when the original "ing" was a shout.
            var ingIsUpper = m.Groups[2].Value.All(char.IsUpper);
            return m.Groups[1].Value + (ingIsUpper ? "IN'" : "in'");
        });
    }

    /// <summary>Re-agrees "a"/"an" for the following word, after a swap flips its initial vowel-sound.</summary>
    public static string FixArticles(string message)
    {
        return ArticleRegex.Replace(message, m =>
        {
            var corrected = StartsWithVowelSound(m.Groups[3].Value) ? "an" : "a";
            if (char.IsUpper(m.Groups[1].Value[0]))
                corrected = char.ToUpperInvariant(corrected[0]) + corrected[1..];
            return corrected + m.Groups[2].Value + m.Groups[3].Value;
        });
    }

    private static bool StartsWithVowelSound(string word)
    {
        // Triad: h-dropping dialects (goblin cockney) turn "house" -> "'ouse", which sounds vowel-initial
        // and wants "an 'ouse"; skip a leading apostrophe so the vowel check sees the real first sound.
        word = word.TrimStart('\'');
        if (word.Length == 0)
            return false;
        if (AnExceptions.Contains(word))
            return true;
        if (AExceptions.Contains(word))
            return false;
        return "aeiou".IndexOf(char.ToLowerInvariant(word[0])) >= 0;
    }

    /// <summary>
    ///     Prepends a prefix tic and inserts the separating space. Shouts the prefix only for a genuine
    ///     all-caps opener (a lone "I"/"I'm" is not a shout), and lowers a real sentence-initial capital.
    /// </summary>
    public static string PrependPrefix(string message, string prefix)
    {
        if (string.IsNullOrWhiteSpace(message))
            return message;

        var firstWord = FirstWord.Match(message).Value;
        var allCaps = firstWord.Length > 1 && !firstWord.Any(char.IsLower);

        if (allCaps)
            prefix = prefix.ToUpper();
        else if (firstWord.Length > 1 && char.IsUpper(firstWord[0]) && !firstWord.StartsWith("I'"))
            message = char.ToLower(message[0]) + message[1..];

        return prefix + " " + message;
    }

    /// <summary>
    ///     Appends a suffix tic just before any trailing .!? so "rustler!" -> "rustler, mate!", not
    ///     "rustler!, mate". A suffix that carries its OWN terminal .!? (e.g. ", da?") supersedes the
    ///     message's punctuation instead of stacking onto it ("me." -> "me, da?", not "me, da?.").
    /// </summary>
    public static string AppendSuffix(string message, string suffix)
    {
        var trimmed = message.TrimEnd();

        var punctLen = 0;
        while (punctLen < trimmed.Length && trimmed[^(punctLen + 1)] is '.' or '!' or '?')
            punctLen++;

        // The suffix brings its own end punctuation: let it replace the sentence's, don't double up.
        if (suffix.Length > 0 && suffix[^1] is '.' or '!' or '?')
            return trimmed[..(trimmed.Length - punctLen)] + suffix;

        return trimmed[..(trimmed.Length - punctLen)] + suffix + trimmed[(trimmed.Length - punctLen)..];
    }

    /// <summary>
    ///     Regex-replaces every match while copying the matched token's casing onto the replacement,
    ///     so a phonetic pass ("water" -> "vater") doesn't flatten capitalization the way a raw
    ///     <see cref="Regex.Replace(string, string)"/> would. Use this for fixed-string phonetic swaps;
    ///     for capture-group transforms call <see cref="MatchCase"/> directly inside your own evaluator.
    /// </summary>
    public static string ReplaceCasePreserving(string message, Regex regex, string replacement)
    {
        return regex.Replace(message, m => MatchCase(m.Value, replacement));
    }

    /// <summary>
    ///     Triad: per-match probabilistic variant of <see cref="ReplaceCasePreserving(string, Regex, string)"/>.
    ///     Each match is replaced only on a <paramref name="chance"/> roll (case preserved), else left as-is.
    ///     Used by the "slight" accent tier to make a phonetic pass an occasional slip. At chance >= 1 it
    ///     always replaces (no RNG call); at chance &lt;= 0 it never does.
    /// </summary>
    public static string ReplaceCasePreserving(string message, Regex regex, string replacement, IRobustRandom random, float chance)
    {
        return regex.Replace(message, m =>
        {
            if (chance < 1f && !random.Prob(chance))
                return m.Value;
            return MatchCase(m.Value, replacement);
        });
    }

    /// <summary>
    ///     Copies the casing shape of <paramref name="source"/> onto <paramref name="replacement"/>:
    ///     an all-caps shout stays a shout, a leading capital stays capitalized, anything else is
    ///     returned verbatim. Mirrors the capitalization logic in ReplacementAccentSystem so bespoke
    ///     phonetic passes match the word-swap engine instead of each re-inventing (and breaking) it.
    /// </summary>
    /// <summary>
    ///     Drops a word-initial h (cockney/French h-dropping: "have" -> "'ave"), carrying a dropped
    ///     capital onto the surviving next letter so a sentence opener stays capitalized ("Hello" -> "'Ello").
    /// </summary>
    public static string DropInitialH(string message)
    {
        return InitialH.Replace(message, m =>
        {
            var next = m.Groups[1].Value;
            if (char.IsUpper(m.Value[0]) && next.Length > 0)
                next = char.ToUpperInvariant(next[0]).ToString();
            return "'" + next;
        });
    }

    public static string MatchCase(string source, string replacement)
    {
        if (replacement.Length == 0 || source.Length == 0)
            return replacement;

        // All-caps shout -> shout the replacement too, but a lone 1-char match ("I") only shouts a
        // 1-char replacement, so "I" -> "Ah" doesn't become "AH" (same guard the swap engine uses).
        if (!source.Any(char.IsLower) && (source.Length > 1 || replacement.Length == 1))
            return replacement.ToUpperInvariant();

        if (char.IsUpper(source[0]))
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];

        return replacement;
    }
}
