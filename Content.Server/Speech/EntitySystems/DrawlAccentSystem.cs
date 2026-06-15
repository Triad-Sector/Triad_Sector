using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Content.Server.Speech.Components;
using Robust.Shared.Random;

namespace Content.Server.Speech.EntitySystems;

// Triad: expanded from upstream SouthernAccentSystem into a shared "drawl" engine driving
// both the Southern and Cowboy accents. Same g-dropping phonetics, but the word list and the
// probabilistic prefix/suffix tic pools are data-driven per flavor (IDrawlAccentComponent), so
// Cowboy (rich vocab, no engine) and Southern (engine, thin vocab) stop being two half-accents.
// Renamed from SouthernAccentSystem so the shared role is obvious; a future upstream edit to that
// file will surface here as a rename/modify conflict.
public sealed class DrawlAccentSystem : EntitySystem
{
    // Triad: match whole -ing words so we can spare short non-gerund nouns. The bare upstream `ing\b`
    // mangled "king/ring/wing/thing" into "kin'/rin'/win'/thin'"; gerunds like "doing/morning" still drop.
    private static readonly Regex RegexIngWord = new(@"\b(\w+?)(ing)\b", RegexOptions.IgnoreCase);

    private static readonly HashSet<string> KeepIng = new(StringComparer.OrdinalIgnoreCase)
    {
        "king", "ring", "thing", "wing", "spring", "string", "bring",
        "sing", "sting", "swing", "cling", "fling", "sling", "bling", "zing", "ping", "ding",
    };

    private static readonly Regex RegexLowerAnd = new(@"\band\b");
    private static readonly Regex RegexUpperAnd = new(@"\bAND\b");
    private static readonly Regex RegexLowerDve = new(@"d've\b");
    private static readonly Regex RegexUpperDve = new(@"D'VE\b");

    // Triad: borrowed from PirateAccentSystem to keep a leading ALLCAPS word capped when we prepend.
    private static readonly Regex FirstWordAllCapsRegex = new(@"^(\S+)");

    // Triad: re-agree "a"/"an" after a swap flips a word's vowel-sound ("a nukie" -> "an outlaw").
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

    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SouthernAccentComponent, AccentGetEvent>(OnAccent);
        SubscribeLocalEvent<CowboyAccentComponent, AccentGetEvent>(OnAccent); // Triad: Cowboy rides the same engine
    }

    private void OnAccent(EntityUid uid, SouthernAccentComponent component, AccentGetEvent args)
    {
        args.Message = Drawl(args.Message, component);
    }

    private void OnAccent(EntityUid uid, CowboyAccentComponent component, AccentGetEvent args)
    {
        args.Message = Drawl(args.Message, component);
    }

    /// <summary>
    ///     Applies the flavor's word list, the shared g-dropping drawl, then an optional
    ///     probabilistic prefix and suffix drawn from the component's tic pools.
    /// </summary>
    public string Drawl(string message, IDrawlAccentComponent accent)
    {
        // Word-list swaps first (per-flavor: "southern" / "cowboy"), then phonetics, then tics.
        var msg = _replacement.ApplyReplacements(message, accent.Accent);

        //They shoulda started runnin' an' hidin' from me!
        msg = RegexIngWord.Replace(msg, m =>
        {
            if (KeepIng.Contains(m.Value))
                return m.Value;

            // Preserve the stem's case; cap the dropped suffix only when the original "ing" was a shout.
            var ingIsUpper = m.Groups[2].Value.All(char.IsUpper);
            return m.Groups[1].Value + (ingIsUpper ? "IN'" : "in'");
        });
        msg = RegexLowerAnd.Replace(msg, "an'");
        msg = RegexUpperAnd.Replace(msg, "AN'");
        msg = RegexLowerDve.Replace(msg, "da");
        msg = RegexUpperDve.Replace(msg, "DA");

        if (string.IsNullOrWhiteSpace(msg))
            return msg;

        // Triad: a swap can flip a word's vowel-sound, leaving "a outlaw" / "an space critter".
        msg = FixArticles(msg);

        // Triad: probabilistic prefix tic, capitalization handled the way Pirate does, minus Pirate's
        // single-letter quirk: a lone leading "I" is not a shout, so require >1 char before all-capping.
        if (accent.Prefixes.Count > 0 && _random.Prob(accent.PrefixProb))
        {
            var firstWord = FirstWordAllCapsRegex.Match(msg).Value;
            var firstWordAllCaps = firstWord.Length > 1 && !firstWord.Any(char.IsLower);
            var prefix = Loc.GetString(_random.Pick(accent.Prefixes));

            if (firstWordAllCaps)
                prefix = prefix.ToUpper();
            // Lower a real sentence-initial capital ("Stop" -> "stop") now that it's mid-sentence, but
            // leave a standalone pronoun "I", its contractions ("I'm"/"I'd"), and lowercase words alone.
            else if (firstWord.Length > 1 && char.IsUpper(firstWord[0]) && !firstWord.StartsWith("I'"))
                msg = char.ToLower(msg[0]) + msg[1..];

            msg = prefix + " " + msg;
        }

        // Triad: probabilistic suffix tic. Pool values lead with ", " and carry no end punctuation; slip
        // the tic in just before any trailing .!? so "rustler!" -> "rustler, I reckon!", not "rustler!, ...".
        if (accent.Suffixes.Count > 0 && _random.Prob(accent.SuffixProb))
        {
            var suffix = Loc.GetString(_random.Pick(accent.Suffixes));
            var trimmed = msg.TrimEnd();

            var punctLen = 0;
            while (punctLen < trimmed.Length && trimmed[^(punctLen + 1)] is '.' or '!' or '?')
                punctLen++;

            var core = trimmed[..(trimmed.Length - punctLen)];
            var punct = trimmed[(trimmed.Length - punctLen)..];
            msg = core + suffix + punct;
        }

        return msg;
    }

    // Triad: correct "a"/"an" for the following word, so swaps that flip its initial vowel-sound
    // ("a toilet" -> "an outhouse", "an alien" -> "a space critter") read right. Capitalization-preserving.
    private static string FixArticles(string text)
    {
        return ArticleRegex.Replace(text, m =>
        {
            var article = m.Groups[1].Value;
            var corrected = StartsWithVowelSound(m.Groups[3].Value) ? "an" : "a";
            if (char.IsUpper(article[0]))
                corrected = char.ToUpperInvariant(corrected[0]) + corrected[1..];
            return corrected + m.Groups[2].Value + m.Groups[3].Value;
        });
    }

    private static bool StartsWithVowelSound(string word)
    {
        if (AnExceptions.Contains(word))
            return true;
        if (AExceptions.Contains(word))
            return false;
        return "aeiou".IndexOf(char.ToLowerInvariant(word[0])) >= 0;
    }
}

// Triad: shared config surface for drawl-family accents (Southern, Cowboy). Each flavor names its
// own ReplacementAccent word list and its own prefix/suffix loc-key pools, tunable per entity.
public interface IDrawlAccentComponent
{
    string Accent { get; }
    List<string> Prefixes { get; }
    float PrefixProb { get; }
    List<string> Suffixes { get; }
    float SuffixProb { get; }
}
