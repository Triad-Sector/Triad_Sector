using Content.Server._NF.Speech.Components;
using Robust.Shared.Random;
using Content.Server.Speech;
using Content.Server.Speech.EntitySystems;
using System.Linq;
using Content.Server.Chat.Systems;

namespace Content.Server._NF.Speech.EntitySystems;

public sealed class CavemanAccentSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ReplacementAccentSystem _replacement = default!;
    [Dependency] private readonly ChatSystem _chat = default!;

    public readonly string[] PunctuationStringsToRemove = { "'", "\"", ".", ",", "!", "?", ";", ":" }; // Leave hyphens

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CavemanAccentComponent, AccentGetEvent>(OnAccentGet);
    }

    private string Convert(string message, CavemanAccentComponent component)
    {
        string msg = _replacement.ApplyReplacements(message, "caveman");

        // Triad: resolve the filler words a caveman drops (the, is, to, a, am, are, was, do...).
        var forbidden = new HashSet<string>(CavemanAccentComponent.ForbiddenWords.Select(Loc.GetString), StringComparer.OrdinalIgnoreCase);

        string[] words = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<string> modifiedWords = new List<string>();

        foreach (var word in words)
        {
            string endPunctuation = "";
            int actualLength = word.Length;

            for (int letterIndex = word.Length - 1; letterIndex >= 0; letterIndex--)
            {
                if (word[letterIndex] != '-' && char.IsPunctuation(word[letterIndex]))
                {
                    endPunctuation = word[letterIndex] + endPunctuation;
                    actualLength = letterIndex; // Length of word = index of first punctuation
                }
                else
                {
                    break;
                }
            }

            var modifiedWord = word;

            if (actualLength > CavemanAccentComponent.MaxWordLength)
            {
                modifiedWord = GetGrunt();
                CapitalizeReplacement(word, ref modifiedWord);
                modifiedWord += endPunctuation;

                modifiedWords.Add(modifiedWord);

                continue;
            }

            modifiedWord = TryRemovePunctuation(modifiedWord);

            // Triad: drop caveman filler words so speech reads "me go store", not "me is go to the store".
            // Carry any trailing punctuation onto the previous word, like the all-punctuation case below.
            if (forbidden.Contains(modifiedWord))
            {
                if (modifiedWords.Count > 0)
                    modifiedWords[^1] += endPunctuation;
                continue;
            }

            modifiedWord = TryConvertNumbers(modifiedWord);

            // If it's all punctuation, append the punctuation to the last word if it exists, otherwise add a grunt.
            if (modifiedWord.Length <= 0)
            {
                if (modifiedWords.Count > 0)
                {
                    modifiedWords[^1] += endPunctuation;
                    continue;
                }
                else
                {
                    modifiedWord = GetGrunt();
                }
            }

            modifiedWord += endPunctuation;

            modifiedWords.Add(modifiedWord);
        }

        if (modifiedWords.Count == 0)
        {
            modifiedWords.Add(GetGrunt());
        }

        return _chat.SanitizeMessageCapital(string.Join(' ', modifiedWords));
    }

    private void OnAccentGet(EntityUid uid, CavemanAccentComponent component, AccentGetEvent args)
    {
        args.Message = Convert(args.Message, component);
    }

    private string GetGrunt()
    {
        var grunt = Loc.GetString(_random.Pick(CavemanAccentComponent.Grunts));

        if (_random.Prob(0.25f)) // Triad: was 0.5 -- double-grunts added a lot of noise
        {
            grunt += "-";
            grunt += Loc.GetString(_random.Pick(CavemanAccentComponent.Grunts));
        }
        return grunt;
    }

    private void CapitalizeReplacement(string input, ref string replacement)
    {
        if (!input.Any(char.IsLower) && (input.Length > 1 || replacement.Length == 1))
        {
            replacement = replacement.ToUpperInvariant();
        }
        else if (input.Length >= 1 && replacement.Length >= 1 && char.IsUpper(input[0]))
        {
            replacement = replacement[0].ToString().ToUpper() + replacement[1..];
        }
    }

    private string TryRemovePunctuation(string word)
    {
        foreach (var punctStr in PunctuationStringsToRemove)
        {
            word = word.Replace(punctStr, "");
        }
        return word;
    }

    private string TryConvertNumbers(string word)
    {
        int num;

        if (int.TryParse(word, out num))
        {
            num = int.Max(0, num); //Negatives treated as zero.
            if (num < CavemanAccentComponent.Numbers.Count)
            {
                return Loc.GetString(CavemanAccentComponent.Numbers[num]);
            }
            else
            {
                return Loc.GetString(CavemanAccentComponent.LargeNumberString);
            }
        }

        return word;
    }

}
