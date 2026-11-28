#nullable enable

using System.Linq;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Content.Shared._Triad.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Log;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The map-init transaction against the golden corpus. Report mode is the audit: it raises
    /// <c>MapInitEvent</c> on every entity of every golden hull and prints every persisted field the
    /// handlers rewrote, every entity they spawned and every one they deleted, with the damage left
    /// on the ship so the golden gate itself shows what an unguarded re-fire costs. Revert mode is
    /// the gate: the same hulls must still match their manifests afterwards.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockFidelitySystem))]
    public sealed class DrydockMapInitRefireTest
    {
        [Test]
        public async Task ReportModeNamesWhatMapInitRewrites()
        {
            await RunCorpus("report", requirePassed: false);
        }

        [Test]
        public async Task RevertModeKeepsTheGoldenCorpusRoundTripping()
        {
            await RunCorpus("revert", requirePassed: true);
        }

        private static async Task RunCorpus(string mode, bool requirePassed)
        {
            var fixtures = GoldenCorpus.Discover();
            Assert.That(fixtures, Is.Not.Empty, "No golden fixtures to fire map init on.");

            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var cfg = server.ResolveDependency<IConfigurationManager>();
            var fidelity = server.System<DrydockFidelitySystem>();
            var (owner, station) = await GoldenCorpus.PrepareHarness(pair, fixtures.Count + 1);

            await server.WaitPost(() => cfg.SetCVar(TriadCCVars.DrydockMapInitRefire, mode));

            var failed = 0;
            try
            {
                foreach (var fixture in fixtures)
                {
                    // Report mode lands the fire's damage on the ship, and a handler whose spawn
                    // has nowhere to go logs an error for it. Those errors are the finding, not a
                    // failure, so the pair's failure level is lifted around the retrieve. Revert
                    // mode keeps it: a retrieve that logs an error is a retrieve that will log it
                    // in production.
                    var failureLevel = pair.ServerLogHandler.FailureLevel;
                    if (!requirePassed)
                        pair.ServerLogHandler.FailureLevel = LogLevel.Fatal;

                    GoldenReport golden;
                    try
                    {
                        golden = await GoldenCorpus.Verify(pair, fixture, owner, station);
                    }
                    finally
                    {
                        pair.ServerLogHandler.FailureLevel = failureLevel;
                    }

                    var report = fidelity.LastMapInitReport;

                    await TestContext.Out.WriteLineAsync($"[mapinit-{mode}] {golden}");
                    Assert.That(report, Is.Not.Null, $"{fixture.Name}: the retrieve ran no map-init transaction.");
                    await TestContext.Out.WriteLineAsync($"[mapinit-{mode}] {fixture.Name}: {report!.Detail()}");

                    // The control on the transaction itself: it fired on the hull, not on a corner of it.
                    if (requirePassed)
                        Assert.That(golden.Load, Is.Empty, $"{fixture.Name}: the hull did not load cleanly: {string.Join("; ", golden.Load)}");
                    Assert.That(report.Entities, Is.GreaterThan(0), $"{fixture.Name}: nothing was snapshotted.");
                    Assert.That(report.Fired, Is.EqualTo(report.Entities), $"{fixture.Name}: {report.Fired} of {report.Entities} entities were fired.");
                    Assert.That(report.HandlerExceptions, Is.Empty,
                        $"{fixture.Name}: map-init handlers threw: {string.Join("; ", report.HandlerExceptions.Select(h => $"{h.Proto}: {h.Error}"))}");

                    if (requirePassed)
                    {
                        Assert.That(report.RevertFailures, Is.Empty,
                            $"{fixture.Name}: reverts failed: {string.Join("; ", report.RevertFailures.Select(r => $"{r.Key}: {r.Reason}"))}");
                        Assert.That(report.Vanished, Is.Empty, $"{fixture.Name}: map init deleted entities the document held: {string.Join(", ", report.Vanished.Keys)}");
                        Assert.That(report.ComponentsRemoved, Is.Empty, $"{fixture.Name}: map init removed components: {string.Join(", ", report.ComponentsRemoved.Keys)}");
                    }

                    if (!golden.Passed)
                        failed++;
                }

                if (requirePassed)
                    Assert.That(failed, Is.Zero, $"{failed} of {fixtures.Count} golden fixture(s) no longer round-trip with the map-init transaction reverting.");
            }
            finally
            {
                await server.WaitPost(() => cfg.SetCVar(TriadCCVars.DrydockMapInitRefire, "off"));
            }

            await pair.CleanReturnAsync();
        }
    }
}
