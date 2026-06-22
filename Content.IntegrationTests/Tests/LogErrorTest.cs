// Triad: removed - engine v277 reports failing logs at pair dispose (PoolTestLogHandler.FailingLogs)
// instead of throwing AssertionException at the log call site, so this test can no longer pass.
// Wizden removed it for the same reason in 03d5c4c685 (#43228); Monolith followed in the v277 merge.
// using Robust.Shared.Configuration;
// using Robust.Shared.Log;
// using Robust.UnitTesting;
//
// namespace Content.IntegrationTests.Tests;
//
// public sealed class LogErrorTest
// {
//     /// <summary>
//     ///     This test ensures that error logs cause tests to fail.
//     /// </summary>
//     [Test]
//     public async Task TestLogErrorCausesTestFailure()
//     {
//         await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
//         var server = pair.Server;
//         var client = pair.Client;
//
//         var cfg = server.ResolveDependency<IConfigurationManager>();
//         var logmill = server.ResolveDependency<ILogManager>().RootSawmill;
//
//         // Default cvar is properly configured
//         Assert.That(cfg.GetCVar(RTCVars.FailureLogLevel), Is.EqualTo(LogLevel.Error));
//
//         // Warnings don't cause tests to fail.
//         await server.WaitPost(() => logmill.Warning("test"));
//
//         // But errors do
//         await server.WaitPost(() => Assert.Throws<AssertionException>(() => logmill.Error("test")));
//         await client.WaitPost(() => Assert.Throws<AssertionException>(() => logmill.Error("test")));
//
//         await pair.CleanReturnAsync();
//     }
// }
