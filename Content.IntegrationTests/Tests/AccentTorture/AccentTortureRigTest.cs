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
// [Explicit] so CI never runs this dev rig (it reads a hardcoded local corpus path). Run it manually:
//   dotnet test Content.IntegrationTests --filter "FullyQualifiedName~AccentTortureRig"
[TestFixture]
[Explicit]
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
        Assert.That(File.Exists(CorpusPath), $"Corpus not found at {CorpusPath} (run Tools/Accents/Gen-TortureCorpus.ps1).");
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

            results.Add($"# {accentName} -- {corpus.Length} lines, *Prob tics zeroed");
            results.Add(new string('-', 70));
            foreach (var line in corpus)
                results.Add($"{line}\n  => {Run(line)}");
        });

        Directory.CreateDirectory(OutDir);
        var outFile = Path.Combine(OutDir, $"{accentName}.txt");
        File.WriteAllLines(outFile, results);
        TestContext.Out.WriteLine($"[accent-torture] wrote {outFile}");

        await pair.CleanReturnAsync();
    }

    // Zero every float DataField whose name ends in "Prob" so random tics don't pollute the dump.
    private static void ZeroProbKnobs(IComponent comp)
    {
        foreach (var f in comp.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (f.FieldType == typeof(float) && f.Name.EndsWith("Prob"))
                f.SetValue(comp, 0f);

        foreach (var p in comp.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.CanWrite && p.PropertyType == typeof(float) && p.Name.EndsWith("Prob"))
                p.SetValue(comp, 0f);
    }
}
