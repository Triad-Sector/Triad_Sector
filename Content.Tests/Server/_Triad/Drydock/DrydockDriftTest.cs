#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Content.Server._Triad.Drydock;
using NUnit.Framework;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;

namespace Content.Tests.Server._Triad.Drydock;

/// <summary>
/// The migration table and the drift detector against synthetic mapping files and registries, so
/// every drift case is a plain unit test rather than something that needs content to move.
/// </summary>
[TestFixture, TestOf(typeof(DrydockDrift))]
[Parallelizable(ParallelScope.All)]
public sealed class DrydockDriftTest
{
    internal static MappingDataNode Mapping(string yaml)
    {
        using var reader = new StringReader(yaml);
        return (MappingDataNode) DataNodeParser.ParseYamlStream(reader).Single().Root;
    }

    internal static DrydockMigrationTable Table(params string[] files) =>
        DrydockMigrationTable.FromMappings(files.Select(Mapping));

    private static readonly DrydockFormatWindow Engine = new(3, 7);
    private static readonly DrydockFormatWindow Ours = new(1, 2);

    private static DrydockDriftVerdict Detect(
        DrydockMigrationTable table,
        IEnumerable<string> known,
        params string[] ids)
    {
        var registry = known.ToHashSet();
        return DrydockDrift.Detect(ids, table, registry.Contains, 7, Engine, 2, Ours);
    }

    [Test]
    public void TheTableReadsDeletionsTheWayMapMigrationSystemDoes()
    {
        var table = Table("Gone: null\nAlsoGone: \"\"\nBlank: \" \"\nOld: New\nNested:\n  a: b\n");

        Assert.Multiple(() =>
        {
            Assert.That(table.Deleted, Is.EquivalentTo(new[] { "Gone", "AlsoGone", "Blank" }));
            Assert.That(table.Renamed, Is.EquivalentTo(new Dictionary<string, string> { ["Old"] = "New" }));
        });
    }

    [Test]
    public void ARenameRepeatedAcrossFilesIsRefusedAsTheEngineRefusesIt()
    {
        Assert.Throws<ArgumentException>(() => Table("Old: New\n", "Old: Newer\n"));
    }

    [Test]
    public void ARepeatedDeletionAndARenameThatIsAlsoDeletedAreBothKept()
    {
        var table = Table("Gone: null\nBoth: Target\n", "Gone: null\nBoth: null\n");

        Assert.Multiple(() =>
        {
            Assert.That(table.Deleted, Is.EquivalentTo(new[] { "Gone", "Both" }));
            Assert.That(table.Renamed["Both"], Is.EqualTo("Target"));
        });
    }

    [Test]
    public void ADocumentOfKnownIdsIsClean()
    {
        var verdict = Detect(Table("Old: New\n"), new[] { "A", "B" }, "A", "B");

        Assert.Multiple(() =>
        {
            Assert.That(verdict.IsClean, Is.True);
            Assert.That(verdict.IsRefusal, Is.False);
        });
    }

    [Test]
    public void ARenameToAKnownIdIsDriftButNotARefusal()
    {
        var verdict = Detect(Table("Old: New\n"), new[] { "New" }, "Old");

        Assert.Multiple(() =>
        {
            Assert.That(verdict.Renamed, Is.EqualTo(new[] { new DrydockRename("Old", "New") }));
            Assert.That(verdict.Unresolved, Is.Empty);
            Assert.That(verdict.IsClean, Is.False);
            Assert.That(verdict.IsRefusal, Is.False);
        });
    }

    [Test]
    public void ADeletedIdIsAdvisory()
    {
        // Not in the registry either: the loader skips the registry check for a deleted id.
        var verdict = Detect(Table("Gone: null\n"), Array.Empty<string>(), "Gone");

        Assert.Multiple(() =>
        {
            Assert.That(verdict.Deleted, Is.EqualTo(new[] { "Gone" }));
            Assert.That(verdict.Unresolved, Is.Empty);
            Assert.That(verdict.IsRefusal, Is.False);
            Assert.That(verdict.IsClean, Is.False);
        });
    }

    [Test]
    public void AnIdThatNeverExistedIsARefusal()
    {
        var verdict = Detect(Table("Old: New\n"), new[] { "A" }, "A", "Phantom");

        Assert.Multiple(() =>
        {
            Assert.That(verdict.Unresolved, Is.EqualTo(new[] { "Phantom" }));
            Assert.That(verdict.IsRefusal, Is.True);
        });
    }

    [Test]
    public void ARenameToAMissingIdIsReportedByItsTarget()
    {
        var verdict = Detect(Table("Old: Missing\n"), Array.Empty<string>(), "Old");

        Assert.Multiple(() =>
        {
            Assert.That(verdict.Renamed, Is.EqualTo(new[] { new DrydockRename("Old", "Missing") }));
            Assert.That(verdict.Unresolved, Is.EqualTo(new[] { "Missing" }));
            Assert.That(verdict.IsRefusal, Is.True);
        });
    }

    [Test]
    public void ARenameChainAcrossFilesTakesOneHopAsTheLoaderDoes()
    {
        // A to B in one file, B to C in a later one, with only C still a prototype. The loader renames
        // A to B and stops, then fails on B.
        var table = Table("A: B\n", "B: C\n");
        var verdict = Detect(table, new[] { "C" }, "A");

        Assert.Multiple(() =>
        {
            Assert.That(verdict.Renamed, Is.EqualTo(new[] { new DrydockRename("A", "B") }));
            Assert.That(verdict.Unresolved, Is.EqualTo(new[] { "B" }));
            Assert.That(verdict.IsRefusal, Is.True);
        });
    }

    [Test]
    public void AnIdBothRenamedAndDeletedIsDeletedAndJudgedByItsTarget()
    {
        var table = Table("Both: Target\n", "Both: null\n");

        var targetKnown = Detect(table, new[] { "Target" }, "Both");
        var targetMissing = Detect(table, Array.Empty<string>(), "Both");

        Assert.Multiple(() =>
        {
            Assert.That(targetKnown.Deleted, Is.EqualTo(new[] { "Both" }));
            Assert.That(targetKnown.Renamed, Is.Empty);
            Assert.That(targetKnown.IsRefusal, Is.False);
            Assert.That(targetMissing.Unresolved, Is.EqualTo(new[] { "Target" }));
        });
    }

    [Test]
    public void BlankIdsAreSkipped()
    {
        var verdict = Detect(DrydockMigrationTable.Empty, Array.Empty<string>(), " ", "");
        Assert.That(verdict.IsClean, Is.True);
    }

    [TestCase(2, true)]
    [TestCase(3, false)]
    [TestCase(7, false)]
    [TestCase(8, true)]
    public void TheEngineFormatWindowIsInclusiveAtBothEdges(int format, bool outside)
    {
        var verdict = DrydockDrift.Detect(Array.Empty<string>(), DrydockMigrationTable.Empty, _ => true, format, Engine, 2, Ours);

        Assert.Multiple(() =>
        {
            Assert.That(verdict.EngineFormatOutOfWindow, Is.EqualTo(outside));
            Assert.That(verdict.IsRefusal, Is.EqualTo(outside));
            Assert.That(verdict.DrydockFormatOutOfWindow, Is.False);
        });
    }

    [TestCase(0, true)]
    [TestCase(1, false)]
    [TestCase(2, false)]
    [TestCase(3, true)]
    public void TheDrydockFormatWindowIsInclusiveAtBothEdges(int format, bool outside)
    {
        var verdict = DrydockDrift.Detect(Array.Empty<string>(), DrydockMigrationTable.Empty, _ => true, 7, Engine, format, Ours);

        Assert.Multiple(() =>
        {
            Assert.That(verdict.DrydockFormatOutOfWindow, Is.EqualTo(outside));
            Assert.That(verdict.IsRefusal, Is.EqualTo(outside));
            Assert.That(verdict.EngineFormatOutOfWindow, Is.False);
        });
    }

    [Test]
    public void TheRealWindowsComeFromTheirReaders()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DrydockDrift.EngineWindow, Is.EqualTo(new DrydockFormatWindow(
                EntityDeserializer.OldestSupportedVersion, EntityDeserializer.NewestSupportedVersion)));
            // What the serializer writes today has to be readable, or every fresh store is a refusal.
            Assert.That(DrydockDrift.EngineWindow.Contains(EntitySerializer.MapFormatVersion), Is.True);
            Assert.That(DrydockDrift.DrydockWindow, Is.EqualTo(new DrydockFormatWindow(
                DrydockFormat.MinimumSupported, DrydockFormat.Current)));
        });
    }

    [Test]
    public void DetectReadsTheIdsTheStreamingReaderReturns()
    {
        const string yaml = "meta:\n  format: 7\nentities:\n- proto: \"\"\n  entities:\n  - uid: 1\n- proto: Old\n  entities:\n  - uid: 2\n- proto: Phantom\n  entities:\n  - uid: 3\n";
        var (ids, format) = DrydockSystem.ReadDriftIds(yaml);

        var verdict = DrydockDrift.Detect(ids, Table("Old: New\n"), new HashSet<string> { "New" }.Contains, format, Engine, 2, Ours);

        Assert.Multiple(() =>
        {
            Assert.That(ids, Is.EqualTo(new[] { "Old", "Phantom" }));
            Assert.That(verdict.Renamed, Is.EqualTo(new[] { new DrydockRename("Old", "New") }));
            Assert.That(verdict.Unresolved, Is.EqualTo(new[] { "Phantom" }));
            Assert.That(verdict.EngineFormatOutOfWindow, Is.False);
        });
    }
}
