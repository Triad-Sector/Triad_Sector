using Content.Server._NF.Speech.Components;
using Content.Server.Speech;
using Content.Server.Speech.EntitySystems;
using Robust.Shared.Random;
using System.Text.RegularExpressions;
using Content.Server._Triad.Speech.EntitySystems; // Triad: AccentHelpers relocated to _Triad

namespace Content.Server._NF.Speech.EntitySystems;

public sealed class StreetpunkAccentSystem : EntitySystem
{
    private static readonly Regex RegexAnd = new(@"\b(an)d\b", RegexOptions.IgnoreCase); // case-preserving via $1
    private static readonly Regex RegexDve = new("d've");

    [Dependency] private readonly IRobustRandom _random = default!; // Triad: tic pools
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<StreetpunkAccentComponent, AccentGetEvent>(OnAccentGet);
    }

    // converts left word when typed into the right word. For example typing you becomes ye.
    public string Accentuate(string message, StreetpunkAccentComponent component)
    {
        // Triad: word swaps FIRST (so keys match the real word), then phonetics, then tics -- matches the
        // shared accent pipeline. Was phonetics-first, which silently killed any -ing/and word-swap key.
        var msg = _replacement.ApplyReplacements(message, "streetpunk");

        //They shoulda started runnin' an' hidin' from me! <- bit from SouthernDrawl Accent
        msg = AccentHelpers.DropG(msg); // Triad: keep-list spares king/ring/wing (bare ing\b mangled them)
        msg = RegexAnd.Replace(msg, "$1'"); // and -> an', And -> An', AND -> AN'
        msg = RegexDve.Replace(msg, "da");

        if (string.IsNullOrWhiteSpace(msg))
            return msg;

        // Triad: a/an re-agreement + data-driven cyberpunk tic pools via the shared helpers.
        msg = AccentHelpers.FixArticles(msg);

        if (component.Prefixes.Count > 0 && _random.Prob(component.PrefixProb))
            msg = AccentHelpers.PrependPrefix(msg, Loc.GetString(_random.Pick(component.Prefixes)));

        if (component.Suffixes.Count > 0 && _random.Prob(component.SuffixProb))
            msg = AccentHelpers.AppendSuffix(msg, Loc.GetString(_random.Pick(component.Suffixes)));

        return msg;
    }

    private void OnAccentGet(EntityUid uid, StreetpunkAccentComponent component, AccentGetEvent args)
    {
        args.Message = Accentuate(args.Message, component);
    }
}
