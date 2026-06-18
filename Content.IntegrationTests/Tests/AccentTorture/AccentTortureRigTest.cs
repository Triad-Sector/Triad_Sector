/*
 * Triad - This file is licensed under AGPLv3
 * Copyright (c) 2025 Triad Contributors
 * See AGPLv3.txt for details.
 */

using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Content.Server.Speech;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests.AccentTorture;

// Triad: NOT a CI regression test -- a manual torture rig for accent-enrichment iteration. Marked
// [Explicit] so it only runs when named directly:
//
//   dotnet test Content.IntegrationTests --filter "FullyQualifiedName~AccentTortureRig&TestCategory=German"
//
// It feeds the ~1000-line corpus (Tools/Accents/Gen-TortureCorpus.ps1 output) through one accent on a
// real pooled server -- real ReplacementAccentSystem, real prototypes, real loc -- and dumps every
// "input => output" pair to %TEMP%/accent-torture/<accent>.txt for eyeballing. All *Prob tic knobs are
// zeroed so the deterministic phonetic + word-swap core is isolated from random prefix/suffix noise.
// Dev rig (not CI regression): dumps the full corpus through one accent to %TEMP%/accent-torture for
// eyeballing. Auto-skips when the local corpus path is absent (e.g. on CI), so it stays harmless there
// without needing [Explicit]. Run locally with:
//   dotnet test Content.IntegrationTests --filter "FullyQualifiedName~AccentTortureRig"
[TestFixture]
[Category("AccentTorture")]
public sealed class AccentTortureRigTest
{
    // Registered component names (class name minus the "Component" suffix), same strings used in YAML.
    private static readonly (string Name, string Category)[] Accents =
    {
        ("GermanAccent", "German"),
        ("FrenchAccent", "French"),
        ("RussianAccent", "Russian"),
        ("SpanishAccent", "Spanish"),
        ("PirateAccent", "Pirate"),
        ("SouthernAccent", "Southern"),
        ("CowboyAccent", "Cowboy"),
        ("DwarfAccent", "Dwarf"),
        ("BoganAccent", "Bogan"),
        ("NewBrooklynAccent", "NewBrooklyn"),
        ("GoblinAccent", "Goblin"),
        ("StreetpunkAccent", "Streetpunk"),
        ("CavemanAccent", "Caveman"),
        ("LizardAccent", "Lizard"),
        ("MothAccent", "Moth"),
    };

    private static string CorpusPath =>
        System.Environment.GetEnvironmentVariable("ACCENT_CORPUS")
        ?? @"C:\src\Triad_Sector\Content.IntegrationTests\Tests\AccentTorture\corpus.txt";

    private static string OutDir =>
        Path.Combine(Path.GetTempPath(), "accent-torture");

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (var (name, category) in Accents)
            yield return new TestCaseData(name).SetName($"AccentTortureRig({category})").SetCategory(category);
    }

    [TestCaseSource(nameof(Cases))]
    public async Task Torture(string accentName)
    {
        if (!File.Exists(CorpusPath))
            Assert.Ignore($"Corpus not present at {CorpusPath}; skipping dev-only torture rig.");
        var corpus = File.ReadAllLines(CorpusPath);

        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var factory = server.ResolveDependency<IComponentFactory>();

        var results = new List<string>(corpus.Length + 4);

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(null, MapCoordinates.Nullspace);

            var comp = factory.GetComponent(accentName);
            ZeroProbKnobs(comp);
            entMan.AddComponent(uid, comp);

            string Run(string s)
            {
                var ev = new AccentGetEvent(uid, s);
                entMan.EventBus.RaiseLocalEvent(uid, ev);
                return ev.Message;
            }

            var pairs = new List<(string, string)>(corpus.Length);
            foreach (var line in corpus)
                pairs.Add((line, Run(line)));

            var (wordPct, editRatio) = Thickness(pairs);
            results.Add($"# {accentName} -- {corpus.Length} lines, *Prob tics zeroed");
            results.Add($"# thickness: {wordPct:P1} words changed, edit-ratio {editRatio:F3}");
            results.Add(new string('-', 70));
            foreach (var (inp, outp) in pairs)
                results.Add($"{inp}\n  => {outp}");
        });

        Directory.CreateDirectory(OutDir);
        var outFile = Path.Combine(OutDir, $"{accentName}.txt");
        File.WriteAllLines(outFile, results);
        TestContext.Out.WriteLine($"[accent-torture] wrote {outFile}");

        await pair.CleanReturnAsync();
    }

    // Zero every float DataField whose name ends in "Prob" or "Chance" (PrefixProb, DasProb, ackChance,
    // flutterChance...) so random tics don't pollute the deterministic-core dump.
    // Zero tic/random knobs for a deterministic core, but KEEP SlightChance: it is the slight tier's
    // phonetic dial, and zeroing it would suppress the very transforms the thickness metric measures.
    private static bool IsRngKnob(string name) =>
        name != "SlightChance" && (name.EndsWith("Prob") || name.EndsWith("Chance"));

    private static void ZeroProbKnobs(IComponent comp)
    {
        foreach (var f in comp.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (f.FieldType == typeof(float) && IsRngKnob(f.Name))
                f.SetValue(comp, 0f);

        foreach (var p in comp.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.CanWrite && p.PropertyType == typeof(float) && IsRngKnob(p.Name))
                p.SetValue(comp, 0f);
    }

    // Fraction of whitespace-split tokens that changed, plus mean normalized Levenshtein distance.
    private static (double WordPct, double EditRatio) Thickness(IReadOnlyList<(string In, string Out)> pairs)
    {
        long wordsTotal = 0, wordsChanged = 0;
        double editSum = 0;
        foreach (var (inp, outp) in pairs)
        {
            var a = inp.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var b = outp.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var n = System.Math.Max(a.Length, b.Length);
            for (var i = 0; i < n; i++)
            {
                wordsTotal++;
                var wa = i < a.Length ? a[i] : "";
                var wb = i < b.Length ? b[i] : "";
                if (wa != wb)
                    wordsChanged++;
            }
            var maxLen = System.Math.Max(inp.Length, outp.Length);
            editSum += maxLen == 0 ? 0 : (double)Levenshtein(inp, outp) / maxLen;
        }
        return (wordsTotal == 0 ? 0 : (double)wordsChanged / wordsTotal, pairs.Count == 0 ? 0 : editSum / pairs.Count);
    }

    private static int Levenshtein(string s, string t)
    {
        var d = new int[t.Length + 1];
        for (var j = 0; j <= t.Length; j++) d[j] = j;
        for (var i = 1; i <= s.Length; i++)
        {
            var prev = d[0];
            d[0] = i;
            for (var j = 1; j <= t.Length; j++)
            {
                var cur = d[j];
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[j] = System.Math.Min(System.Math.Min(d[j] + 1, d[j - 1] + 1), prev + cost);
                prev = cur;
            }
        }
        return d[t.Length];
    }
}
