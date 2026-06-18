// Triad: enriched French accent (TF2-Spy / Clouseau-thick). Identifiers kept upstream-named for clean
// cherry-picking. Phonetics ride the shared case-preserving helper. Distinct from German on purpose:
// French DROPS word-initial h (German keeps it) and never does German's w->v / final devoicing.
//   th -> z   (the->ze, this->zis, with->wiz; French has no /th/, realizes it as /z/)
//   word-initial h dropped -> ' (have->'ave, Hello->'Ello; case promoted onto the next letter)
//   j -> zh   (just->zhust, major->mazhor; the French /ʒ/)
//   + French-style space before ! ? : ; (typographic tic German lacks)
using System.Text.RegularExpressions;
using Content.Server.Speech.Components;
using Robust.Shared.Random;

namespace Content.Server.Speech.EntitySystems;

/// <summary>
/// System that gives the speaker a faux-Gallic accent.
/// </summary>
public sealed class FrenchAccentSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;

    private static readonly Regex RegexTh = new("th", RegexOptions.IgnoreCase);
    private static readonly Regex RegexJ = new("j", RegexOptions.IgnoreCase);
    private static readonly Regex RegexSpacePunctuation = new(@"(?<=\w\w)[!?;:](?!\w)", RegexOptions.IgnoreCase);

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<FrenchAccentComponent, AccentGetEvent>(OnAccentGet);
    }

    public string Accentuate(string message, FrenchAccentComponent component)
    {
        // j -> zh runs on the raw English BEFORE the word swaps, so a French loanword swap that itself
        // contains a j ("hello" -> "bonjour") is not re-mangled into "bonzhour". English "just" -> "zhust".
        var msg = AccentHelpers.ReplaceCasePreserving(message, RegexJ, "zh");

        msg = _replacement.ApplyReplacements(msg, "french");

        // Phonetics: th -> z (case-preserving), then drop word-initial h (shared case-aware helper).
        msg = AccentHelpers.ReplaceCasePreserving(msg, RegexTh, "z");
        msg = AccentHelpers.DropInitialH(msg);

        if (!string.IsNullOrWhiteSpace(msg))
        {
            msg = AccentHelpers.FixArticles(msg);

            if (component.Prefixes.Count > 0 && _random.Prob(component.PrefixProb))
                msg = AccentHelpers.PrependPrefix(msg, Loc.GetString(_random.Pick(component.Prefixes)));

            if (component.Suffixes.Count > 0 && _random.Prob(component.SuffixProb))
                msg = AccentHelpers.AppendSuffix(msg, Loc.GetString(_random.Pick(component.Suffixes)));
        }

        // French-style spacing before ! ? : ; runs last (keys off the sentence's final punctuation).
        msg = RegexSpacePunctuation.Replace(msg, " $&");

        return msg;
    }

    private void OnAccentGet(EntityUid uid, FrenchAccentComponent component, AccentGetEvent args)
    {
        args.Message = Accentuate(args.Message, component);
    }
}
