#nullable enable

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The golden corpus gate: every frozen fixture still materializes on today's code, its reborn hull
    /// matches the manifest it was filed with, and storing that hull again writes the same manifest.
    /// This is the CI gate for upstream merges and engine bumps, because every durability promise the
    /// drydock makes reduces to "old blobs still load on new code", and a fixture that is regenerated
    /// on every run cannot measure that.
    ///
    /// <para>The fixtures are real roster hulls put through named recipes and filed by the real store,
    /// written only by <see cref="DrydockGoldenCorpusRefreshLocalTest"/>. The gate files each one into
    /// the test database under a fresh hull id, retrieves it through the whole pipeline including the
    /// dock, and reads the reborn grid before it has simulated more than a tick.</para>
    ///
    /// <para>What is compared, and against what, is in <see cref="GoldenCorpus.Verify"/>. What is
    /// deliberately not compared: the document bytes (entity uids and clock offsets differ on every
    /// store of one hull), the manifest's entry order (it follows the transform child set a reload
    /// rebuilds, so the manifest is compared as a tree), and the two format versions (constants of the
    /// storing code, so a newer build moving them is a bump rather than a failure). All three are
    /// printed.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockSystem))]
    public sealed class DrydockGoldenCorpusTest
    {
        [Test]
        public async Task EveryGoldenFixtureStillMaterializesAndMatchesItsManifest()
        {
            var fixtures = GoldenCorpus.Discover();

            // The control on discovery: a corpus that stopped being embedded, or a directory somebody
            // emptied, would otherwise verify nothing and pass.
            Assert.That(fixtures, Has.Count.GreaterThanOrEqualTo(GoldenCorpus.CommittedFixtures),
                $"Found {fixtures.Count} golden fixture(s), fewer than the {GoldenCorpus.CommittedFixtures} committed: "
                + string.Join(", ", fixtures.Select(f => f.Name)));

            await using var pair = await PoolManager.GetServerClient();
            var (owner, station) = await GoldenCorpus.PrepareHarness(pair, fixtures.Count + 1);

            var failed = 0;
            var summary = new StringBuilder();

            foreach (var fixture in fixtures)
            {
                var report = await GoldenCorpus.Verify(pair, fixture, owner, station);
                await TestContext.Out.WriteLineAsync($"[golden-corpus] {report}");

                if (report.Passed)
                    continue;

                failed++;
                summary.Append(Environment.NewLine).Append(report);
            }

            Assert.That(failed, Is.Zero,
                $"{failed} of {fixtures.Count} golden fixture(s) no longer round-trip on this code:{summary}");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The gate going red, once per assertion, on corrupted copies of committed fixtures. Each
        /// corruption is aimed at one assertion and the check is that that one fails; the stack flip
        /// also checks the census stays green, so the value comparison is proven to see what the census
        /// cannot. Corrupted in memory, never on disk, and the checksum is recomputed over the corrupted
        /// document so the retrieve's own integrity check does not catch it first.
        /// </summary>
        [Test]
        public async Task TheGateGoesRedOnACorruptedFixture()
        {
            var fixtures = GoldenCorpus.Discover();
            Assert.That(fixtures, Is.Not.Empty, "The control needs a fixture to corrupt.");

            // The stack flip needs the cargo recipe's stack; the smallest such fixture keeps this cheap.
            var basis = fixtures
                .Where(f => f.Recipes.Contains("cargo-into-locker"))
                .OrderBy(f => f.Revision.SizeBytes)
                .First();

            await using var pair = await PoolManager.GetServerClient();
            var (owner, station) = await GoldenCorpus.PrepareHarness(pair, 4);

            // A reinforced or plain wall entity is referenced by nothing else in the document, so taking
            // one out cannot break the load; it can only leave the reborn hull one short.
            var dropped = Corrupt(basis, yaml => DropOneEntity(yaml, "WallReinforced"));
            var droppedReport = await GoldenCorpus.Verify(pair, dropped, owner, station);
            await TestContext.Out.WriteLineAsync($"[golden-control] dropped entity: {droppedReport}");

            // The only control that is meant to log errors, so the only one run with the pair's failure
            // level lowered. What turns (a) red here is therefore the refused retrieve itself, which is
            // the stronger half of the assertion: the error lines are gone from the report with the level.
            var missing = Corrupt(basis, yaml => RenameProtoGroup(yaml, "WallReinforced", "GoldenCorpusControlNoSuchPrototype"));
            var missingReport = await DrydockTestHelpers.Quietly(pair, () => GoldenCorpus.Verify(pair, missing, owner, station));
            await TestContext.Out.WriteLineAsync($"[golden-control] missing prototype: {missingReport}");

            var flipped = Corrupt(basis, yaml => FlipStackCount(yaml, "SheetSteel", 17, 16));
            var flippedReport = await GoldenCorpus.Verify(pair, flipped, owner, station);
            await TestContext.Out.WriteLineAsync($"[golden-control] flipped stack count: {flippedReport}");

            Assert.Multiple(() =>
            {
                Assert.That(droppedReport.Census, Is.Not.Empty, "(b) must go red on a hull that comes back one entity short.");
                Assert.That(droppedReport.Restore, Is.Not.Empty, "(d) must go red too: the fresh store walks the short hull.");

                Assert.That(missingReport.Load, Is.Not.Empty, "(a) must go red on a document naming a prototype that does not exist.");

                Assert.That(flippedReport.Values, Is.Not.Empty, "(c) must go red on a stack that comes back with a different count.");
                Assert.That(flippedReport.Census, Is.Empty, "The census cannot see a stack count, so a red census here would mean the corruption hit more than the count.");
                Assert.That(flippedReport.Load, Is.Empty, "The flipped document still loads cleanly.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>A copy of <paramref name="basis"/> whose document has been edited, with its checksum and size recomputed.</summary>
        private static GoldenFixture Corrupt(GoldenFixture basis, Func<string, string> edit)
        {
            var copy = basis.Clone();
            // The document's line endings are the storing machine's, so they are normalized for the
            // edits below; the loader reads either.
            var yaml = Encoding.UTF8.GetString(DrydockSystem.DecompressZstd(copy.Blob)).Replace("\r\n", "\n");
            var edited = edit(yaml);
            Assert.That(edited, Is.Not.EqualTo(yaml), "The corruption changed nothing, so the control would prove nothing.");

            var bytes = Encoding.UTF8.GetBytes(edited);
            copy.Blob = DrydockSystem.CompressZstd(bytes);
            copy.BlobFileSha256 = GoldenCorpus.Sha256(copy.Blob);
            copy.BlobFileBytes = copy.Blob.Length;
            copy.Revision.Checksum = Convert.ToBase64String(SHA256.HashData(bytes));
            copy.Revision.SizeBytes = bytes.Length;
            copy.Name = basis.Name + " (corrupted)";
            return copy;
        }

        /// <summary>Removes the first entity of a prototype group that has more than one.</summary>
        private static string DropOneEntity(string yaml, string proto)
        {
            var group = new Regex($@"(?m)^- proto: {Regex.Escape(proto)}\n  entities:\n(?<first>  - uid: \d+\n(?:    .*\n)+)(?=  - uid: )").Match(yaml);
            Assert.That(group.Success, Is.True, $"No {proto} group with two or more entities to drop one from.");

            var first = group.Groups["first"];
            return yaml.Remove(first.Index, first.Length);
        }

        private static string RenameProtoGroup(string yaml, string proto, string replacement)
        {
            var header = new Regex($@"(?m)^- proto: {Regex.Escape(proto)}$");
            Assert.That(header.IsMatch(yaml), Is.True, $"No {proto} group to rename.");
            return header.Replace(yaml, $"- proto: {replacement}", 1);
        }

        /// <summary>Changes the count on the first entity of <paramref name="proto"/> whose stack holds <paramref name="from"/>.</summary>
        private static string FlipStackCount(string yaml, string proto, int from, int to)
        {
            var group = new Regex($@"(?ms)^- proto: {Regex.Escape(proto)}\n  entities:\n.*?(?=^- proto: |\z)").Match(yaml);
            Assert.That(group.Success, Is.True, $"No {proto} group in the document.");

            var count = new Regex($@"(?m)^(?<lead>\s+count: ){from}$").Match(group.Value);
            Assert.That(count.Success, Is.True, $"No {proto} stack of {from} in the document.");

            var at = group.Index + count.Index;
            return yaml.Remove(at, count.Length).Insert(at, count.Groups["lead"].Value + to);
        }
    }
}
