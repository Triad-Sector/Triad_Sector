using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Content.Server._Triad.Shipyard;
using Content.Server._Triad.Shipyard.Persistence;
using Content.Server.Database;
using Content.Shared._Triad.CCVar;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._Triad.Shipyard.TamperProtection;

[TestFixture]
public sealed class PolicyServiceTests
{
    private static readonly NetUserId TestPlayer = new(Guid.Parse("11111111-2222-3333-4444-555555555555"));

    [Test]
    public async Task PolicyService_OffAllowsEverything()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var cfg = server.ResolveDependency<Robust.Shared.Configuration.IConfigurationManager>();
        await server.WaitPost(() => cfg.SetCVar(TriadCCVars.TamperMode, "off"));

        await server.WaitAssertion(() =>
        {
            var policy = server.ResolveDependency<IEntityManager>().System<TriadTamperPolicyService>();
            using var foreignKey = RSA.Create(2048);

            var unsigned = AuthenticatedShipFile.FromShipFile("anything");
            var foreign = TamperTestHelpers.SignedEnvelope(foreignKey, "foreign content");

            foreach (var envelope in new[] { unsigned, foreign })
            {
                var decision = policy.EvaluateLoad(envelope, TestPlayer, "test-ship");
                Assert.That(decision.Allow, Is.True);
                Assert.That(decision.ResolvedEvent, Is.EqualTo(TriadShipyardEventType.LoadVerifiedTrusted));
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PolicyService_NotifyAllowsUnsigned()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var cfg = server.ResolveDependency<Robust.Shared.Configuration.IConfigurationManager>();
        await server.WaitPost(() => cfg.SetCVar(TriadCCVars.TamperMode, "notify"));

        await server.WaitAssertion(() =>
        {
            var policy = server.ResolveDependency<IEntityManager>().System<TriadTamperPolicyService>();
            var raw = ""; // truly unsigned: no envelope structure at all
            var envelope = AuthenticatedShipFile.FromShipFile(raw);
            var decision = policy.EvaluateLoad(envelope, TestPlayer, "test-ship");
            Assert.That(decision.Allow, Is.True);
            Assert.That(decision.ResolvedEvent, Is.EqualTo(TriadShipyardEventType.LoadUnsigned));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PolicyService_NotifyAllowsInvalidAndForeignWithDescriptiveEvents()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var cfg = server.ResolveDependency<Robust.Shared.Configuration.IConfigurationManager>();
        await server.WaitPost(() => cfg.SetCVar(TriadCCVars.TamperMode, "notify"));

        await server.WaitAssertion(() =>
        {
            var policy = server.ResolveDependency<IEntityManager>().System<TriadTamperPolicyService>();
            using var foreignKey = RSA.Create(2048);

            var invalid = TamperTestHelpers.TamperedEnvelope(foreignKey, "signed content", "edited content");
            var invalidDecision = policy.EvaluateLoad(invalid, TestPlayer, "test-ship");
            Assert.That(invalidDecision.Allow, Is.True);
            Assert.That(invalidDecision.ResolvedEvent, Is.EqualTo(TriadShipyardEventType.LoadInvalidSignature));

            var foreign = TamperTestHelpers.SignedEnvelope(foreignKey, "foreign content");
            var foreignDecision = policy.EvaluateLoad(foreign, TestPlayer, "test-ship");
            Assert.That(foreignDecision.Allow, Is.True);
            Assert.That(foreignDecision.ResolvedEvent, Is.EqualTo(TriadShipyardEventType.LoadVerifiedUntrusted),
                "A forged-key ship is flagged, never trusted, under notify.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PolicyService_EnforceBlocksUnsigned()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var cfg = server.ResolveDependency<Robust.Shared.Configuration.IConfigurationManager>();
        await server.WaitPost(() => cfg.SetCVar(TriadCCVars.TamperMode, "enforce"));

        await server.WaitAssertion(() =>
        {
            var policy = server.ResolveDependency<IEntityManager>().System<TriadTamperPolicyService>();
            // Not an envelope at all, so it reads as an unsigned ship.
            var envelope = AuthenticatedShipFile.FromShipFile("anything");
            var decision = policy.EvaluateLoad(envelope, TestPlayer, "test-ship");
            Assert.That(decision.Allow, Is.False);
            Assert.That(decision.ResolvedEvent, Is.EqualTo(TriadShipyardEventType.LoadRejectedUnsigned));
            Assert.That(decision.PopupReasonLocId, Is.Not.Null);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PolicyService_EnforceBlocksInvalidSignature()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var cfg = server.ResolveDependency<Robust.Shared.Configuration.IConfigurationManager>();
        await server.WaitPost(() => cfg.SetCVar(TriadCCVars.TamperMode, "enforce"));

        // Signed by a key the server owns, then edited: owning the key must not rescue a payload the
        // signature no longer covers.
        using var ownKey = RSA.Create(2048);
        await SeedOwnKey(server, ownKey);

        await server.WaitAssertion(() =>
        {
            var policy = server.ResolveDependency<IEntityManager>().System<TriadTamperPolicyService>();
            var envelope = TamperTestHelpers.TamperedEnvelope(ownKey, "signed content", "edited content");
            Assert.That(envelope.IsShipSigned(), Is.False);
            var decision = policy.EvaluateLoad(envelope, TestPlayer, "test-ship");
            Assert.That(decision.Allow, Is.False);
            Assert.That(decision.ResolvedEvent, Is.EqualTo(TriadShipyardEventType.LoadRejectedInvalidSignature));
            Assert.That(decision.PopupReasonLocId, Is.Not.Null);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PolicyService_EnforceAllowsOwnKeySignedShipWithNoAdminAction()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var cfg = server.ResolveDependency<Robust.Shared.Configuration.IConfigurationManager>();
        await server.WaitPost(() => cfg.SetCVar(TriadCCVars.TamperMode, "enforce"));

        using var ownKey = RSA.Create(2048);
        await SeedOwnKey(server, ownKey);

        await server.WaitAssertion(() =>
        {
            var policy = server.ResolveDependency<IEntityManager>().System<TriadTamperPolicyService>();
            // Signed by a key in this server's signing-keys table. No admin trust action of any kind.
            var envelope = TamperTestHelpers.SignedEnvelope(ownKey, "hello world");
            var decision = policy.EvaluateLoad(envelope, TestPlayer, "test-ship");
            Assert.That(decision.Allow, Is.True);
            Assert.That(decision.ResolvedEvent, Is.EqualTo(TriadShipyardEventType.LoadVerifiedTrusted),
                "A ship signed by the server's own key must load under enforce with no admin action.");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PolicyService_EnforceRejectsForeignSignedShip()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var cfg = server.ResolveDependency<Robust.Shared.Configuration.IConfigurationManager>();
        await server.WaitPost(() => cfg.SetCVar(TriadCCVars.TamperMode, "enforce"));

        await server.WaitAssertion(() =>
        {
            var policy = server.ResolveDependency<IEntityManager>().System<TriadTamperPolicyService>();
            using var foreignKey = RSA.Create(2048);
            var foreign = TamperTestHelpers.SignedEnvelope(foreignKey, "forged content");
            // Self-consistent signature, but from a key the server never generated.
            Assert.That(foreign.IsShipSigned(), Is.True);
            var decision = policy.EvaluateLoad(foreign, TestPlayer, "test-ship");
            Assert.That(decision.Allow, Is.False);
            Assert.That(decision.ResolvedEvent, Is.EqualTo(TriadShipyardEventType.LoadRejectedForeignKey),
                "A forged-key (valid but foreign) ship must be rejected under enforce.");
        });

        await pair.CleanReturnAsync();
    }

    // F15 fix: removed PolicyService_PermitClearedOnPlayerDisconnect.
    // The previous behavior cleared in-memory permits on player disconnect via OnPlayerStatus.
    // F15 persists permits with explicit expiry so admin investigations across a player's
    // disconnect/reconnect don't lose their grants. Disconnect-eviction is no longer the
    // intended behavior; the test as written contradicts the new design.

    [Test]
    public async Task PolicyService_PermitAllowsUnsignedUnderEnforce()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var cfg = server.ResolveDependency<Robust.Shared.Configuration.IConfigurationManager>();
        await server.WaitPost(() => cfg.SetCVar(TriadCCVars.TamperMode, "enforce"));

        await server.WaitPost(() =>
        {
            var permitStore = IoCManager.Resolve<ITriadShipyardPermitStore>();
            permitStore.GrantAsync(TestPlayer.UserId, Guid.NewGuid(),
                DateTime.UtcNow, "test", default).GetAwaiter().GetResult();
        });

        await server.WaitAssertion(() =>
        {
            var policy = server.ResolveDependency<IEntityManager>().System<TriadTamperPolicyService>();
            var unsigned = AuthenticatedShipFile.FromShipFile("anything");
            var decision = policy.EvaluateLoad(unsigned, TestPlayer, "test-ship");
            Assert.That(decision.Allow, Is.True);
            Assert.That(decision.ResolvedEvent, Is.EqualTo(TriadShipyardEventType.LoadMigrated));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PolicyService_PermitTriggersMigrationOnForeignKey()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var cfg = server.ResolveDependency<Robust.Shared.Configuration.IConfigurationManager>();
        await server.WaitPost(() => cfg.SetCVar(TriadCCVars.TamperMode, "enforce"));

        // A foreign-signed (forged-key) ship is rejected under enforce unless the player holds a
        // permit; with one, it is let through and logged as migrated.
        using var foreignKey = RSA.Create(2048);
        await server.WaitPost(() =>
        {
            var permitStore = IoCManager.Resolve<ITriadShipyardPermitStore>();
            permitStore.GrantAsync(TestPlayer.UserId, Guid.NewGuid(),
                DateTime.UtcNow, "test", default).GetAwaiter().GetResult();
        });

        await server.WaitAssertion(() =>
        {
            var policy = server.ResolveDependency<IEntityManager>().System<TriadTamperPolicyService>();
            var foreign = TamperTestHelpers.SignedEnvelope(foreignKey, "legacy foreign content");
            var decision = policy.EvaluateLoad(foreign, TestPlayer, "test-ship");
            Assert.That(decision.Allow, Is.True);
            Assert.That(decision.ResolvedEvent, Is.EqualTo(TriadShipyardEventType.LoadMigrated));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PolicyService_OwnKeyShipTakesOursBranchEvenWithPermit()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var cfg = server.ResolveDependency<Robust.Shared.Configuration.IConfigurationManager>();
        await server.WaitPost(() => cfg.SetCVar(TriadCCVars.TamperMode, "enforce"));

        using var ownKey = RSA.Create(2048);
        await SeedOwnKey(server, ownKey);

        // Grant a permit to the player about to load, to confirm an own-key ship takes the ours
        // branch (LoadVerifiedTrusted) rather than the migration branch.
        await server.WaitPost(() =>
        {
            var permitStore = IoCManager.Resolve<ITriadShipyardPermitStore>();
            permitStore.GrantAsync(TestPlayer.UserId, Guid.NewGuid(),
                DateTime.UtcNow, "test", default).GetAwaiter().GetResult();
        });

        await server.WaitAssertion(() =>
        {
            var policy = server.ResolveDependency<IEntityManager>().System<TriadTamperPolicyService>();
            var signed = TamperTestHelpers.SignedEnvelope(ownKey, "trusted content");

            var decision = policy.EvaluateLoad(signed, TestPlayer, "test-ship");
            Assert.That(decision.Allow, Is.True);
            Assert.That(decision.ResolvedEvent, Is.EqualTo(TriadShipyardEventType.LoadVerifiedTrusted),
                "An own-key ship must take the ours branch, not migration, even when a permit exists.");
        });

        await pair.CleanReturnAsync();
    }

    // Record a test key as one of this server's own signing keys and reseed the own-key cache, so the
    // ours branch answers for envelopes that key signs. Each test brings a fresh key, so pooled
    // servers sharing a DB never make one test's key count for another.
    private static async Task SeedOwnKey(
        Robust.UnitTesting.RobustIntegrationTest.ServerIntegrationInstance server,
        RSA key)
    {
        var db = server.ResolveDependency<IServerDbManager>();
        await TamperTestHelpers.InsertOwnKey(db, key);
        await server.WaitPost(() =>
        {
            var keyStore = IoCManager.Resolve<ITriadShipyardKeyStore>();
            keyStore.PopulateOwnKeysAsync(default).GetAwaiter().GetResult();
        });
    }
}
