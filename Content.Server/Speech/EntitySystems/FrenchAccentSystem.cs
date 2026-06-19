// Triad: enriched French accent (TF2-Spy / Clouseau-thick). Identifiers kept upstream-named for clean
// cherry-picking. Phonetics ride the shared case-preserving helper. Distinct from German on purpose:
// French DROPS word-initial h (German keeps it) and never does German's w->v / final devoicing.
//   th -> z   (the->ze, this->zis, with->wiz; French has no /th/, realizes it as /z/)
//   word-initial h dropped -> ' (have->'ave, Hello->'Ello; case promoted onto the next letter)
//   j -> zh   (just->zhust, major->mazhor; the French /ʒ/)
//   + French-style space before ! ? : ; (typographic tic German lacks)
using System.Text.RegularExpressions;
using Content.Server.Speech;
using Content.Server.Speech.Components;
using Robust.Shared.Random;
using Content.Server._Triad.Speech; // Triad: AccentStrength relocated to _Triad
using Content.Server._Triad.Speech.EntitySystems; // Triad: AccentHelpers relocated to _Triad

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
        var slight = component.Strength == AccentStrength.Slight;
        var chance = slight ? component.SlightChance : 1f;

        // j -> zh on raw English BEFORE swaps. Thick-only: zhust-for-just hurts slight.
        var msg = slight
            ? message
            : AccentHelpers.ReplaceCasePreserving(message, RegexJ, "zh");

        msg = _replacement.ApplyReplacements(msg, slight ? "french_slight" : "french");

        // th -> z, per-match chance (1 for thick). Kept in slight: "ze" is iconic and recognizable.
        msg = AccentHelpers.ReplaceCasePreserving(msg, RegexTh, "z", _random, chance);

        // Word-initial h-drop is thick-only (less recognizable than th->z).
        if (!slight)
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
