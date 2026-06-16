using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Content.Server.Speech.EntitySystems;

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
}
