// Triad: renamed from FrenchAccent to keep real-world nations vague in the post-corporate setting.
// Enriched onto the shared AccentHelpers (a/an fixup + data-driven prefix/suffix tics) on top of the
// original phonetics: th -> 'z, word-initial h -> ', and French-style spacing before ! ? : ;.
using System.Text.RegularExpressions;
using Content.Server.Speech.Components;
using Robust.Shared.Random;

namespace Content.Server.Speech.EntitySystems;

/// <summary>
/// System that gives the speaker a faux-Gallic accent.
/// </summary>
public sealed class TerraGallicAccentSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;

    private static readonly Regex RegexTh = new("th", RegexOptions.IgnoreCase);
    private static readonly Regex RegexStartH = new(@"(?<!\w)h", RegexOptions.IgnoreCase);
    private static readonly Regex RegexSpacePunctuation = new(@"(?<=\w\w)[!?;:](?!\w)", RegexOptions.IgnoreCase);

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TerraGallicAccentComponent, AccentGetEvent>(OnAccentGet);
    }

    public string Accentuate(string message, TerraGallicAccentComponent component)
    {
        var msg = _replacement.ApplyReplacements(message, "terragallic");

        // Phonetics: th -> 'z, then word-initial h -> '.
        msg = RegexTh.Replace(msg, "'z");
        msg = RegexStartH.Replace(msg, "'");

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

    private void OnAccentGet(EntityUid uid, TerraGallicAccentComponent component, AccentGetEvent args)
    {
        args.Message = Accentuate(args.Message, component);
    }
}
