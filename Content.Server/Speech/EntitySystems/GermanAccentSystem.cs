// Triad: enriched German accent (TF2-Medic-thick). Identifiers kept upstream-named for clean
// cherry-picking. Phonetics now ride the shared case-preserving helper instead of char-shift hacks:
//   v -> f   (have->hafe, very->fery, over->ofer; German /v/ + obstruent devoicing)
//   w -> v   (water->vater, will->vill; only consonantal w, never the "ow/aw/ew" vowel digraphs)
//   th -> z  (think->zink, with->viz, brother->brozer; German has no /th/)
//   final d/g/b -> t/k/p  (good->goot, dog->dok, club->clup, and -ing->-ink: singing->singink)
// ORDER MATTERS: v->f runs BEFORE w->v so the v's that w->v creates are not devoiced back to f.
// das/umlaut chances stay component DataFields. Word swaps (ja/nein/ze/und...) run first.
using System.Text;
using System.Text.RegularExpressions;
using Content.Server.Speech;
using Content.Server.Speech.Components;
using Robust.Shared.Random;
using Content.Server._Triad.Speech; // Triad: AccentStrength relocated to _Triad
using Content.Server._Triad.Speech.EntitySystems; // Triad: AccentHelpers relocated to _Triad

namespace Content.Server.Speech.EntitySystems;

public sealed class GermanAccentSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;

    private static readonly Regex RegexThe = new(@"(?<=\s|^)the(?=\s|$)", RegexOptions.IgnoreCase);
    private static readonly Regex RegexV = new("v", RegexOptions.IgnoreCase);
    // Consonantal w only: word-initial or after a consonant. The lookbehind spares the "ow/aw/ew" vowel
    // digraphs (so "now/saw/new/how" don't become "nov/sav/nev/hov") AND a preceding w, so elongated
    // chat exclamations ("owwww", "wwww", "ewww") aren't amplified into "owvvv"/"vvvv".
    private static readonly Regex RegexW = new(@"(?<![aeiouw])w", RegexOptions.IgnoreCase);
    private static readonly Regex RegexTh = new("th", RegexOptions.IgnoreCase);
    // Word-final voiced obstruent -> voiceless. The (?![\w']) keeps it at the end of the spoken word.
    private static readonly Regex RegexFinalObstruent = new(@"[dgb](?![\w'])", RegexOptions.IgnoreCase);

    public override void Initialize()
    {
        SubscribeLocalEvent<GermanAccentComponent, AccentGetEvent>(OnAccent);
    }

    public string Accentuate(string message, GermanAccentComponent component)
    {
        var slight = component.Strength == AccentStrength.Slight;
        var chance = slight ? component.SlightChance : 1f;
        var msg = message;

        // Rarely, "the" -> "das". In slight, scale the chance down so it stays an occasional flourish.
        var dasProb = slight ? component.DasProb * component.SlightChance : component.DasProb;
        msg = RegexThe.Replace(msg, m =>
            _random.Prob(dasProb) ? AccentHelpers.MatchCase(m.Value, "das") : m.Value);

        // Word swaps: the full list for thick, a slim iconic-only list for slight.
        msg = _replacement.ApplyReplacements(msg, slight ? "german_slight" : "german");

        // Phonetics, case-preserving. Per-match chance = 1 for thick (always), SlightChance for slight.
        msg = AccentHelpers.ReplaceCasePreserving(msg, RegexV, "f", _random, chance);
        msg = AccentHelpers.ReplaceCasePreserving(msg, RegexW, "v", _random, chance);
        msg = AccentHelpers.ReplaceCasePreserving(msg, RegexTh, "z", _random, chance);

        // Final-obstruent devoicing and umlauts are thick-only (too mangling for the intelligible slight tier).
        if (!slight)
        {
            msg = RegexFinalObstruent.Replace(msg, DevoiceFinal);
            msg = ApplyUmlauts(msg, component.UmlautProb);
        }

        if (!string.IsNullOrWhiteSpace(msg))
        {
            msg = AccentHelpers.FixArticles(msg);

            if (component.Prefixes.Count > 0 && _random.Prob(component.PrefixProb))
                msg = AccentHelpers.PrependPrefix(msg, Loc.GetString(_random.Pick(component.Prefixes)));

            if (component.Suffixes.Count > 0 && _random.Prob(component.SuffixProb))
                msg = AccentHelpers.AppendSuffix(msg, Loc.GetString(_random.Pick(component.Suffixes)));
        }

        return msg;
    }

    private static string DevoiceFinal(Match m)
    {
        var c = m.Value[0];
        var repl = char.ToLowerInvariant(c) switch
        {
            'd' => 't',
            'g' => 'k',
            'b' => 'p',
            _ => c,
        };
        return (char.IsUpper(c) ? char.ToUpperInvariant(repl) : repl).ToString();
    }

    private string ApplyUmlauts(string msg, float prob)
    {
        if (prob <= 0f)
            return msg;

        var sb = new StringBuilder(msg);
        var cooldown = 0;
        for (var i = 0; i < sb.Length; i++)
        {
            if (cooldown == 0)
            {
                if (_random.Prob(prob))
                {
                    sb[i] = sb[i] switch
                    {
                        'A' => 'Ä',
                        'a' => 'ä',
                        'O' => 'Ö',
                        'o' => 'ö',
                        'U' => 'Ü',
                        'u' => 'ü',
                        _ => sb[i],
                    };
                    cooldown = 4;
                }
            }
            else
            {
                cooldown--;
            }
        }

        return sb.ToString();
    }

    private void OnAccent(Entity<GermanAccentComponent> ent, ref AccentGetEvent args)
    {
        args.Message = Accentuate(args.Message, ent.Comp);
    }
}
