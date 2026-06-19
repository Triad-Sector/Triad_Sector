using Content.Server._NF.Speech.Components;
using Content.Server.Speech;
using Content.Server.Speech.EntitySystems;
using Robust.Shared.Random;
using System.Text.RegularExpressions;
using Content.Server._Triad.Speech.EntitySystems; // Triad: AccentHelpers relocated to _Triad

namespace Content.Server._NF.Speech.EntitySystems;

// Originally a copy of SouthernAccentSystem by UBlueberry (https://github.com/UBlueberry).
// Triad: cockney + thieves'-cant. Phonetics ride the shared AccentHelpers. th-fronting is now a RULE
// (the .ftl header always advertised it but only the word-list did it): intervocalic th -> v
// (weather -> weavah), otherwise th -> f (north -> norf, both -> bof). The function-word th's
// (the/this/that/them/with...) are handled earlier by the word list (da/dis/dat/dem/wiv) and the
// "the -> da" pass, so they never reach the f/v rule. -er is a clean non-rhotic -ah (water -> watah),
// and g-dropping goes through the shared keep-list so king/ring survive.
public sealed class GoblinAccentSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;

    private static readonly Regex RegexThe = new(@"\bthe\b", RegexOptions.IgnoreCase);
    // Voiced th between vowels -> v (mother -> mover, weather -> weaver). Keeps the flanking vowels.
    private static readonly Regex RegexThVoiced = new(@"([aeiou])th([aeiou])", RegexOptions.IgnoreCase);
    // Any remaining th -> f (think is already list-swapped to "fink"; this catches north/both/thick...).
    private static readonly Regex RegexThVoiceless = new("th", RegexOptions.IgnoreCase);
    private static readonly Regex RegexAnd = new(@"\b(an)d\b", RegexOptions.IgnoreCase);
    private static readonly Regex RegexTt = new(@"([aeiouy])tt", RegexOptions.IgnoreCase);
    // Non-rhotic -er/-ers -> -ah/-ahs, but only after a word char so an already-h-dropped "'er" (her)
    // is left alone instead of becoming "'ah".
    private static readonly Regex RegexErs = new(@"(?<=\w)ers\b", RegexOptions.IgnoreCase);
    private static readonly Regex RegexEr = new(@"(?<=\w)er\b", RegexOptions.IgnoreCase);
    private static readonly Regex RegexOf = new(@"\b(o)f\b", RegexOptions.IgnoreCase);
    private static readonly Regex RegexSelf = new(@"self\b", RegexOptions.IgnoreCase);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GoblinAccentComponent, AccentGetEvent>(OnAccent);
    }

    private void OnAccent(EntityUid uid, GoblinAccentComponent component, AccentGetEvent args)
    {
        var message = _replacement.ApplyReplacements(args.Message, "goblin_accent");

        // "the" -> da, case-preserving, BEFORE th-fronting so it never becomes "fe".
        message = RegexThe.Replace(message, m =>
            m.Value == m.Value.ToUpperInvariant() ? "DA" : char.IsUpper(m.Value[0]) ? "Da" : "da");

        message = AccentHelpers.DropG(message);                 // -ing -> -in' (keep-list spares king/ring)

        // th-fronting: intervocalic -> v (keep the flanking vowels), everything else -> f.
        message = RegexThVoiced.Replace(message, m =>
        {
            var th = m.Value.Substring(m.Groups[1].Length, 2);
            var v = char.IsUpper(th[0]) ? "V" : "v";
            return m.Groups[1].Value + v + m.Groups[2].Value;
        });
        message = AccentHelpers.ReplaceCasePreserving(message, RegexThVoiceless, "f");

        message = RegexAnd.Replace(message, "$1'");             // and -> an'
        message = RegexTt.Replace(message, "$1'");              // glottal stop: better -> be'er
        message = AccentHelpers.ReplaceCasePreserving(message, RegexErs, "ahs"); // papers -> papahs
        message = AccentHelpers.ReplaceCasePreserving(message, RegexEr, "ah");   // water -> watah
        message = RegexOf.Replace(message, "$1'");             // of -> o'
        message = AccentHelpers.DropInitialH(message);          // h-dropping: hello -> 'ello, Hello -> 'Ello
        message = AccentHelpers.ReplaceCasePreserving(message, RegexSelf, "sewf"); // self -> sewf

        // The rich phonetics above are the goblin identity; layer the rubric on top via the shared
        // helpers -- a/an re-agreement (handles h-dropped "a 'ouse" -> "an 'ouse") and data-driven tics.
        if (!string.IsNullOrWhiteSpace(message))
        {
            message = AccentHelpers.FixArticles(message);

            if (component.Prefixes.Count > 0 && _random.Prob(component.PrefixProb))
                message = AccentHelpers.PrependPrefix(message, Loc.GetString(_random.Pick(component.Prefixes)));

            if (component.Suffixes.Count > 0 && _random.Prob(component.SuffixProb))
                message = AccentHelpers.AppendSuffix(message, Loc.GetString(_random.Pick(component.Suffixes)));
        }

        args.Message = message;
    }
}
