#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._Triad.Drydock;
using Content.Server.Database;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.ShipSize;
using Content.Shared.Damage;
using Content.Shared.Stacks;
using Content.Shared.VendingMachines;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// One frozen fixture: a compressed document exactly as the drydock filed it, and the sidecar that
    /// carries the revision row a retrieve reads beside it. The pair lives under <c>GoldenCorpus/</c>
    /// as <c>&lt;name&gt;.blob.zst</c> and <c>&lt;name&gt;.json</c>, embedded into the test assembly so
    /// the gate finds them wherever the tests run.
    /// </summary>
    public sealed class GoldenFixture
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("vesselProto")]
        public string? VesselProto { get; set; }

        [JsonPropertyName("shipName")]
        public string ShipName { get; set; } = string.Empty;

        [JsonPropertyName("sizeClass")]
        public string? SizeClass { get; set; }

        /// <summary>The named mutations applied to the hull before it was stored.</summary>
        [JsonPropertyName("recipes")]
        public List<string> Recipes { get; set; } = new();

        [JsonPropertyName("generatedAtCommit")]
        public string GeneratedAtCommit { get; set; } = string.Empty;

        [JsonPropertyName("generatedAtUtc")]
        public string GeneratedAtUtc { get; set; } = string.Empty;

        /// <summary>SHA-256 of the compressed blob file, so a fixture that rotted on disk fails as that.</summary>
        [JsonPropertyName("blobFileSha256")]
        public string BlobFileSha256 { get; set; } = string.Empty;

        [JsonPropertyName("blobFileBytes")]
        public int BlobFileBytes { get; set; }

        [JsonPropertyName("revision")]
        public GoldenRevision Revision { get; set; } = new();

        /// <summary>Recipe-specific facts the manifest cannot carry, checked on the reborn hull.</summary>
        [JsonPropertyName("probes")]
        public List<GoldenProbe> Probes { get; set; } = new();

        /// <summary>The compressed document. Not in the sidecar; filled from the blob file.</summary>
        [JsonIgnore]
        public byte[] Blob { get; set; } = Array.Empty<byte>();

        public GoldenFixture Clone()
        {
            var copy = JsonSerializer.Deserialize<GoldenFixture>(JsonSerializer.Serialize(this, GoldenCorpus.Json), GoldenCorpus.Json)!;
            copy.Blob = (byte[]) Blob.Clone();
            return copy;
        }
    }

    /// <summary>The <c>drydock_revision</c> columns a retrieve reads, as filed.</summary>
    public sealed class GoldenRevision
    {
        [JsonPropertyName("checksum")]
        public string Checksum { get; set; } = string.Empty;

        [JsonPropertyName("sizeBytes")]
        public int SizeBytes { get; set; }

        [JsonPropertyName("engineFormatVer")]
        public int EngineFormatVer { get; set; }

        [JsonPropertyName("drydockFormatVer")]
        public int DrydockFormatVer { get; set; }

        [JsonPropertyName("protoFingerprint")]
        public string ProtoFingerprint { get; set; } = string.Empty;

        [JsonPropertyName("capturedKeyHash")]
        public string CapturedKeyHash { get; set; } = string.Empty;

        [JsonPropertyName("appraisedValue")]
        public int? AppraisedValue { get; set; }

        /// <summary>The manifest column byte for byte, which is why it is a string and not an object.</summary>
        [JsonPropertyName("manifest")]
        public string Manifest { get; set; } = string.Empty;
    }

    /// <summary>
    /// A fact a recipe left on one entity. Located by prototype and grid-local position, because
    /// neither an entity uid nor a manifest index survives a round trip and an anchored machine's
    /// tile does.
    /// </summary>
    public sealed class GoldenProbe
    {
        public const string Damage = "damage";
        public const string VendorInventory = "vendorInventory";
        public const string LatheQueue = "latheQueue";
        public const string ContainedStack = "containedStack";
        public const string GunAmmo = "gunAmmo";

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("proto")]
        public string Proto { get; set; } = string.Empty;

        [JsonPropertyName("x")]
        public float X { get; set; }

        [JsonPropertyName("y")]
        public float Y { get; set; }

        /// <summary>A damage total, a stack count or an ammo count, depending on the kind.</summary>
        [JsonPropertyName("value")]
        public float Value { get; set; }

        /// <summary>The recipe id, or the stacked item's prototype.</summary>
        [JsonPropertyName("item")]
        public string? Item { get; set; }

        [JsonPropertyName("requested")]
        public int Requested { get; set; }

        [JsonPropertyName("printed")]
        public int Printed { get; set; }

        [JsonPropertyName("inventory")]
        public Dictionary<string, uint>? Inventory { get; set; }
    }

    /// <summary>
    /// What one verification found, split by the assertion it belongs to so a control can prove the
    /// right one went red and the others did not.
    /// </summary>
    public sealed class GoldenReport
    {
        public readonly string Fixture;
        public readonly List<string> Load = new();
        public readonly List<string> Census = new();
        public readonly List<string> Values = new();
        public readonly List<string> Probes = new();
        public readonly List<string> Restore = new();

        /// <summary>Differences read and judged legitimate. Printed, never failed on.</summary>
        public readonly List<string> Notes = new();

        public int RebornEntities;
        public double Seconds;

        public GoldenReport(string fixture)
        {
            Fixture = fixture;
        }

        public bool Passed => Load.Count + Census.Count + Values.Count + Probes.Count + Restore.Count == 0;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"fixture '{Fixture}': {(Passed ? "passed" : "FAILED")} ({RebornEntities} reborn entities, {Seconds:F1}s)");
            Section(sb, "(a) load", Load);
            Section(sb, "(b) census", Census);
            Section(sb, "(c) damage and stacks", Values);
            Section(sb, "probes", Probes);
            Section(sb, "(d) fresh store", Restore);
            Section(sb, "notes", Notes);
            return sb.ToString();

            static void Section(StringBuilder sb, string title, List<string> lines)
            {
                if (lines.Count == 0)
                    return;

                sb.Append(Environment.NewLine).Append("  ").Append(title).Append(':');
                foreach (var line in lines)
                    sb.Append(Environment.NewLine).Append("    ").Append(line);
            }
        }
    }

    /// <summary>
    /// The golden corpus: discovery, the canonical manifest shape, and the verification a fixture has
    /// to pass. Shared by the gate, its controls, and the refresh path that writes the fixtures.
    /// </summary>
    public static class GoldenCorpus
    {
        /// <summary>
        /// How many fixtures are committed. The gate refuses to pass on fewer, so a corpus that stopped
        /// being embedded, or a directory somebody emptied, fails instead of verifying nothing.
        /// </summary>
        public const int CommittedFixtures = 3;

        /// <summary>Where the embedded resources are named, by the csproj's LogicalName.</summary>
        public const string ResourcePrefix = "DrydockGoldenCorpus/";

        /// <summary>Relative to the repository root, for the refresh path that writes them.</summary>
        public const string SourceDirectory = "Content.IntegrationTests/Tests/_Triad/Drydock/GoldenCorpus";

        /// <summary>
        /// Indented, defaults omitted, and quotes left unescaped, so the manifest string inside a sidecar
        /// still reads as JSON in a review diff rather than as a wall of <c>"</c>.
        /// </summary>
        public static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>Every fixture embedded in this assembly, in name order.</summary>
        public static List<GoldenFixture> Discover()
        {
            var assembly = typeof(GoldenCorpus).Assembly;
            var fixtures = new List<GoldenFixture>();

            foreach (var resource in assembly.GetManifestResourceNames()
                         .Where(r => r.StartsWith(ResourcePrefix, StringComparison.Ordinal) && r.EndsWith(".json", StringComparison.Ordinal))
                         .OrderBy(r => r, StringComparer.Ordinal))
            {
                var stem = resource[..^".json".Length];
                var fixture = JsonSerializer.Deserialize<GoldenFixture>(ReadResource(assembly, resource), Json)
                              ?? throw new InvalidDataException($"{resource} is not a golden fixture sidecar.");
                fixture.Blob = ReadResource(assembly, stem + ".blob.zst");
                fixtures.Add(fixture);
            }

            return fixtures;
        }

        private static byte[] ReadResource(Assembly assembly, string name)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                               ?? throw new FileNotFoundException($"Embedded golden corpus resource {name} is missing: a sidecar without its blob, or the reverse.");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        /// <summary>
        /// The pair-wide setup every verification needs: the drydock switched on with slicing off, the
        /// shipyard stood up, and a station on a test grid to dock at, made immune to the janitor.
        /// </summary>
        public static async Task<(Guid Owner, EntityUid Station)> PrepareHarness(TestPair pair, int berths)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var db = server.ResolveDependency<IServerDbManager>();
            var store = server.ResolveDependency<DrydockStore>();
            var cfg = server.ResolveDependency<IConfigurationManager>();

            var owner = Guid.NewGuid();
            await DrydockTestHelpers.InsertPlayer(db, owner);

            // A fixture is filed into one berth and its fresh store takes one again after the retrieve
            // vacates it, so one per fixture is enough; the spare keeps one failed retrieve from turning
            // every later fixture into a capacity refusal.
            for (var i = 0; i < berths; i++)
                await store.AddBerth(owner, ShipSizeClass.SuperCapital, DrydockBerthKind.Granted, 0, null, null);

            var map = await pair.CreateTestMap();
            EntityUid station = default;

            await server.WaitPost(() =>
            {
                cfg.SetCVar(TriadCCVars.DrydockEnabled, true);
                cfg.SetCVar(TriadCCVars.DrydockReadOnly, false);
                cfg.SetCVar(TriadCCVars.DrydockTickBudgetMs, 0);

                server.System<ShipyardSystem>().SetupShipyardIfNeeded();

                station = entMan.Spawn();
                entMan.AddComponent<StationDataComponent>(station);
                server.System<StationSystem>().AddGridToStation(station, map.Grid.Owner);
            });

            await pair.MakeCleanupImmune(map.Grid.Owner);
            await pair.RunTicksSync(5);

            return (owner, station);
        }

        /// <summary>
        /// Files <paramref name="fixture"/> as a fresh hull, retrieves it through the real pipeline,
        /// compares the reborn grid with the fixture's own record, then stores the reborn grid again and
        /// compares the manifest that store writes. Never asserts on what it compares: it reports, so the
        /// gate can name every failing fixture at once and a control can check which assertion went red.
        /// A pipeline operation that never completes fails the test through
        /// <see cref="DrydockTestHelpers.RunOnServer{T}"/>'s own assertion.
        /// </summary>
        /// <param name="onReborn">
        /// Runs on the game thread with the reborn grid before it is stored again. The refresh path uses
        /// it to read the fidelity oracle; the gate passes nothing.
        /// </param>
        public static async Task<GoldenReport> Verify(
            TestPair pair,
            GoldenFixture fixture,
            Guid owner,
            EntityUid station,
            Action<EntityUid>? onReborn = null)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var store = server.ResolveDependency<DrydockStore>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var drydock = server.System<DrydockSystem>();
            var report = new GoldenReport(fixture.Name);
            var clock = System.Diagnostics.Stopwatch.StartNew();

            // Every error the server logs while this fixture is in flight is this fixture's. Read off the
            // pair's own failing-log list rather than left to the pair's return, which could only say
            // that some fixture logged one. The pair still fails on them as well, which is right for the
            // gate; a control that expects errors lowers the failure level around its own call.
            var logsBefore = pair.ServerLogHandler.FailingLogs.Count;

            try
            {
                if (Sha256(fixture.Blob) != fixture.BlobFileSha256)
                {
                    report.Load.Add("the blob file no longer hashes to the value its sidecar recorded: the fixture itself rotted or was edited");
                    return report;
                }

                var expected = DrydockManifest.Deserialize(fixture.Revision.Manifest);
                if (expected == null || expected.Entries.Count == 0)
                {
                    report.Load.Add("the sidecar's manifest does not parse, or is empty");
                    return report;
                }

                // A fresh hull id every run: pooled pairs share one database, so the fixture's own id may
                // already be filed by an earlier run of this very test.
                var shipId = Guid.NewGuid();
                var filed = await store.FileRevision(new DrydockRevisionRequest
                {
                    ShipGuid = shipId,
                    OwnerUserId = owner,
                    ShipName = fixture.ShipName,
                    VesselProto = fixture.VesselProto,
                    SizeClass = fixture.SizeClass,
                    Kind = DrydockRevisionKind.PlayerStore,
                    MarkStored = true,
                    ActorUserId = owner,
                    CreatedRoundId = null,
                    EngineFormatVer = fixture.Revision.EngineFormatVer,
                    DrydockFormatVer = fixture.Revision.DrydockFormatVer,
                    ProtoFingerprint = Convert.FromBase64String(fixture.Revision.ProtoFingerprint),
                    CapturedKeyHash = Convert.FromBase64String(fixture.Revision.CapturedKeyHash),
                    Checksum = Convert.FromBase64String(fixture.Revision.Checksum),
                    SizeBytes = fixture.Revision.SizeBytes,
                    AppraisedValue = fixture.Revision.AppraisedValue,
                    Manifest = fixture.Revision.Manifest,
                }, fixture.Blob, keepBlobs: 0);

                if (filed.Outcome != DrydockBerthResult.Success)
                {
                    report.Load.Add($"the fixture could not be filed into the test database ({filed.Outcome}); this is the harness, not the drydock");
                    return report;
                }

                var retrieved = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryRetrieveShip(shipId, owner, station, null), OperationTimeout);
                if (!retrieved.Succeeded || retrieved.Grid is not { } grid)
                {
                    report.Load.Add($"retrieve refused with {retrieved.Result}");
                    return report;
                }

                // One tick so anything queued by the dock lands, and no more: the comparison is against
                // the moment of store, and a docked ship left to simulate starts to disagree with it.
                await pair.RunTicksSync(1);

                List<GoldenRecord> reborn = default!;
                await server.WaitPost(() =>
                {
                    reborn = WalkLive(entMan, grid);
                    CheckProbes(entMan, server.System<SharedTransformSystem>(), grid, fixture.Probes, report.Probes);
                    onReborn?.Invoke(grid);
                });

                report.RebornEntities = reborn.Count;
                var recorded = FromManifest(expected, UnsavableIn(protoMan), report.Notes);

                CompareCensus(recorded, reborn, report.Census);
                CompareValues(recorded, reborn, report.Values);

                // The fresh store. The reborn grid still carries the id stamped into its document, which
                // names the hull the fixture was generated as; restamped to the hull this run filed, so
                // the store lands a second revision on the same row the way a player's would.
                await server.WaitPost(() => entMan.EnsureComponent<DrydockIdentityComponent>(grid).ShipId = shipId);

                var (restored, restoredId) = await DrydockTestHelpers.RunOnServer(pair, () => drydock.TryStoreShip(grid, owner, null), OperationTimeout);
                if (restored != DrydockStoreResult.Success || restoredId != shipId)
                {
                    report.Restore.Add($"the reborn grid would not store again ({restored}, filed as {restoredId})");
                    await server.WaitPost(() =>
                    {
                        if (entMan.EntityExists(grid))
                            entMan.DeleteEntity(grid);
                    });
                    return report;
                }

                await pair.RunTicksSync(1);

                var current = await store.LoadCurrent(shipId);
                if (current == null || current.Revision.Revision != filed.Revision + 1)
                {
                    report.Restore.Add($"the fresh store filed no second revision (current {current?.Revision.Revision})");
                    return report;
                }

                var fresh = DrydockManifest.Deserialize(current.Revision.Manifest);
                if (fresh == null)
                {
                    report.Restore.Add("the fresh store's manifest does not parse");
                    return report;
                }

                CompareManifests(expected, fresh, UnsavableIn(protoMan), report.Restore);

                if (Convert.ToBase64String(current.Revision.ProtoFingerprint) != fixture.Revision.ProtoFingerprint)
                    report.Restore.Add("the prototype fingerprint moved: the reborn document references a different prototype set");

                if (Convert.ToBase64String(current.Revision.CapturedKeyHash) != fixture.Revision.CapturedKeyHash)
                    report.Restore.Add("the captured-key hash moved: the fidelity capture wrote a different key set");

                // Both format versions are the code's own constants at store time, so a newer build moving
                // them is the bump working, not the fixture failing. Read, printed, and left to the
                // re-bake ladder.
                if (current.Revision.EngineFormatVer != fixture.Revision.EngineFormatVer)
                    report.Notes.Add($"engine map format {fixture.Revision.EngineFormatVer} -> {current.Revision.EngineFormatVer}");

                if (current.Revision.DrydockFormatVer != fixture.Revision.DrydockFormatVer)
                    report.Notes.Add($"drydock format {fixture.Revision.DrydockFormatVer} -> {current.Revision.DrydockFormatVer}");

                // The document itself is not compared: it carries entity uids assigned at load and clock
                // offsets taken at store, so two stores of one hull never agree byte for byte. The size
                // is printed so a store that suddenly writes far more or less is visible.
                report.Notes.Add($"document {fixture.Revision.SizeBytes} -> {current.Revision.SizeBytes} bytes uncompressed");

                if (!SameOrder(expected, fresh))
                    report.Notes.Add("manifest entry order differs (walk order follows the transform child set, which reload rebuilds); compared as a tree instead");

                return report;
            }
            finally
            {
                foreach (var error in pair.ServerLogHandler.FailingLogs.Skip(logsBefore))
                    report.Load.Add($"logged {error}");

                report.Seconds = clock.Elapsed.TotalSeconds;
            }
        }

        /// <summary>
        /// The wall-clock bound on one pipeline operation against a corpus hull, passed to
        /// <see cref="DrydockTestHelpers.RunOnServer{T}"/>.
        /// </summary>
        public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(120);

        /// <summary>One entity, in the terms a manifest can state and a reborn grid can be read in.</summary>
        public readonly record struct GoldenRecord(string Path, string Proto, float Damage, int Stack, string Keys)
        {
            /// <summary>The (c) key: where the entity sits and what it carries.</summary>
            public string ValueKey => $"{Path} damage={Damage:F2} stack={Stack}";

            /// <summary>The (d) key: (c) plus the captured-state keys.</summary>
            public string FullKey => Keys.Length == 0 ? ValueKey : $"{ValueKey} keys=[{Keys}]";
        }

        /// <summary>
        /// Reads a manifest into records. An entry whose prototype is currently <c>save: false</c> is
        /// dropped with its subtree, because the manifest walks the live tree and the serializer never
        /// writes such an entity, so it can never be reborn: a sound in the air at the instant of the
        /// store is the case this exists for. A prototype that no longer exists at all is kept, and
        /// fails, because that is drift.
        /// </summary>
        public static List<GoldenRecord> FromManifest(DrydockManifest manifest, Func<string, bool> isUnsavable, List<string>? notes)
        {
            var records = new List<GoldenRecord>();
            var paths = new string?[manifest.Entries.Count];
            var skipped = 0;

            for (var i = 0; i < manifest.Entries.Count; i++)
            {
                var entry = manifest.Entries[i];

                // Parents always precede their children in the walk, so a null here means the parent
                // was dropped and this entry goes with it.
                string? parentPath = null;
                if (entry.Parent is { } parent)
                {
                    parentPath = parent >= 0 && parent < i ? paths[parent] : null;
                    if (parentPath == null)
                    {
                        skipped++;
                        continue;
                    }
                }

                if (isUnsavable(entry.Proto))
                {
                    skipped++;
                    continue;
                }

                var path = parentPath == null ? entry.Proto : $"{parentPath}/{entry.Proto}";
                paths[i] = path;

                var keys = entry.CapturedKeys == null
                    ? string.Empty
                    : string.Join(",", entry.CapturedKeys.OrderBy(k => k, StringComparer.Ordinal));

                records.Add(new GoldenRecord(path, entry.Proto, entry.Damage, entry.Stack, keys));
            }

            if (skipped > 0)
                notes?.Add($"{skipped} manifest entr{(skipped == 1 ? "y" : "ies")} skipped as save: false (never written, so never reborn)");

            return records;
        }

        /// <summary>The live counterpart of <see cref="FromManifest"/>: the whole tree under the grid, grid included.</summary>
        public static List<GoldenRecord> WalkLive(IEntityManager entMan, EntityUid grid)
        {
            var records = new List<GoldenRecord>();
            var stack = new Stack<(EntityUid Uid, string? ParentPath)>();
            stack.Push((grid, null));

            while (stack.Count > 0)
            {
                var (uid, parentPath) = stack.Pop();
                var meta = entMan.GetComponent<MetaDataComponent>(uid);

                if (meta.EntityPrototype?.MapSavable == false)
                    continue;

                var proto = meta.EntityPrototype?.ID ?? string.Empty;
                var path = parentPath == null ? proto : $"{parentPath}/{proto}";

                var damage = entMan.TryGetComponent<DamageableComponent>(uid, out var damageable)
                    ? (float) damageable.TotalDamage
                    : 0f;

                var count = entMan.TryGetComponent<StackComponent>(uid, out var stackComp) ? stackComp.Count : 0;

                records.Add(new GoldenRecord(path, proto, damage, count, string.Empty));

                var children = entMan.GetComponent<TransformComponent>(uid).ChildEnumerator;
                while (children.MoveNext(out var child))
                    stack.Push((child, path));
            }

            return records;
        }

        /// <summary>(b): the whole-tree count, then the per-prototype multiset.</summary>
        public static void CompareCensus(List<GoldenRecord> recorded, List<GoldenRecord> reborn, List<string> failures)
        {
            if (recorded.Count != reborn.Count)
                failures.Add($"whole-tree entity count: manifest {recorded.Count}, reborn {reborn.Count}");

            DiffMultiset(
                recorded.Select(r => r.Proto.Length == 0 ? "<no prototype>" : r.Proto),
                reborn.Select(r => r.Proto.Length == 0 ? "<no prototype>" : r.Proto),
                "prototype", failures);
        }

        /// <summary>(c): every entity's place in the tree with its damage total and stack count.</summary>
        public static void CompareValues(List<GoldenRecord> recorded, List<GoldenRecord> reborn, List<string> failures)
        {
            DiffMultiset(recorded.Select(r => r.ValueKey), reborn.Select(r => r.ValueKey), "entity", failures);
        }

        /// <summary>
        /// (d): two manifests compared as trees. Entry order is a walk over the transform child set,
        /// which a reload rebuilds, so indices are not comparable across a round trip and paths are.
        /// </summary>
        public static void CompareManifests(
            DrydockManifest expected, DrydockManifest fresh, Func<string, bool> isUnsavable, List<string> failures)
        {
            if (expected.Version != fresh.Version)
                failures.Add($"manifest version {expected.Version} -> {fresh.Version}");

            // Filtered on both sides, because both walks are of a live tree: the store plays its
            // departure sound at the hull, and whether that sound is still a grid child when the manifest
            // walk reaches it is timing, not content.
            var want = FromManifest(expected, isUnsavable, null);
            var got = FromManifest(fresh, isUnsavable, null);

            if (want.Count != got.Count)
                failures.Add($"manifest entry count: fixture {want.Count}, fresh store {got.Count}");

            // Captured-state keys included: the key set is persisted and feeds the captured-key hash, so
            // it has to be a function of the hull, and this is where a store that disagrees with itself
            // shows up.
            DiffMultiset(want.Select(r => r.FullKey), got.Select(r => r.FullKey), "manifest entry", failures);
        }

        private static bool SameOrder(DrydockManifest a, DrydockManifest b)
        {
            if (a.Entries.Count != b.Entries.Count)
                return false;

            for (var i = 0; i < a.Entries.Count; i++)
            {
                if (a.Entries[i].Proto != b.Entries[i].Proto || a.Entries[i].Parent != b.Entries[i].Parent)
                    return false;
            }

            return true;
        }

        /// <summary>Adds one line per differing key, the first ten by name, and a count of the rest.</summary>
        private static void DiffMultiset(IEnumerable<string> expected, IEnumerable<string> actual, string noun, List<string> failures)
        {
            var want = expected.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
            var got = actual.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());

            var differing = want.Keys.Union(got.Keys)
                .Select(k => (Key: k, Want: want.GetValueOrDefault(k), Got: got.GetValueOrDefault(k)))
                .Where(d => d.Want != d.Got)
                .OrderBy(d => d.Key, StringComparer.Ordinal)
                .ToList();

            foreach (var (key, w, g) in differing.Take(10))
                failures.Add($"{noun} '{key}': fixture {w}, now {g}");

            if (differing.Count > 10)
                failures.Add($"and {differing.Count - 10} more differing {noun} keys");
        }

        private static void CheckProbes(
            IEntityManager entMan, SharedTransformSystem xform, EntityUid grid, List<GoldenProbe> probes, List<string> failures)
        {
            foreach (var probe in probes)
            {
                var target = FindProbeTarget(entMan, grid, probe);
                if (target == null)
                {
                    failures.Add($"{probe.Kind} on {probe.Proto} at ({probe.X}, {probe.Y}): no such entity on the reborn grid");
                    continue;
                }

                var uid = target.Value;
                var label = $"{probe.Kind} on {probe.Proto} at ({probe.X}, {probe.Y})";

                switch (probe.Kind)
                {
                    case GoldenProbe.Damage:
                    {
                        var total = entMan.TryGetComponent<DamageableComponent>(uid, out var damageable) ? (float) damageable.TotalDamage : 0f;
                        if (MathF.Abs(total - probe.Value) > 0.005f)
                            failures.Add($"{label}: damage {total}, fixture {probe.Value}");
                        break;
                    }
                    case GoldenProbe.VendorInventory:
                    {
                        if (!entMan.TryGetComponent<VendingMachineComponent>(uid, out var vendor))
                        {
                            failures.Add($"{label}: no vending machine component");
                            break;
                        }

                        foreach (var (item, amount) in probe.Inventory ?? new())
                        {
                            var have = vendor.Inventory.TryGetValue(item, out var entry) ? entry.Amount : 0u;
                            if (have != amount)
                                failures.Add($"{label}: {item} stocks {have}, fixture {amount}");
                        }

                        break;
                    }
                    case GoldenProbe.LatheQueue:
                    {
                        if (!entMan.TryGetComponent<Content.Shared.Lathe.LatheComponent>(uid, out var lathe))
                        {
                            failures.Add($"{label}: no lathe component");
                            break;
                        }

                        var batch = lathe.Queue.FirstOrDefault(b => b.Recipe.ID == probe.Item);
                        if (batch == null)
                            failures.Add($"{label}: no queued batch of {probe.Item} (queue holds {lathe.Queue.Count})");
                        else if (batch.ItemsRequested != probe.Requested || batch.ItemsPrinted != probe.Printed)
                            failures.Add($"{label}: {probe.Item} {batch.ItemsPrinted}/{batch.ItemsRequested}, fixture {probe.Printed}/{probe.Requested}");

                        break;
                    }
                    case GoldenProbe.ContainedStack:
                    {
                        var found = false;
                        var children = entMan.GetComponent<TransformComponent>(uid).ChildEnumerator;
                        while (children.MoveNext(out var child))
                        {
                            if (entMan.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID != probe.Item
                                || !entMan.TryGetComponent<StackComponent>(child, out var stackComp)
                                || stackComp.Count != (int) probe.Value)
                            {
                                continue;
                            }

                            found = true;
                            break;
                        }

                        if (!found)
                            failures.Add($"{label}: no {probe.Item} stack of {(int) probe.Value} inside it");

                        break;
                    }
                    case GoldenProbe.GunAmmo:
                    {
                        var ev = new GetAmmoCountEvent();
                        entMan.EventBus.RaiseLocalEvent(uid, ref ev);
                        if (ev.Count != (int) probe.Value)
                            failures.Add($"{label}: {ev.Count} rounds, fixture {(int) probe.Value}");
                        break;
                    }
                    default:
                        failures.Add($"{label}: unknown probe kind");
                        break;
                }
            }
        }

        /// <summary>A direct grid child of the probe's prototype, on the probe's tile.</summary>
        public static EntityUid? FindProbeTarget(IEntityManager entMan, EntityUid grid, GoldenProbe probe)
        {
            var children = entMan.GetComponent<TransformComponent>(grid).ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                if (entMan.GetComponent<MetaDataComponent>(child).EntityPrototype?.ID != probe.Proto)
                    continue;

                var pos = entMan.GetComponent<TransformComponent>(child).LocalPosition;
                if (MathF.Abs(pos.X - probe.X) < 0.05f && MathF.Abs(pos.Y - probe.Y) < 0.05f)
                    return child;
            }

            return null;
        }

        public static string Sha256(byte[] bytes) => Convert.ToBase64String(SHA256.HashData(bytes));

        /// <summary>Whether a prototype id is currently <c>save: false</c>. An unknown id is not.</summary>
        public static Func<string, bool> UnsavableIn(IPrototypeManager protoMan) =>
            id => id.Length > 0 && protoMan.TryIndex<EntityPrototype>(id, out var proto) && !proto.MapSavable;
    }
}
