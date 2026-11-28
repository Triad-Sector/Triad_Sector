#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server.Storage.Components;
using Content.Server.Storage.EntitySystems;
using Content.Server.VendingMachines;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Content.Shared.Lathe;
using Content.Shared.Research.Prototypes;
using Content.Shared.Stacks;
using Content.Shared.VendingMachines;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The deliberate refresh path for the golden corpus, and the only thing that ever writes it.
    ///
    /// <para>Explicit, so no normal run and no CI filter reaches it: a fixture regenerated on every run
    /// tests today's code against today's code, and the corpus exists to test last season's ships
    /// against today's. Run it when the corpus is meant to move (a new recipe, a new hull, or a
    /// re-bake after a format bump that the ladder has already carried stored ships across), then
    /// review the diff it prints and the sidecar diff in git before committing.</para>
    ///
    /// <para>Each hull is loaded from its vessel's shuttle file, given what a purchase gives it, put
    /// through its named recipes, and stored through the real pipeline. What gets written is what the
    /// store filed, read back from the database: the compressed blob as the blob row holds it, and the
    /// revision row's columns in the sidecar. Every new fixture is then put through the gate's own
    /// verification before the run is allowed to pass, so a refresh cannot commit a corpus the gate
    /// would reject.</para>
    ///
    /// <para>Run: <c>dotnet test Content.IntegrationTests --filter FullyQualifiedName~DrydockGoldenCorpusRefreshLocalTest</c>,
    /// then rebuild before running the gate, since the fixtures are embedded resources.</para>
    /// </summary>
    [TestFixture]
    [Explicit("Rewrites the committed golden corpus. Run deliberately, review the diff, commit.")]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockGoldenCorpusRefreshLocalTest
    {
        private const string DamageWall = "damage-wall";
        private const string CargoIntoLocker = "cargo-into-locker";
        private const string RestockVendor = "restock-vendor";
        private const string QueueLathe = "queue-lathe";
        private const string FireGun = "fire-gun";

        private const string CargoProto = "SheetSteel";
        private const int CargoCount = 17;
        private const string LatheRecipe = "SheetSteel";
        private const string SidearmProto = "WeaponPistolMk58";
        private static readonly ProtoId<DamageTypePrototype> Blunt = "Blunt";

        /// <summary>
        /// Three size classes, every recipe at least once. A small hull for the cheap two, a mid hull for
        /// the vendor and the gun, and a large one carrying four at once so the recipes are also seen
        /// interacting on one document.
        /// </summary>
        private static readonly (string Name, string Vessel, string[] Recipes)[] Plan =
        {
            ("small-piva", "Piva", new[] { DamageWall, CargoIntoLocker }),
            ("medium-medicus", "Medicus", new[] { RestockVendor, FireGun }),
            ("large-hammerhead", "Hammerhead", new[] { RestockVendor, QueueLathe, DamageWall, CargoIntoLocker }),
        };

        [Test]
        public async Task RegenerateTheGoldenCorpus()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var drydock = server.System<DrydockSystem>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var mapLoader = server.System<MapLoaderSystem>();

            var directory = Path.Combine(FindRepositoryRoot(), GoldenCorpus.SourceDirectory);
            Directory.CreateDirectory(directory);
            var commit = Git("rev-parse HEAD");

            // Three berths a hull: its own store, and the verification after it and in the second pass,
            // each of which files a copy and stores it again.
            var (owner, station) = await GoldenCorpus.PrepareHarness(pair, Plan.Length * 3);
            var map = await pair.CreateTestMap();

            var reports = new List<GoldenReport>();

            foreach (var (name, vesselId, recipes) in Plan)
            {
                var vessel = protoMan.Index<VesselPrototype>(vesselId);
                EntityUid grid = default;

                await server.WaitPost(() =>
                {
                    Assert.That(mapLoader.TryLoadGrid(map.MapId, vessel.ShuttlePath, out var loaded), Is.True,
                        $"{vesselId}: its shuttle file would not load.");
                    grid = loaded!.Value.Owner;

                    // What the purchase gives a hull that its file does not carry, so the store files the
                    // vessel id and the retrieve rebuilds the vessel's own station rather than a plain one.
                    entMan.AddComponents(grid, vessel.AddComponents);
                    entMan.EnsureComponent<VesselComponent>(grid).VesselId = vessel.ID;
                    server.System<MetaDataSystem>().SetEntityName(grid, $"Golden {vessel.Name}");
                });

                // Power nets and atmos settle before anything is touched.
                await pair.RunTicksSync(10);

                var probes = new List<GoldenProbe>();
                var targets = new List<EntityUid>();
                var ammoBefore = -1;

                await server.WaitPost(() =>
                {
                    foreach (var recipe in recipes)
                    {
                        probes.Add(Apply(pair, grid, recipe, ref ammoBefore, out var target));
                        targets.Add(target);
                    }
                });

                // Long enough for a fired round to leave the hull or strike it; a projectile still in the
                // air at the store is a real grid child and would be filed.
                await pair.RunTicksSync(60);

                DrydockStateSnapshot before = default!;
                await server.WaitPost(() =>
                {
                    for (var i = 0; i < probes.Count; i++)
                        Read(entMan, targets[i], probes[i]);

                    before = fidelity.SnapshotGrid(grid);
                });

                if (recipes.Contains(FireGun))
                {
                    var gunProbe = probes.Single(p => p.Kind == GoldenProbe.GunAmmo);
                    Assert.That((int) gunProbe.Value, Is.LessThan(ammoBefore),
                        $"{name}: the gun recipe did not fire ({ammoBefore} rounds before, {gunProbe.Value} after). Skip the recipe rather than file an unfired gun as fired.");
                }

                var (result, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(grid, owner, null), GoldenCorpus.OperationTimeout);
                Assert.That(result, Is.EqualTo(DrydockStoreResult.Success), $"{name}: the store refused ({result}).");

                var filed = await store.LoadCurrent(shipId!.Value);
                Assert.That(filed, Is.Not.Null, $"{name}: nothing was filed.");

                var fixture = new GoldenFixture
                {
                    Name = name,
                    VesselProto = filed!.Ship.VesselProto,
                    ShipName = filed.Ship.ShipName,
                    SizeClass = filed.Ship.SizeClass,
                    Recipes = recipes.ToList(),
                    GeneratedAtCommit = commit,
                    GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
                    BlobFileSha256 = GoldenCorpus.Sha256(filed.Blob),
                    BlobFileBytes = filed.Blob.Length,
                    Blob = filed.Blob,
                    Probes = probes,
                    Revision = new GoldenRevision
                    {
                        Checksum = Convert.ToBase64String(filed.Revision.Checksum),
                        SizeBytes = filed.Revision.SizeBytes,
                        EngineFormatVer = filed.Revision.EngineFormatVer,
                        DrydockFormatVer = filed.Revision.DrydockFormatVer,
                        ProtoFingerprint = Convert.ToBase64String(filed.Revision.ProtoFingerprint),
                        CapturedKeyHash = Convert.ToBase64String(filed.Revision.CapturedKeyHash),
                        AppraisedValue = filed.Revision.AppraisedValue,
                        Manifest = filed.Revision.Manifest,
                    },
                };

                await ReportAgainstCommitted(directory, fixture, protoMan);

                await File.WriteAllBytesAsync(Path.Combine(directory, name + ".blob.zst"), fixture.Blob);
                await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"),
                    JsonSerializer.Serialize(fixture, GoldenCorpus.Json) + "\n");

                // The gate's own verification, on the fixture just written. The fidelity oracle rides
                // along for the record: field-level drift is not what the gate asserts, and printing it
                // here is how an exemption gets read before anyone writes one.
                DrydockStateSnapshot after = default!;
                var report = await GoldenCorpus.Verify(pair, fixture, owner, station,
                    reborn => after = fidelity.SnapshotGrid(reborn));
                reports.Add(report);

                var drift = DrydockStateSnapshot.Diff(before, after)
                    .Where(DrydockRoundTripExpectations.IsUnexpected)
                    .ToList();

                await TestContext.Out.WriteLineAsync(
                    $"[golden-refresh] {name}: {vesselId}, recipes {string.Join(", ", recipes)}; blob {fixture.BlobFileBytes} bytes "
                    + $"compressed, {fixture.Revision.SizeBytes} uncompressed; {fixture.Probes.Count} probe(s)");
                await TestContext.Out.WriteLineAsync($"[golden-refresh] {report}");
                await TestContext.Out.WriteLineAsync(
                    $"[golden-refresh] {name}: fidelity oracle, {drift.Count} unexpected field difference(s) across the round trip"
                    + string.Concat(drift.Take(40).Select(d => Environment.NewLine + "    " + d)));
            }

            // A second pass over every fixture, in the reverse of the order they were made. Each was
            // verified right after its own store, on a server that had seen exactly the hulls before it;
            // anything in a store that depends on what the server stored earlier passes that check and
            // then fails the gate, which walks the corpus in name order. This pass is where it shows up
            // instead: it is how an empty lathe queue's capture was caught depending on server history.
            foreach (var (name, _, _) in Plan.Reverse())
            {
                var written = JsonSerializer.Deserialize<GoldenFixture>(
                    await File.ReadAllTextAsync(Path.Combine(directory, name + ".json")), GoldenCorpus.Json)!;
                written.Blob = await File.ReadAllBytesAsync(Path.Combine(directory, name + ".blob.zst"));

                var report = await GoldenCorpus.Verify(pair, written, owner, station);
                reports.Add(report);
                await TestContext.Out.WriteLineAsync($"[golden-refresh] second pass, reverse order: {report}");
            }

            Assert.That(reports.Where(r => !r.Passed).Select(r => r.ToString()), Is.Empty,
                "A refreshed fixture fails the gate's own verification, so committing it would commit a red gate.");

            Assert.That(Plan.Length, Is.EqualTo(GoldenCorpus.CommittedFixtures),
                "The gate's committed-count control has to move with the plan.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Applies one recipe on the game thread and returns the probe for what it changed, with the
        /// entity it changed in <paramref name="target"/>. The probe's value and position are read later
        /// by <see cref="Read"/>, after the hull has ticked, so they state what was stored rather than
        /// what was set.
        /// </summary>
        private static GoldenProbe Apply(TestPair pair, EntityUid grid, string recipe, ref int ammoBefore, out EntityUid target)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var protoMan = server.ResolveDependency<IPrototypeManager>();

            switch (recipe)
            {
                case DamageWall:
                {
                    target = Pick(entMan, grid, (uid, id) =>
                        id.StartsWith("Wall", StringComparison.Ordinal)
                        && entMan.HasComponent<DamageableComponent>(uid)
                        && entMan.GetComponent<TransformComponent>(uid).Anchored);

                    server.System<DamageableSystem>().TryChangeDamage(target,
                        new DamageSpecifier(protoMan.Index(Blunt), FixedPoint2.New(37)), ignoreResistances: true);

                    return Probe(GoldenProbe.Damage);
                }
                case CargoIntoLocker:
                {
                    // Closed, and holding no steel already, so the stack goes in as its own entity rather
                    // than merging into one that was there.
                    target = Pick(entMan, grid, (uid, id) =>
                        id.Contains("Locker", StringComparison.Ordinal)
                        && entMan.TryGetComponent<EntityStorageComponent>(uid, out var storage)
                        && !storage.Open
                        && storage.Contents.ContainedEntities.All(e =>
                            entMan.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID != CargoProto));

                    var cargo = entMan.SpawnEntity(CargoProto, entMan.GetComponent<TransformComponent>(target).Coordinates);
                    server.System<SharedStackSystem>().SetCount(cargo, CargoCount);
                    Assert.That(server.System<EntityStorageSystem>().Insert(cargo, target), Is.True, "The cargo went into the locker.");

                    var probe = Probe(GoldenProbe.ContainedStack);
                    probe.Item = CargoProto;
                    return probe;
                }
                case RestockVendor:
                {
                    // A vendor with at least one finite line, so a restock changes something: an
                    // unlimited line is never restocked.
                    target = Pick(entMan, grid, (uid, _) =>
                        entMan.TryGetComponent<VendingMachineComponent>(uid, out var comp)
                        && comp.Inventory.Values.Any(e => e.Amount is > 0 and < uint.MaxValue));

                    server.System<VendingMachineSystem>().RestockInventoryFromPrototype(target, restockQuality: 1f);
                    return Probe(GoldenProbe.VendorInventory);
                }
                case QueueLathe:
                {
                    target = Pick(entMan, grid, (uid, _) => entMan.HasComponent<LatheComponent>(uid));
                    var batch = new LatheRecipeBatch(protoMan.Index<LatheRecipePrototype>(LatheRecipe), itemsPrinted: 1, itemsRequested: 5, actor: null);
                    entMan.GetComponent<LatheComponent>(target).Queue.Add(batch);

                    var probe = Probe(GoldenProbe.LatheQueue);
                    probe.Item = LatheRecipe;
                    return probe;
                }
                case FireGun:
                {
                    // A sidearm carried aboard and fired once where somebody sat. Roster hulls sell their
                    // turrets unloaded, so the ship's own guns have nothing to fire; this pistol spawns
                    // with its magazine and chamber filled and needs no wielder.
                    var seat = Pick(entMan, grid, (_, id) => id.StartsWith("Chair", StringComparison.Ordinal));
                    target = entMan.SpawnEntity(SidearmProto, entMan.GetComponent<TransformComponent>(seat).Coordinates);

                    ammoBefore = AmmoCount(entMan, target);

                    // A spawned pistol comes with its bolt open, and an open bolt hands out no round: the
                    // shot then takes the empty branch and nothing is fired. A player racks it first.
                    var chamber = entMan.GetComponent<ChamberMagazineAmmoProviderComponent>(target);
                    if (chamber.BoltClosed != true)
                        server.System<GunSystem>().SetBoltClosed(target, chamber, true);

                    server.System<GunSystem>().AttemptShoot(target, entMan.GetComponent<GunComponent>(target));
                    return Probe(GoldenProbe.GunAmmo);
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(recipe), recipe, "No such recipe.");
            }

            GoldenProbe Probe(string kind) => new() { Kind = kind };
        }

        /// <summary>Fills a probe from the live hull, just before the store: where its entity is, and what it holds.</summary>
        private static void Read(IEntityManager entMan, EntityUid uid, GoldenProbe probe)
        {
            Assert.That(entMan.EntityExists(uid), Is.True, $"The {probe.Kind} target was deleted before the store.");

            var xform = entMan.GetComponent<TransformComponent>(uid);
            probe.Proto = entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype!.ID;
            probe.X = MathF.Round(xform.LocalPosition.X, 2);
            probe.Y = MathF.Round(xform.LocalPosition.Y, 2);

            switch (probe.Kind)
            {
                case GoldenProbe.Damage:
                    probe.Value = (float) entMan.GetComponent<DamageableComponent>(uid).TotalDamage;
                    break;
                case GoldenProbe.VendorInventory:
                    probe.Inventory = entMan.GetComponent<VendingMachineComponent>(uid).Inventory.Values
                        .Where(e => e.Amount < uint.MaxValue)
                        .ToDictionary(e => e.ID, e => e.Amount);
                    break;
                case GoldenProbe.LatheQueue:
                    var batch = entMan.GetComponent<LatheComponent>(uid).Queue.Single(b => b.Recipe.ID == probe.Item);
                    probe.Requested = batch.ItemsRequested;
                    probe.Printed = batch.ItemsPrinted;
                    break;
                case GoldenProbe.ContainedStack:
                    probe.Value = CargoCount;
                    break;
                case GoldenProbe.GunAmmo:
                    probe.Value = AmmoCount(entMan, uid);
                    break;
            }
        }

        private static int AmmoCount(IEntityManager entMan, EntityUid gun)
        {
            var ev = new GetAmmoCountEvent();
            entMan.EventBus.RaiseLocalEvent(gun, ref ev);
            return ev.Count;
        }

        /// <summary>The first direct grid child that qualifies, in a stable order: prototype, then tile.</summary>
        private static EntityUid Pick(IEntityManager entMan, EntityUid grid, Func<EntityUid, string, bool> qualifies)
        {
            var candidates = new List<(EntityUid Uid, string Id, float X, float Y)>();
            var children = entMan.GetComponent<TransformComponent>(grid).ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                var id = entMan.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID;
                if (id == null || !qualifies(child, id))
                    continue;

                var pos = entMan.GetComponent<TransformComponent>(child).LocalPosition;
                candidates.Add((child, id, pos.X, pos.Y));
            }

            Assert.That(candidates, Is.Not.Empty, "The hull carries nothing this recipe can act on; pick another hull for it.");

            return candidates
                .OrderBy(c => c.Id, StringComparer.Ordinal)
                .ThenBy(c => c.Y)
                .ThenBy(c => c.X)
                .First().Uid;
        }

        /// <summary>
        /// The reviewable half of a refresh: what the new manifest says that the committed one did not.
        /// Printed, not asserted, because a refresh exists to move the corpus.
        /// </summary>
        private static async Task ReportAgainstCommitted(string directory, GoldenFixture fresh, IPrototypeManager protoMan)
        {
            var path = Path.Combine(directory, fresh.Name + ".json");
            if (!File.Exists(path))
            {
                await TestContext.Out.WriteLineAsync($"[golden-refresh] {fresh.Name}: new fixture, nothing committed to compare with.");
                return;
            }

            var committed = JsonSerializer.Deserialize<GoldenFixture>(await File.ReadAllTextAsync(path), GoldenCorpus.Json)!;
            var changes = new List<string>();
            GoldenCorpus.CompareManifests(
                DrydockManifest.Deserialize(committed.Revision.Manifest)!,
                DrydockManifest.Deserialize(fresh.Revision.Manifest)!,
                GoldenCorpus.UnsavableIn(protoMan),
                changes);

            if (committed.Revision.ProtoFingerprint != fresh.Revision.ProtoFingerprint)
                changes.Add("prototype fingerprint changed");

            if (committed.Revision.CapturedKeyHash != fresh.Revision.CapturedKeyHash)
                changes.Add("captured-key hash changed");

            await TestContext.Out.WriteLineAsync(
                $"[golden-refresh] {fresh.Name}: against the committed fixture from {committed.GeneratedAtCommit}, "
                + (changes.Count == 0
                    ? "the manifest is unchanged."
                    : $"{changes.Count} change(s):" + string.Concat(changes.Select(c => Environment.NewLine + "    " + c))));
        }

        private static string FindRepositoryRoot()
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SpaceStation14.slnx")))
                dir = dir.Parent;

            return dir?.FullName ?? throw new DirectoryNotFoundException("No SpaceStation14.slnx above the test directory.");
        }

        private static string Git(string arguments)
        {
            try
            {
                using var git = Process.Start(new ProcessStartInfo("git", arguments)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    WorkingDirectory = FindRepositoryRoot(),
                })!;
                var output = git.StandardOutput.ReadToEnd().Trim();
                git.WaitForExit();
                return git.ExitCode == 0 ? output : "unknown";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }
}
