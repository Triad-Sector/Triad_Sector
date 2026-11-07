#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server._Triad.Drydock;
using NUnit.Framework;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Tests.Server._Triad.Drydock;

/// <summary>
/// Tier 1 re-bake on synthetic documents. Fixtures go through the engine's emitter first, because
/// every stored document did, and the transform's byte-preservation claim is about documents of
/// that shape.
/// </summary>
[TestFixture, TestOf(typeof(DrydockDocumentRebake))]
[Parallelizable(ParallelScope.All)]
public sealed class DrydockDocumentRebakeTest
{
    private static string Canon(string yaml) => DrydockSystem.EmitDocument(DrydockDriftTest.Mapping(yaml));

    private const string Header =
        "meta:\n  format: 7\n  category: Grid\n  engineVersion: 289.0.2\n  forkId: \"\"\n  forkVersion: \"\"\n  time: 09/13/2026 12:00:00\n  entityCount: 5\n"
        + "maps: []\ngrids:\n- 1\norphans:\n- 1\nnullspace: []\ntilemap:\n  0: Space\n  1: Plating\n";

    private const string GridGroup =
        "- proto: \"\"\n  entities:\n  - uid: 1\n    components:\n    - type: MetaData\n      name: grid\n    - type: Transform\n      pos: 0,0\n      parent: invalid\n";

    private static string Group(string proto, params int[] uids)
    {
        var text = $"- proto: {proto}\n  entities:\n";
        foreach (var uid in uids)
        {
            text += $"  - uid: {uid}\n    components:\n    - type: Transform\n      pos: {uid}.5,0.5\n      parent: 1\n";
        }

        return text;
    }

    private static string Doc(params string[] groups) => Canon(Header + "entities:\n" + GridGroup + string.Concat(groups));

    [Test]
    public void TheEmitterReproducesADocumentItWrote()
    {
        var once = Doc(Group("Keep", 3), Group("Old", 2, 5));
        Assert.That(Canon(once), Is.EqualTo(once));
    }

    [Test]
    public void ADocumentWithNothingToDoComesBackAsTheSameString()
    {
        var doc = Doc(Group("Keep", 3), Group("Unrelated", 2));
        var table = DrydockDriftTest.Table("Old: New\nGone: null\n");

        var result = DrydockDocumentRebake.Transform(doc, table, DrydockFormat.Current);

        Assert.Multiple(() =>
        {
            Assert.That(result.Yaml, Is.SameAs(doc));
            Assert.That(result.Changed, Is.False);
            Assert.That(result.AppliedRenames, Is.Empty);
            Assert.That(result.AppliedSteps, Is.Empty);
            Assert.That(result.DrydockFormatVer, Is.EqualTo(DrydockFormat.Current));
        });
    }

    [Test]
    public void ARenameRewritesTheGroupAndKeepsEverythingElse()
    {
        var doc = Doc(Group("Keep", 3), Group("Old", 2, 5));
        var result = DrydockDocumentRebake.Transform(doc, DrydockDriftTest.Table("Old: Zeta\n"), DrydockFormat.Current);

        var expected = Doc(Group("Keep", 3), Group("Zeta", 2, 5));

        Assert.Multiple(() =>
        {
            Assert.That(result.Yaml, Is.EqualTo(expected));
            Assert.That(DrydockDriftTest.Mapping(result.Yaml), Is.EqualTo(DrydockDriftTest.Mapping(expected)));
            Assert.That(result.Changed, Is.True);
            Assert.That(result.AppliedRenames, Is.EqualTo(new[] { new DrydockRename("Old", "Zeta") }));
            Assert.That(DrydockSystem.ReadDriftIds(result.Yaml).Ids, Is.EqualTo(new[] { "Keep", "Zeta" }));
        });
    }

    [TestCase("\n")]
    [TestCase("\r\n")]
    public void ARewrittenDocumentKeepsItsOwnLineEndings(string newLine)
    {
        var doc = Doc(Group("Keep", 3), Group("Old", 2)).ReplaceLineEndings(newLine);
        var result = DrydockDocumentRebake.Transform(doc, DrydockDriftTest.Table("Old: Zeta\n"), DrydockFormat.Current);

        Assert.That(result.Yaml, Is.EqualTo(Doc(Group("Keep", 3), Group("Zeta", 2)).ReplaceLineEndings(newLine)));
    }

    [Test]
    public void ARenameIntoAnExistingGroupMergesAndSortsLikeTheSerializer()
    {
        var doc = Doc(Group("Keep", 3), Group("Old", 2, 6), Group("Target", 4));
        var result = DrydockDocumentRebake.Transform(doc, DrydockDriftTest.Table("Old: Target\n"), DrydockFormat.Current);

        var expected = Doc(Group("Keep", 3), Group("Target", 2, 4, 6));

        Assert.Multiple(() =>
        {
            Assert.That(result.Yaml, Is.EqualTo(expected));
            Assert.That(result.AppliedRenames, Is.EqualTo(new[] { new DrydockRename("Old", "Target") }));
        });
    }

    [Test]
    public void ARenameLandsInTheSerializersGroupOrder()
    {
        var doc = Doc(Group("Keep", 3), Group("Zulu", 2));
        var result = DrydockDocumentRebake.Transform(doc, DrydockDriftTest.Table("Zulu: Alpha\n"), DrydockFormat.Current);

        Assert.That(result.Yaml, Is.EqualTo(Doc(Group("Alpha", 2), Group("Keep", 3))));
    }

    [Test]
    public void DeletedIdsAreLeftForTheLoader()
    {
        var doc = Doc(Group("Gone", 2), Group("Keep", 3));
        var result = DrydockDocumentRebake.Transform(doc, DrydockDriftTest.Table("Gone: null\n"), DrydockFormat.Current);

        Assert.Multiple(() =>
        {
            Assert.That(result.Yaml, Is.SameAs(doc));
            Assert.That(result.Changed, Is.False);
        });
    }

    [Test]
    public void AnIdBothRenamedAndDeletedIsNotRenamed()
    {
        var doc = Doc(Group("Both", 2));
        var result = DrydockDocumentRebake.Transform(doc, DrydockDriftTest.Table("Both: Target\n", "Both: null\n"), DrydockFormat.Current);

        Assert.That(result.Yaml, Is.SameAs(doc));
    }

    [Test]
    public void AnIdTheTableDoesNotMentionIsLeftAlone()
    {
        // The transform has no registry: an id that never existed is the detector's business.
        var doc = Doc(Group("Phantom", 2));
        var result = DrydockDocumentRebake.Transform(doc, DrydockDriftTest.Table("Old: New\n"), DrydockFormat.Current);

        Assert.That(result.Yaml, Is.SameAs(doc));
    }

    [Test]
    public void AChainTakesOneHopPerPassAsTheLoaderDoes()
    {
        var table = DrydockDriftTest.Table("A: B\n", "B: C\n");
        var doc = Doc(Group("A", 2));

        var first = DrydockDocumentRebake.Transform(doc, table, DrydockFormat.Current);
        var second = DrydockDocumentRebake.Transform(first.Yaml, table, DrydockFormat.Current);

        Assert.Multiple(() =>
        {
            Assert.That(first.Yaml, Is.EqualTo(Doc(Group("B", 2))));
            Assert.That(second.Yaml, Is.EqualTo(Doc(Group("C", 2))));
        });
    }

    [Test]
    public void AFormatStepRunsFromTheRevisionsVersionAndAdvancesIt()
    {
        var doc = Doc(Group("Keep", 3));
        var steps = new[]
        {
            new DrydockDocumentRebake.FormatStep(DrydockFormat.Current - 1, "mark", root =>
            {
                root["stepped"] = new ValueDataNode("yes");
                return true;
            }),
        };

        var behind = DrydockDocumentRebake.Transform(doc, DrydockMigrationTable.Empty, DrydockFormat.Current - 1, steps, Array.Empty<DrydockDocumentRebake.Repair>());
        var current = DrydockDocumentRebake.Transform(doc, DrydockMigrationTable.Empty, DrydockFormat.Current, steps, Array.Empty<DrydockDocumentRebake.Repair>());

        Assert.Multiple(() =>
        {
            Assert.That(behind.Yaml, Does.Contain("stepped: yes"));
            Assert.That(behind.DrydockFormatVer, Is.EqualTo(DrydockFormat.Current));
            Assert.That(behind.AppliedSteps, Is.EqualTo(new[] { "mark" }));
            Assert.That(current.Yaml, Is.SameAs(doc));
            Assert.That(current.DrydockFormatVer, Is.EqualTo(DrydockFormat.Current));
        });
    }

    [Test]
    public void AFormatStepThatChangesNothingStillAdvancesTheVersion()
    {
        var doc = Doc(Group("Keep", 3));
        var steps = new[] { new DrydockDocumentRebake.FormatStep(DrydockFormat.Current - 1, "noop", _ => false) };

        var result = DrydockDocumentRebake.Transform(doc, DrydockMigrationTable.Empty, DrydockFormat.Current - 1, steps, Array.Empty<DrydockDocumentRebake.Repair>());

        Assert.Multiple(() =>
        {
            Assert.That(result.Yaml, Is.SameAs(doc));
            Assert.That(result.Changed, Is.True);
            Assert.That(result.DrydockFormatVer, Is.EqualTo(DrydockFormat.Current));
        });
    }

    [Test]
    public void TheShippedLadderIsOrderedAndInsideTheWindow()
    {
        var froms = DrydockDocumentRebake.FormatSteps.Select(s => s.FromDrydockFormat).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(froms, Is.Ordered.And.Unique);
            Assert.That(froms, Has.All.InRange(DrydockFormat.MinimumSupported, DrydockFormat.Current - 1));
        });
    }

    private static string Proximity(int uid, string nextVisualUpdate, string type = "TriggerOnProximity") =>
        $"- proto: PortableFlasher\n  entities:\n  - uid: {uid}\n    components:\n    - type: {type}\n      nextTrigger: 1.5\n      nextVisualUpdate: {nextVisualUpdate}\n";

    [Test]
    public void TheProximitySentinelIsDroppedAndARealDeadlineIsKept()
    {
        // 922337203685.4775 s is TimeSpan.MaxValue; the serializer wrote it less the storing clock.
        var doc = Doc(Proximity(2, "922337196085.4775"));
        var live = Doc(Proximity(2, "0.4"));
        var other = Doc(Proximity(2, "922337196085.4775", type: "SomethingElse"));

        var result = DrydockDocumentRebake.Transform(doc, DrydockMigrationTable.Empty, DrydockFormat.Current);
        var again = DrydockDocumentRebake.Transform(result.Yaml, DrydockMigrationTable.Empty, DrydockFormat.Current);

        Assert.Multiple(() =>
        {
            Assert.That(result.Yaml, Is.EqualTo(Canon(doc.Replace("      nextVisualUpdate: 922337196085.4775" + Environment.NewLine, ""))));
            Assert.That(result.AppliedSteps, Is.EqualTo(new[] { DrydockDocumentRebake.ProximityVisualSentinel.Name }));
            Assert.That(result.Changed, Is.True);
            Assert.That(again.Yaml, Is.SameAs(result.Yaml));
            Assert.That(again.Changed, Is.False);
            Assert.That(DrydockDocumentRebake.Transform(live, DrydockMigrationTable.Empty, DrydockFormat.Current).Yaml, Is.SameAs(live));
            Assert.That(DrydockDocumentRebake.Transform(other, DrydockMigrationTable.Empty, DrydockFormat.Current).Yaml, Is.SameAs(other));
        });
    }

    [Test]
    public void TheFingerprintIsUnchangedByTheIdRead()
    {
        // ReadDriftMetadata hashes what ReadDriftIds returns, and the value is persisted: both paths must agree with the literal hash.
        var doc = Doc(Group("Keep", 3), Group("Old", 2));
        var (ids, _) = DrydockSystem.ReadDriftIds(doc);
        var expected = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("Keep\nOld"));

        Assert.Multiple(() =>
        {
            Assert.That(DrydockSystem.DriftFingerprint(ids), Is.EqualTo(expected));
            Assert.That(DrydockSystem.ReadDriftMetadata(doc).Fingerprint, Is.EqualTo(expected));
        });
    }
}
