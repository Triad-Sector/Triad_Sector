using System.Text.RegularExpressions;
using Content.Server.Speech.Components;
using Robust.Shared.Random;
using Content.Server._Triad.Speech.EntitySystems; // Triad: AccentHelpers relocated to _Triad

namespace Content.Server.Speech.EntitySystems;

public sealed class BoganAccentSystem : EntitySystem
{
    // Non-rhotic Aussie -er -> -a (water -> wata, better -> betta). Two word chars before "er" keeps it
    // off short words like "her"/"per". Shorter "-a" than the cockney "-ah" keeps bogan distinct.
    private static readonly Regex RegexEr = new(@"(?<=\w\w)er\b", RegexOptions.IgnoreCase);

    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<BoganAccentComponent, AccentGetEvent>(OnAccent);
    }

    private void OnAccent(EntityUid uid, BoganAccentComponent component, AccentGetEvent args)
    {
        var msg = _replacement.ApplyReplacements(args.Message, "bogan");

        // Triad: bogans drop the g too ("havin' a chat"); shared keep-list spares king/ring/wing.
        msg = AccentHelpers.DropG(msg);
        msg = AccentHelpers.ReplaceCasePreserving(msg, RegexEr, "a"); // water -> wata, over -> ova

        if (string.IsNullOrWhiteSpace(msg))
        {
            args.Message = msg;
            return;
        }

        // Triad: re-agree a/an after swaps, then data-driven prob prefix/suffix tics with the shared
        // caps-aware placement (replaces the old hardcoded probs + _random.Next index math).
        msg = AccentHelpers.FixArticles(msg);

        if (component.Prefixes.Count > 0 && _random.Prob(component.PrefixProb))
            msg = AccentHelpers.PrependPrefix(msg, Loc.GetString(_random.Pick(component.Prefixes)));

        if (component.Suffixes.Count > 0 && _random.Prob(component.SuffixProb))
            msg = AccentHelpers.AppendSuffix(msg, Loc.GetString(_random.Pick(component.Suffixes)));

        args.Message = msg;
    }
}
