// Triad: renamed from GermanAccent to keep real-world nations vague in the post-corporate setting.
// Enriched onto the shared AccentHelpers (a/an fixup + data-driven prefix/suffix tics) on top of the
// original phonetics: probabilistic the->das, th->zh, and the random-umlaut pass. The das/umlaut
// chances are now component DataFields instead of hardcoded.
using System.Text;
using System.Text.RegularExpressions;
using Content.Server.Speech.Components;
using Robust.Shared.Random;

namespace Content.Server.Speech.EntitySystems;

public sealed class TerraGermanicAccentSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;

    private static readonly Regex RegexTh = new(@"(?<=\s|^)th", RegexOptions.IgnoreCase);
    private static readonly Regex RegexThe = new(@"(?<=\s|^)the(?=\s|$)", RegexOptions.IgnoreCase);

    public override void Initialize()
    {
        SubscribeLocalEvent<TerraGermanicAccentComponent, AccentGetEvent>(OnAccent);
    }

    public string Accentuate(string message, TerraGermanicAccentComponent component)
    {
        var msg = message;

        // Rarely, "the" becomes "das" instead of "ze". Shift T, H, E over to D, A, S to preserve caps.
        foreach (Match match in RegexThe.Matches(msg))
        {
            if (_random.Prob(component.DasProb))
            {
                msg = msg.Substring(0, match.Index) +
                      (char)(msg[match.Index] - 16) +
                      (char)(msg[match.Index + 1] - 7) +
                      (char)(msg[match.Index + 2] + 14) +
                      msg.Substring(match.Index + 3);
            }
        }

        // Word replacements.
        msg = _replacement.ApplyReplacements(msg, "terragermanic");

        // th -> zh (zhis, zhat...); "the -> ze" is already handled by the replacements.
        var msgBuilder = new StringBuilder(msg);
        foreach (Match match in RegexTh.Matches(msg))
        {
            // Shift T over to Z to preserve capitalization.
            msgBuilder[match.Index] = (char)(msgBuilder[match.Index] + 6);
        }

        // Random umlaut time. The joke outweighs the emotional damage this inflicts on actual Germans.
        var umlautCooldown = 0;
        for (var i = 0; i < msgBuilder.Length; i++)
        {
            if (umlautCooldown == 0)
            {
                if (_random.Prob(component.UmlautProb))
                {
                    msgBuilder[i] = msgBuilder[i] switch
                    {
                        'A' => 'Ä',
                        'a' => 'ä',
                        'O' => 'Ö',
                        'o' => 'ö',
                        'U' => 'Ü',
                        'u' => 'ü',
                        _ => msgBuilder[i]
                    };
                    umlautCooldown = 4;
                }
            }
            else
            {
                umlautCooldown--;
            }
        }

        msg = msgBuilder.ToString();

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

    private void OnAccent(Entity<TerraGermanicAccentComponent> ent, ref AccentGetEvent args)
    {
        args.Message = Accentuate(args.Message, ent.Comp);
    }
}
