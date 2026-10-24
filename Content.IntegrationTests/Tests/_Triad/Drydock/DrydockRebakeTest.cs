#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.ShipSize;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The tier 1 re-bake runner against a real database and the real migration files: a stored ship
    /// whose document still carries a renamed id gets a system revision with the rename baked in, and
    /// nothing else about the fleet moves. Every ship is freshly minted and the pooled database carries
    /// other fixtures' ships, so every assertion reads this test's own ids out of the sweep's report.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockRebakeTest
    {
        /// <summary>
        /// A real line of <c>migration.yml</c>. Checked against the loaded table and registry at the top
        /// of every test, so a content change that breaks the premise fails loudly as a premise.
        /// </summary>
        private const string RenamedFrom = "lantern";
        private const string RenamedTo = "Lantern";

        /// <summary>
        /// The wall-clock bound on every pumped operation here: the sweep's worker hops are real time
        /// on another thread, not ticks.
        /// </summary>
        private static readonly TimeSpan SweepTimeout = TimeSpan.FromSeconds(120);

        [Test]
        public async Task ASweepBakesARenameIntoASystemRevisionAndKeepsTheSource()
        {
            await using var pair = await PoolManager.GetServerClient();
            var (store, drydock, owner) = await Setup(pair);

            var ship = Guid.NewGuid();
            var control = Guid.NewGuid();
            var sourceYaml = Document(RenamedFrom);
            await FileDocument(store, ship, owner, "Kestrel", sourceYaml, Manifest(RenamedFrom));
            await FileDocument(store, control, owner, "Harrier", Document(RenamedTo), Manifest(RenamedTo));
            var source = (await store.LoadCurrent(ship))!;

            // The candidate listing walked a row at a time: every stored ship once, headers only.
            var walked = new List<DrydockRebakeCandidate>();
            Guid? cursor = null;
            while (walked.Count < 10_000)
            {
                var page = await store.GetRebakeCandidates(cursor, 1);
                if (page.Count == 0)
                    break;

                walked.AddRange(page);
                cursor = page[^1].ShipGuid;
            }

            Assert.Multiple(() =>
            {
                Assert.That(walked.Select(c => c.ShipGuid), Is.Unique, "The cursor never revisits a ship.");
                Assert.That(walked.SingleOrDefault(c => c.ShipGuid == ship),
                    Is.EqualTo(new DrydockRebakeCandidate(ship, "Kestrel", 1, DrydockFormat.Current)));
                Assert.That(walked.Select(c => c.ShipGuid), Does.Contain(control));
            });

            // A page of one, so the walk crosses pages and the keyset cursor is exercised on this provider.
            var report = await DrydockTestHelpers.RunOnServer(pair, () => drydock.RunRebakeSweep(throttle: false, pageSize: 1), SweepTimeout);

            Assert.Multiple(() =>
            {
                Assert.That(report, Is.Not.Null);
                Assert.That(report!.Completed, Is.True);
                Assert.That(Outcome(report, ship), Is.EqualTo(DrydockRebakeShipResult.Filed));
                Assert.That(Outcome(report, control), Is.EqualTo(DrydockRebakeShipResult.Clean),
                    "Control: the same document already on the new id needs nothing.");
                Assert.That(report.Unresolvable, Does.Not.Contain(ship).And.Not.Contain(control));
            });

            var current = (await store.LoadCurrent(ship))!;
            var yaml = Encoding.UTF8.GetString(DrydockSystem.DecompressZstd(current.Blob));
            var bytes = Encoding.UTF8.GetBytes(yaml);
            var manifest = DrydockManifest.Deserialize(current.Revision.Manifest)!;

            Assert.Multiple(() =>
            {
                Assert.That(current.Ship.CurrentRevision, Is.EqualTo(2), "The pointer advanced to the re-bake.");
                Assert.That(current.Ship.State, Is.EqualTo(DrydockShipState.Stored));
                Assert.That(current.Revision.Kind, Is.EqualTo(DrydockRevisionKind.SystemRebake));
                Assert.That(current.Revision.DerivedFromRevision, Is.EqualTo(1));
                Assert.That(current.Revision.RebakeVersion, Is.EqualTo(DrydockSystem.LadderVersion).And.EqualTo(1));
                Assert.That(current.Revision.ActorUserId, Is.Null);
                Assert.That(current.Revision.CreatedRoundId, Is.Null);

                Assert.That(yaml, Does.Contain($"proto: {RenamedTo}\n"), "The document carries the new id.");
                Assert.That(yaml, Does.Not.Contain($"proto: {RenamedFrom}\n"), "And not the old one.");
                Assert.That(current.Revision.Checksum, Is.EqualTo(SHA256.HashData(bytes)), "The checksum is over the new document.");
                Assert.That(current.Revision.SizeBytes, Is.EqualTo(bytes.Length));
                Assert.That(current.Revision.ProtoFingerprint, Is.EqualTo(DrydockSystem.ReadDriftMetadata(yaml).Fingerprint));
                Assert.That(current.Revision.ProtoFingerprint, Is.Not.EqualTo(source.Revision.ProtoFingerprint),
                    "The id set changed, so the fingerprint did.");
                Assert.That(current.Revision.CapturedKeyHash, Is.EqualTo(source.Revision.CapturedKeyHash), "A rename moves no captured key.");
                Assert.That(current.Revision.AppraisedValue, Is.EqualTo(source.Revision.AppraisedValue));
                Assert.That(manifest.Entries.Select(e => e.Proto), Is.EqualTo(new[] { "", RenamedTo }),
                    "The manifest describes the new document.");
                Assert.That(manifest.Entries[1].Stack, Is.EqualTo(3), "Everything else in the manifest is carried.");
            });

            var sourceStill = await store.LoadRevision(ship, 1);
            var audit = (await store.GetAudit(ship)).Where(a => a.Action == DrydockAuditAction.Rebake).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(sourceStill?.Blob, Is.EqualTo(source.Blob), "Append-only: the source document is still there, byte for byte.");
                Assert.That(audit, Has.Count.EqualTo(1));
                Assert.That(audit.SingleOrDefault()?.Revision, Is.EqualTo(2));
                Assert.That(audit.SingleOrDefault()?.ActorUserId, Is.Null);
            });
            Assert.That(await Revisions(pair, control), Is.EqualTo(new[] { 1 }), "The clean control got no revision.");

            // Idempotent: the re-baked document needs nothing, so a second sweep files nothing.
            var again = await DrydockTestHelpers.RunOnServer(pair, () => drydock.RunRebakeSweep(throttle: false), SweepTimeout);
            Assert.Multiple(() =>
            {
                Assert.That(Outcome(again, ship), Is.EqualTo(DrydockRebakeShipResult.Clean));
                Assert.That(Outcome(again, control), Is.EqualTo(DrydockRebakeShipResult.Clean));
            });
            Assert.That(await Revisions(pair, ship), Is.EqualTo(new[] { 1, 2 }));

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task AShipOutOfStorageIsNotListedOrTouched()
        {
            await using var pair = await PoolManager.GetServerClient();
            var (store, drydock, owner) = await Setup(pair);

            var ship = Guid.NewGuid();
            await FileDocument(store, ship, owner, "Kestrel", Document(RenamedFrom), Manifest(RenamedFrom));
            Assert.That(await store.TrySetState(ship, DrydockShipState.Stored, DrydockShipState.CheckedOut, DrydockAuditAction.Retrieve, owner, null, null), Is.True);

            var report = await DrydockTestHelpers.RunOnServer(pair, () => drydock.RunRebakeSweep(throttle: false), SweepTimeout);
            Assert.Multiple(() =>
            {
                Assert.That(report!.Completed, Is.True);
                Assert.That(report.Ships.ContainsKey(ship), Is.False, "A checked-out ship is not a candidate.");
            });
            Assert.That(await Revisions(pair, ship), Is.EqualTo(new[] { 1 }));

            // Control: stored again, the same sweep re-bakes it.
            Assert.That(await store.TrySetState(ship, DrydockShipState.CheckedOut, DrydockShipState.Stored, DrydockAuditAction.ClaimReleased, null, null, "test"), Is.True);
            var stored = await DrydockTestHelpers.RunOnServer(pair, () => drydock.RunRebakeSweep(throttle: false), SweepTimeout);
            Assert.That(Outcome(stored, ship), Is.EqualTo(DrydockRebakeShipResult.Filed));
            Assert.That(await Revisions(pair, ship), Is.EqualTo(new[] { 1, 2 }));

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task AStoreLandingMidRebakeWinsAndTheSweepCountsItStale()
        {
            await using var pair = await PoolManager.GetServerClient();
            var (store, drydock, owner) = await Setup(pair);

            var ship = Guid.NewGuid();
            await FileDocument(store, ship, owner, "Kestrel", Document(RenamedFrom), Manifest(RenamedFrom));

            var secondStart = (DrydockRebakeStart?) null;
            var report = await DrydockTestHelpers.RunOnServer(pair, () => drydock.RunRebakeSweep(throttle: false, async guid =>
            {
                if (guid != ship)
                    return;

                secondStart = drydock.StartRebakeSweep("test");

                // The player stores again between the worker's read of revision 1 and the filing.
                await FileDocument(store, ship, owner, "Kestrel", Document(RenamedFrom), Manifest(RenamedFrom));
            }), SweepTimeout);

            Assert.Multiple(() =>
            {
                Assert.That(secondStart, Is.EqualTo(DrydockRebakeStart.AlreadyRunning), "One sweep at a time.");
                Assert.That(Outcome(report, ship), Is.EqualTo(DrydockRebakeShipResult.Stale));
                Assert.That(report.Completed, Is.True, "A stale ship is normal and the sweep carries on.");
            });

            Assert.That(await Kinds(pair, ship), Is.EqualTo(new[] { DrydockRevisionKind.PlayerStore, DrydockRevisionKind.PlayerStore }),
                "Nothing filed on top of the player's store.");
            Assert.That((await store.GetAudit(ship)).Any(a => a.Action == DrydockAuditAction.Rebake), Is.False);

            // Control: the next sweep derives from the store that won and files.
            var next = await DrydockTestHelpers.RunOnServer(pair, () => drydock.RunRebakeSweep(throttle: false), SweepTimeout);
            Assert.That(Outcome(next, ship), Is.EqualTo(DrydockRebakeShipResult.Filed));
            Assert.That((await store.LoadCurrent(ship))!.Revision.DerivedFromRevision, Is.EqualTo(2));

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task TheKillSwitchesStopTheSweep()
        {
            await using var pair = await PoolManager.GetServerClient();
            var (store, drydock, owner) = await Setup(pair);
            var cfg = pair.Server.ResolveDependency<IConfigurationManager>();

            var ship = Guid.NewGuid();
            await FileDocument(store, ship, owner, "Kestrel", Document(RenamedFrom), Manifest(RenamedFrom));

            try
            {
                await pair.Server.WaitPost(() => cfg.SetCVar(TriadCCVars.DrydockReadOnly, true));
                var readOnly = await DrydockTestHelpers.RunOnServer(pair, () => drydock.RunRebakeSweep(throttle: false), SweepTimeout);
                var start = DrydockRebakeStart.Started;
                await pair.Server.WaitPost(() => start = drydock.StartRebakeSweep("test"));
                Assert.Multiple(() =>
                {
                    Assert.That(readOnly!.Ships, Is.Empty, "Read-only: the sweep reads nothing.");
                    Assert.That(readOnly.Completed, Is.False);
                    Assert.That(start, Is.EqualTo(DrydockRebakeStart.Disabled));
                });

                await pair.Server.WaitPost(() =>
                {
                    cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);
                    cfg.SetCVar(TriadCCVars.DrydockRebakeEnabled, false);
                });
                var switchedOff = await DrydockTestHelpers.RunOnServer(pair, () => drydock.RunRebakeSweep(throttle: false), SweepTimeout);
                Assert.That(switchedOff!.Ships, Is.Empty, "The re-bake switch alone stops it too.");
                Assert.That(await Revisions(pair, ship), Is.EqualTo(new[] { 1 }));

                // Mid-sweep: read-only turned on while this ship's document is ready to file.
                await pair.Server.WaitPost(() => cfg.SetCVar(TriadCCVars.DrydockRebakeEnabled, true));
                var stopped = await DrydockTestHelpers.RunOnServer(pair, () => drydock.RunRebakeSweep(throttle: false, guid =>
                {
                    if (guid == ship)
                        cfg.SetCVar(TriadCCVars.DrydockReadOnly, true);

                    return Task.CompletedTask;
                }), SweepTimeout);
                Assert.Multiple(() =>
                {
                    Assert.That(stopped!.Completed, Is.False, "Stopped, not finished.");
                    Assert.That(stopped.Ships.ContainsKey(ship), Is.False, "Stopped before an outcome for the ship it was holding.");
                });
                Assert.That(await Revisions(pair, ship), Is.EqualTo(new[] { 1 }), "Nothing filed after the switch.");

                // Control: every switch back on, the same sweep files.
                await pair.Server.WaitPost(() => cfg.SetCVar(TriadCCVars.DrydockReadOnly, false));
                var resumed = await DrydockTestHelpers.RunOnServer(pair, () => drydock.RunRebakeSweep(throttle: false), SweepTimeout);
                Assert.That(Outcome(resumed, ship), Is.EqualTo(DrydockRebakeShipResult.Filed));
            }
            finally
            {
                await pair.Server.WaitPost(() =>
                {
                    cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);
                    cfg.SetCVar(TriadCCVars.DrydockRebakeEnabled, true);
                });
            }

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// End to end on a real grid: a ship stored by the pipeline, its document put back on the old id
        /// the way a store from before the rename would have written it, re-baked, and retrieved. The
        /// retrieve reads the re-baked revision and the lantern comes back as itself.
        /// </summary>
        [Test]
        public async Task ARebakedShipRetrieves()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var (store, drydock, owner) = await Setup(pair);
            await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var (station, shipGrid, _) = await DrydockRoundTripTest.BuildShipAndStation(pair);
            // Tile (0, 0), clear of the airlock the builder anchors on tile (1, 1).
            await server.WaitPost(() => server.EntMan.SpawnEntity(RenamedTo, new EntityCoordinates(shipGrid, new Vector2(0.5f, 0.5f))));
            await pair.RunTicksSync(5);

            var (stored, shipId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(shipGrid, owner, null), SweepTimeout);
            Assert.That(stored, Is.EqualTo(DrydockStoreResult.Success));
            await pair.RunTicksSync(5);

            // Put the stored document back on the old id, as a new player revision.
            var filed = (await store.LoadCurrent(shipId!.Value))!;
            var newYaml = Encoding.UTF8.GetString(DrydockSystem.DecompressZstd(filed.Blob));
            // The engine writes the platform's line ending, so the group line is matched either way.
            var group = new Regex($@"^- proto: {RenamedTo}(\r?)$", RegexOptions.Multiline);
            Assert.That(group.Matches(newYaml), Has.Count.EqualTo(1), "Control: the stored ship carries one lantern group.");
            var oldYaml = group.Replace(newYaml, $"- proto: {RenamedFrom}$1");
            var oldManifest = filed.Revision.Manifest.Replace($"\"p\":\"{RenamedTo}\"", $"\"p\":\"{RenamedFrom}\"");
            await FileDocument(store, shipId.Value, owner, filed.Ship.ShipName, oldYaml, oldManifest, filed.Revision.CapturedKeyHash, filed.Ship.SizeClass);

            var report = await DrydockTestHelpers.RunOnServer(pair, () => drydock.RunRebakeSweep(throttle: false), SweepTimeout);
            Assert.That(Outcome(report, shipId.Value), Is.EqualTo(DrydockRebakeShipResult.Filed));

            var rebaked = (await store.LoadCurrent(shipId.Value))!;
            Assert.Multiple(() =>
            {
                Assert.That(rebaked.Revision.Kind, Is.EqualTo(DrydockRevisionKind.SystemRebake));
                Assert.That(rebaked.Revision.Manifest, Is.EqualTo(filed.Revision.Manifest),
                    "The manifest's renames undo exactly what was done to it.");
            });

            var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId.Value, owner, station, null), SweepTimeout);
            Assert.That(retrieved.Result, Is.EqualTo(DrydockRetrieveResult.Success));
            await pair.RunTicksSync(5);

            var lanterns = 0;
            await server.WaitPost(() =>
            {
                var query = server.EntMan.AllEntityQueryEnumerator<MetaDataComponent, TransformComponent>();
                while (query.MoveNext(out _, out var meta, out var xform))
                {
                    if (xform.GridUid == retrieved.Grid && meta.EntityPrototype?.ID == RenamedTo)
                        lanterns++;
                }
            });
            Assert.That(lanterns, Is.EqualTo(1));

            await pair.CleanReturnAsync();
        }

        private static async Task<(DrydockStore Store, DrydockSystem Drydock, Guid Owner)> Setup(TestPair pair)
        {
            var server = pair.Server;
            var cfg = server.ResolveDependency<IConfigurationManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var resources = server.ResolveDependency<IResourceManager>();
            var db = server.ResolveDependency<IServerDbManager>();
            var drydock = server.System<DrydockSystem>();

            DrydockMigrationTable table = null!;
            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);
                cfg.SetCVar(TriadCCVars.DrydockRebakeEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockTickBudgetMs, 0);
                table = DrydockMigrationTable.Load(resources);
            });

            Assert.Multiple(() =>
            {
                Assert.That(table.Renamed.GetValueOrDefault(RenamedFrom), Is.EqualTo(RenamedTo), "Premise: the real table renames the id.");
                Assert.That(table.Deleted, Does.Not.Contain(RenamedFrom), "Premise: not also deleted, which the re-bake leaves alone.");
                Assert.That(table.Renamed.ContainsKey(RenamedTo), Is.False, "Premise: the target is not renamed again.");
                Assert.That(protoMan.HasIndex<EntityPrototype>(RenamedTo), Is.True, "Premise: the target exists.");
                Assert.That(protoMan.HasIndex<EntityPrototype>(RenamedFrom), Is.False, "Premise: the source does not.");
            });

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);
            await server.ResolveDependency<DrydockStore>().AddBerth(owner, ShipSizeClass.Cutter, DrydockBerthKind.Granted, 0, null, null);

            return (server.ResolveDependency<DrydockStore>(), drydock, owner);
        }

        /// <summary>A grid and one group, the shape the engine writes, small enough to read.</summary>
        private static string Document(string proto) =>
            "meta:\n  format: 7\n  category: Grid\nentities:\n"
            + "- proto: \"\"\n  entities:\n  - uid: 1\n    components:\n    - type: Transform\n      parent: invalid\n"
            + $"- proto: {proto}\n  entities:\n  - uid: 2\n    components:\n    - type: Transform\n      pos: 0.5,0.5\n      parent: 1\n";

        private static string Manifest(string proto) => new DrydockManifest
        {
            Entries =
            {
                new DrydockManifestEntry { Proto = "" },
                new DrydockManifestEntry { Proto = proto, Stack = 3 },
            },
        }.Serialize();

        private static Task FileDocument(DrydockStore store, Guid ship, Guid owner, string name, string yaml, string manifest, byte[]? capturedKeyHash = null, string? sizeClass = nameof(ShipSizeClass.Cutter))
        {
            var bytes = Encoding.UTF8.GetBytes(yaml);
            var (fingerprint, engineFormat) = DrydockSystem.ReadDriftMetadata(yaml);

            return store.FileRevision(new DrydockRevisionRequest
            {
                ShipGuid = ship,
                OwnerUserId = owner,
                ShipName = name,
                SizeClass = sizeClass,
                Kind = DrydockRevisionKind.PlayerStore,
                MarkStored = true,
                ActorUserId = owner,
                EngineFormatVer = engineFormat,
                ProtoFingerprint = fingerprint,
                CapturedKeyHash = capturedKeyHash ?? new byte[] { 4, 5, 6 },
                Checksum = SHA256.HashData(bytes),
                SizeBytes = bytes.Length,
                AppraisedValue = 24000,
                Manifest = manifest,
            }, DrydockSystem.CompressZstd(bytes), keepBlobs: 3);
        }

        /// <summary>The sweep's outcome for one ship, or null when the sweep never reached it: never the enum's default by accident.</summary>
        private static DrydockRebakeShipResult? Outcome(DrydockRebakeSweepReport? report, Guid ship) =>
            report != null && report.Ships.TryGetValue(ship, out var result) ? result : null;

        private static Task<int[]> Revisions(TestPair pair, Guid ship)
        {
            return pair.Server.ResolveDependency<IServerDbManager>().RunTriadDbCommand(async (context, token) =>
                await context.DrydockRevision.AsNoTracking()
                    .Where(r => r.ShipGuid == ship)
                    .Select(r => r.Revision)
                    .OrderBy(r => r)
                    .ToArrayAsync(token), CancellationToken.None);
        }

        private static Task<DrydockRevisionKind[]> Kinds(TestPair pair, Guid ship)
        {
            return pair.Server.ResolveDependency<IServerDbManager>().RunTriadDbCommand(async (context, token) =>
                await context.DrydockRevision.AsNoTracking()
                    .Where(r => r.ShipGuid == ship)
                    .OrderBy(r => r.Revision)
                    .Select(r => r.Kind)
                    .ToArrayAsync(token), CancellationToken.None);
        }

    }
}
