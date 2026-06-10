using System;
using System.Threading.Tasks;
using Content.Server._Triad.Shipyard;
using Content.Server.Database;
using Content.Shared._Triad.CCVar;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._Triad.Shipyard.TamperProtection;

[TestFixture]
public sealed class SaveLoadRoundTripTests
{
    [Test]
    public async Task SaveLoadRoundTrip_ProducesLoadVerifiedTrusted()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var cfg = server.ResolveDependency<Robust.Shared.Configuration.IConfigurationManager>();
        await server.WaitPost(() => cfg.SetCVar(TriadCCVars.TamperMode, "enforce"));

        var player = new NetUserId(Guid.Parse("33333333-4444-5555-6666-777777777777"));
        string envelopeStr = "";

        await server.WaitPost(() =>
        {
            var policy = server.ResolveDependency<IEntityManager>().System<TriadTamperPolicyService>();
            var envelope = policy.SignSave(yaml: "save-test ship YAML body", appraisal: 1000);
            envelopeStr = envelope.ShipFileString();
        });

        await server.WaitAssertion(() =>
        {
            var policy = server.ResolveDependency<IEntityManager>().System<TriadTamperPolicyService>();
            var parsed = AuthenticatedShipFile.FromShipFile(envelopeStr);
            var decision = policy.EvaluateLoad(parsed, player, "test-ship");
            Assert.That(decision.Allow, Is.True);
            Assert.That(decision.ResolvedEvent, Is.EqualTo(TriadShipyardEventType.LoadVerifiedTrusted));
            Assert.That(parsed.Appraisal, Is.EqualTo(1000), "Appraisal must survive the envelope round-trip.");
        });

        await pair.CleanReturnAsync();
    }
}
