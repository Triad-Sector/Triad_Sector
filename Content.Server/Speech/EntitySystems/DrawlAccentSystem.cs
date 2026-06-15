using System.Text.RegularExpressions;
using Content.Server.Speech.Components;
using Robust.Shared.Random;

namespace Content.Server.Speech.EntitySystems;

// Triad: expanded from upstream SouthernAccentSystem into a shared "drawl" engine driving both the
// Southern and Cowboy accents. Shared mechanics (g-drop, a/an, caps-aware tics) live in AccentHelpers;
// this system adds the drawl-specific phonetics and the data-driven per-flavor word list + tic pools
// (IDrawlAccentComponent). Renamed from SouthernAccentSystem so the shared role is obvious; a future
// upstream edit to that file will surface here as a rename/modify conflict.
public sealed class DrawlAccentSystem : EntitySystem
{
    // Drawl-specific phonetics (not shared): "and" -> "an'", "would've" -> "woulda".
    private static readonly Regex RegexLowerAnd = new(@"\band\b");
    private static readonly Regex RegexUpperAnd = new(@"\bAND\b");
    private static readonly Regex RegexLowerDve = new(@"d've\b");
    private static readonly Regex RegexUpperDve = new(@"D'VE\b");

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
    ///     Applies the flavor's word list, the g-dropping drawl, then an optional probabilistic prefix
    ///     and suffix drawn from the component's tic pools.
    /// </summary>
    public string Drawl(string message, IDrawlAccentComponent accent)
    {
        // Word-list swaps first (per-flavor: "southern" / "cowboy"), then phonetics, then tics.
        var msg = _replacement.ApplyReplacements(message, accent.Accent);

        //They shoulda started runnin' an' hidin' from me!
        msg = AccentHelpers.DropG(msg);
        msg = RegexLowerAnd.Replace(msg, "an'");
        msg = RegexUpperAnd.Replace(msg, "AN'");
        msg = RegexLowerDve.Replace(msg, "da");
        msg = RegexUpperDve.Replace(msg, "DA");

        if (string.IsNullOrWhiteSpace(msg))
            return msg;

        // A swap can flip a word's vowel-sound, leaving "a outlaw" / "an space critter".
        msg = AccentHelpers.FixArticles(msg);

        if (accent.Prefixes.Count > 0 && _random.Prob(accent.PrefixProb))
            msg = AccentHelpers.PrependPrefix(msg, Loc.GetString(_random.Pick(accent.Prefixes)));

        if (accent.Suffixes.Count > 0 && _random.Prob(accent.SuffixProb))
            msg = AccentHelpers.AppendSuffix(msg, Loc.GetString(_random.Pick(accent.Suffixes)));

        return msg;
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
