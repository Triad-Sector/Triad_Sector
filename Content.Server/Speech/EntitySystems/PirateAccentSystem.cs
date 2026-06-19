using System.Text.RegularExpressions;
using Content.Server.Speech.Components;
using Robust.Shared.Random;
using Content.Server._Triad.Speech.EntitySystems; // Triad: AccentHelpers relocated to _Triad

namespace Content.Server.Speech.EntitySystems;

// Triad: enriched the Pirate accent (display name "Freeport Corsair") onto the shared AccentHelpers --
// word swaps + the salty g-drop (sailin', fightin'), "Arrr" prefix interjections, and pirate suffix tics.
// Replaces the upstream prefix-only "Yarr" implementation. Kept the PirateAccent name for portability.
public sealed class PirateAccentSystem : EntitySystem
{
    private static readonly Regex RegexOf = new(@"\b(o)f\b", RegexOptions.IgnoreCase); // of -> o'

    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PirateAccentComponent, AccentGetEvent>(OnAccentGet);
    }

    public string Accentuate(string message, PirateAccentComponent component)
    {
        var msg = _replacement.ApplyReplacements(message, "pirate");

        // Salty g-drop: sailing -> sailin', fighting -> fightin' (keep-list spares king/ring).
        msg = AccentHelpers.DropG(msg);
        msg = RegexOf.Replace(msg, "$1'"); // cup of grog -> cup o' grog

        if (string.IsNullOrWhiteSpace(msg))
            return msg;

        msg = AccentHelpers.FixArticles(msg);

        if (component.Prefixes.Count > 0 && _random.Prob(component.PrefixProb))
            msg = AccentHelpers.PrependPrefix(msg, Loc.GetString(_random.Pick(component.Prefixes)));

        if (component.Suffixes.Count > 0 && _random.Prob(component.SuffixProb))
            msg = AccentHelpers.AppendSuffix(msg, Loc.GetString(_random.Pick(component.Suffixes)));

        return msg;
    }

    private void OnAccentGet(EntityUid uid, PirateAccentComponent component, AccentGetEvent args)
    {
        args.Message = Accentuate(args.Message, component);
    }
}
