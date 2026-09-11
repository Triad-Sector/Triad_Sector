// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Content.Server._HL.Shipyard;
using Content.Shared.Xenoarchaeology.Artifact.Components;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._Triad.Shipyard;

/// <summary>
/// Guards the day-one rollout bug of the xenoarchaeology rework: ship files live on the player's
/// machine, so a ship saved on the legacy build hands the loader entities whose prototypes the rework
/// removed, and one missing prototype fails the entire ship load ("Failed to load ship.", 13 times on
/// prod within hours of the deploy).
///
/// The fix is two layered halves and this test exercises them together, end to end:
/// triad_migration.yml renames the artifact bodies to their node-graph replacements inside the
/// engine's deserializer, and <see cref="ShipSaveYamlSanitizer.ScrubShipLoadYaml"/> drops the legacy
/// grant action and the deleted legacy component nodes before the loader sees them. Either half alone
/// still fails: without the migration the load dies on the missing body prototype, without the scrub
/// it survives but spews an error per legacy node. The pool fails any test whose run logged an error,
/// so a clean pass here is also the zero-noise assertion.
/// </summary>
[TestFixture]
public sealed class LegacyXenoarchShipLoadTest
{
    // The legacy nodes real pre-rework files carry, verbatim shapes from a live map file of that era:
    // the Artifact/BiasedArtifact component nodes on the body, and the grant action parented into the
    // body's 'actions' container. {0} is the grid's yaml uid.
    private const string LegacyInjection = @"- proto: VariedXenoArtifactItem
  entities:
  - uid: 9001
    components:
    - type: Transform
      parent: {0}
      pos: 0.5,0.5
    - type: Artifact
      isSuppressed: True
    - type: BiasedArtifact
    - type: ContainerContainer
      containers:
        actions: !type:Container
          ents:
          - 9002
- proto: ActionArtifactActivate
  entities:
  - uid: 9002
    components:
    - type: Transform
      parent: 9001
";

    private static readonly Regex UidLine = new(@"^  - uid: (\d+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    [Test]
    public async Task LegacyArtifactShipStillLoads()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var mapLoader = entManager.System<MapLoaderSystem>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var userData = server.ResolveDependency<IResourceManager>().UserData;

        var savedPath = new ResPath("/legacy xenoarch ship.yml");
        var legacyPath = new ResPath("/legacy xenoarch ship injected.yml");

        // A current-format grid file to graft the legacy entities into, so the fixture cannot rot
        // away from the real save format the way a hand-authored file would.
        await server.WaitPost(() =>
        {
            mapSystem.CreateMap(out var mapId);
            var grid = mapSystem.CreateGridEntity(mapId);
            mapSystem.SetTile(grid, new Vector2i(0, 0), new Tile(1));
            entManager.RunMapInit(grid.Owner, entManager.GetComponent<MetaDataComponent>(grid));
            Assert.That(mapLoader.TrySaveGrid(grid.Owner, savedPath));
            entManager.DeleteEntity(grid.Owner);
            mapSystem.DeleteMap(mapId);
        });

        await server.WaitIdleAsync();

        string savedYaml;
        await using (var stream = userData.Open(savedPath, FileMode.Open))
        using (var reader = new StreamReader(stream))
        {
            // The emitter writes \r\n on Windows; the anchors and splice below assume \n.
            savedYaml = (await reader.ReadToEndAsync()).Replace("\r\n", "\n");
        }

        // The freshly saved grid is the file's only entity, so its uid is the file's only uid. Both
        // asserts are controls: if the save format grows a second entity or drops the marker line,
        // the injection below would target the wrong parent and the test would prove nothing.
        var uids = UidLine.Matches(savedYaml);
        Assert.That(uids, Has.Count.EqualTo(1), "expected the empty grid to be the file's only entity");
        var gridYamlUid = uids[0].Groups[1].Value;
        Assert.That(savedYaml, Does.Contain("\nentities:\n"), "expected a top-level entities sequence");

        var injected = savedYaml.Replace("\nentities:\n",
            "\nentities:\n" + LegacyInjection.Replace("{0}", gridYamlUid));

        // The production entry point the console load path runs. Four nodes: the action entity, two
        // legacy component nodes, and the container slot that held the action.
        var scrubbedYaml = ShipSaveYamlSanitizer.ScrubShipLoadYaml(injected, out var scrubbed);
        Assert.That(scrubbed, Is.EqualTo(4), "the scrub missed a legacy node the injection planted");

        await server.WaitPost(() =>
        {
            using var writer = userData.OpenWriteText(legacyPath);
            writer.Write(scrubbedYaml);
        });

        await server.WaitAssertion(() =>
        {
            mapSystem.CreateMap(out var mapId);
            Assert.That(mapLoader.TryLoadGrid(mapId, legacyPath, out var grid),
                "a legacy-artifact ship failed to load; the whole point of the migration is that it cannot");

            // The migration renamed the body inside the deserializer: the file says
            // VariedXenoArtifactItem, the world holds its node-graph replacement.
            var artifacts = entManager.GetEntities()
                .Where(uid => entManager.GetComponentOrNull<MetaDataComponent>(uid)?.EntityPrototype?.ID
                              == "ComplexXenoArtifactItem")
                .ToList();
            Assert.That(artifacts, Has.Count.EqualTo(1));
            Assert.That(entManager.HasComponent<XenoArtifactComponent>(artifacts[0]),
                "the renamed body composed without the rework's artifact component");

            entManager.DeleteEntity(grid!.Value.Owner);
            mapSystem.DeleteMap(mapId);
        });

        await pair.CleanReturnAsync();
    }
}
