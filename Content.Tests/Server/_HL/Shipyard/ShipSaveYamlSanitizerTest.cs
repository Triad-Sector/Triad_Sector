using Content.Server._HL.Shipyard;
using NUnit.Framework;

namespace Content.Tests.Server._HL.Shipyard;

/// <summary>
/// Covers the load-side scrub, whose risk is the YAML parse/emit round-trip rather than the pruning
/// itself: a ship file the owner cannot load is far worse than the log noise the scrub removes.
/// </summary>
[TestFixture]
[TestOf(typeof(ShipSaveYamlSanitizer))]
public sealed class ShipSaveYamlSanitizerTest
{
    // uid 1 is present. 'invalid' is what the writer emits for a reference it could not resolve, and
    // 99 is a uid the file never defines. Both are dangling; only 1 should survive.
    private const string ShipWithDanglingRefs = @"meta:
  format: 7
entities:
- proto: """"
  entities:
  - uid: 1
    components:
    - type: Transform
- proto: WallSolid
  entities:
  - uid: 2
    components:
    - type: Transform
    - type: EmbeddedContainer
      embeddedObjects:
      - 1
      - invalid
      - 99
    - type: CombatMode
      combatToggleActionEntity: invalid
    - type: VendingMachinePurchase
      purchaseGrid: invalid
";

    private const string CleanShip = @"meta:
  format: 7
entities:
- proto: WallSolid
  entities:
  - uid: 1
    components:
    - type: Transform
  - uid: 2
    components:
    - type: EmbeddedContainer
      embeddedObjects:
      - 1
";

    [Test]
    public void ScrubDropsDanglingSetEntriesAndKeepsLiveOnes()
    {
        var result = ShipSaveYamlSanitizer.ScrubShipLoadYaml(ShipWithDanglingRefs, out var scrubbed);

        Assert.That(scrubbed, Is.EqualTo(4), "two set entries, one scalar, one required-ref component");
        Assert.That(result, Does.Not.Contain("invalid"), "no dangling marker should survive the scrub");
        Assert.That(result, Does.Not.Contain("99"), "a uid the file never defines is dangling too");
        Assert.That(result, Does.Contain("embeddedObjects"), "the live entry keeps the field alive");
    }

    [Test]
    public void ScrubNullsScalarRefAndDropsRequiredRefComponent()
    {
        var result = ShipSaveYamlSanitizer.ScrubShipLoadYaml(ShipWithDanglingRefs, out _);

        // Nullable scalar is nulled in place; the component stays so the rest of its state loads.
        Assert.That(result, Does.Contain("CombatMode"));
        // purchaseGrid is non-nullable, so nulling would just trade one load error for another.
        Assert.That(result, Does.Not.Contain("VendingMachinePurchase"));
    }

    [Test]
    public void ScrubLeavesACleanFileByteIdentical()
    {
        var result = ShipSaveYamlSanitizer.ScrubShipLoadYaml(CleanShip, out var scrubbed);

        Assert.That(scrubbed, Is.EqualTo(0));
        Assert.That(result, Is.EqualTo(CleanShip), "a file with nothing to remove must not be re-emitted");
    }

    [Test]
    public void ScrubReturnsInputUnchangedWhenItCannotParse()
    {
        const string garbage = "this: is: not: a: ship: file:\n\t- [unclosed";

        var result = ShipSaveYamlSanitizer.ScrubShipLoadYaml(garbage, out var scrubbed);

        Assert.That(scrubbed, Is.EqualTo(0));
        Assert.That(result, Is.EqualTo(garbage), "a parse failure must hand the loader the original text");
    }

    // The shape a pre-rework ship save carries: a legacy artifact whose grant action sits in its
    // 'actions' container, with the legacy component nodes real map files serialized (Artifact with
    // isSuppressed, BiasedArtifact). uid 2 is the artifact, uid 3 the action.
    private const string ShipWithLegacyArtifact = @"meta:
  format: 7
entities:
- proto: WallSolid
  entities:
  - uid: 1
    components:
    - type: Transform
- proto: VariedXenoArtifactItem
  entities:
  - uid: 2
    components:
    - type: Transform
    - type: Artifact
      isSuppressed: True
    - type: BiasedArtifact
    - type: ContainerContainer
      containers:
        actions: !type:Container
          ents:
          - 3
- proto: ActionArtifactActivate
  entities:
  - uid: 3
    components:
    - type: Transform
      parent: 2
";

    [Test]
    public void ScrubDropsLegacyActionEntityAndItsContainerSlot()
    {
        var result = ShipSaveYamlSanitizer.ScrubShipLoadYaml(ShipWithLegacyArtifact, out var scrubbed);

        // One action entity, two legacy component nodes, one container slot.
        Assert.That(scrubbed, Is.EqualTo(4));
        Assert.That(result, Does.Not.Contain("ActionArtifactActivate"),
            "the legacy grant action has no replacement and must not reach the loader");
        Assert.That(result, Does.Not.Contain("- 3"),
            "the dropped action's container slot must be pruned with it");
    }

    [Test]
    public void ScrubStripsLegacyComponentNodesButLeavesTheRenameToTheMigration()
    {
        var result = ShipSaveYamlSanitizer.ScrubShipLoadYaml(ShipWithLegacyArtifact, out _);

        Assert.That(result, Does.Not.Contain("BiasedArtifact"));
        Assert.That(result, Does.Not.Contain("isSuppressed"),
            "the legacy Artifact node goes whole, its fields with it");
        // Renaming the body is triad_migration.yml's job, applied inside the engine's deserializer.
        // The scrub touching prototype ids would hide a broken migration entry instead of failing on it.
        Assert.That(result, Does.Contain("VariedXenoArtifactItem"));
    }

    [Test]
    public void ScrubKeepsLiveComponentsWhoseNamesTheReworkReused()
    {
        // ArtifactAnalyzer was deleted by the rework and re-registered with a new data shape; a save
        // written by the current build legitimately carries it, so the scrub must not eat it.
        const string ship = @"meta:
  format: 7
entities:
- proto: MachineArtifactAnalyzer
  entities:
  - uid: 1
    components:
    - type: Transform
    - type: ArtifactAnalyzer
";

        var result = ShipSaveYamlSanitizer.ScrubShipLoadYaml(ship, out var scrubbed);

        Assert.That(scrubbed, Is.EqualTo(0));
        Assert.That(result, Is.EqualTo(ship));
    }
}
