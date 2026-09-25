#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client._Triad.Lobby;
using Content.Client.Players.PlayTimeTracking;
using Content.IntegrationTests.Pair;
using Content.Server.Administration.Managers;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Events;
using Content.Server.Mind;
using Content.Server.Station.Events;
using Content.Shared._Mono.CCVar;
using Content.Shared.CCVar;
using Content.Shared.Follower;
using Content.Shared.Follower.Components;
using Content.Shared.GameTicking;
using Content.Shared.Ghost;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Robust.Server.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using static Content.IntegrationTests.Tests._Triad.HighCommand.HighCommandTestHelpers;
using ClientGhostSystem = Content.Client.Ghost.GhostSystem;

namespace Content.IntegrationTests.Tests._Triad.HighCommand;

/// <summary>
/// Who gets into the TFA High Command outpost: the admin-gated jobs, the lobby that hides them, and ghosts.
/// </summary>
/// <remarks>
/// Every pool session starts as a host admin, because <c>console.loginlocal</c> defaults on and test connections
/// look local. A session with no admin rank is made by turning loginlocal off before a dummy connects, or before
/// reloading an existing session's admin data.
/// </remarks>
[TestFixture]
public sealed class HighCommandAccessTest
{
    private static readonly ProtoId<JobPrototype> ChiefEnforcer = "TdfChiefEnforcer";
    private static readonly ProtoId<DepartmentPrototype> CentralCommand = "CentralCommand";
    private static readonly ProtoId<DepartmentPrototype> Command = "Command";
    private static readonly ProtoId<DepartmentPrototype> Cargo = "Cargo";

    /// <summary>
    /// Round-start assignment and the latejoin auto-pick never hand an admin-gated job to a player with no admin
    /// rank, with role whitelists off as they are in this pool. Plain whitelisted jobs keep following the cvar.
    /// </summary>
    [Test]
    public async Task PlayersWithoutARankAreNeverAssignedHighCommand()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var admins = server.ResolveDependency<IAdminManager>();

        Assert.That(server.CfgMan.GetCVar(CCVars.GameRoleWhitelist), Is.False, "Fixture: the pool runs with role whitelists off.");

        server.CfgMan.SetCVar(CCVars.ConsoleLoginLocal, false);
        var plain = await server.AddDummySession();
        var admin = await server.AddDummySession();
        await server.WaitPost(() => admins.PromoteHost(admin));
        await PoolManager.WaitUntil(server, () => admins.IsAdmin(admin));
        Assert.That(admins.IsAdmin(plain, includeDeAdmin: true), Is.False, "Fixture: the plain dummy holds an admin rank.");

        await server.WaitAssertion(() =>
        {
            var (plainDisallowed, plainCandidates) = RaiseAssignmentEvents(server.EntMan, plain);
            var (adminDisallowed, adminCandidates) = RaiseAssignmentEvents(server.EntMan, admin);

            Assert.Multiple(() =>
            {
                Assert.That(plainDisallowed, Is.SupersetOf(new[] { Rep, Intern }),
                    "The latejoin auto-pick may hand an admin-gated job to a player with no admin rank.");
                Assert.That(plainCandidates, Is.EquivalentTo(new[] { ChiefEnforcer }),
                    "Round-start assignment kept an admin-gated job for a player with no admin rank.");
                Assert.That(plainDisallowed, Does.Not.Contain(ChiefEnforcer),
                    "A plain whitelisted job was disallowed with role whitelists off.");

                Assert.That(adminDisallowed, Is.Empty, "An admin was disallowed a job.");
                Assert.That(adminCandidates, Is.EquivalentTo(new[] { Rep, Intern, ChiefEnforcer }),
                    "Round-start assignment dropped a job an admin may take.");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// <c>admin.deadmin_on_join</c> de-admins the player inside <c>JoinGameCommand</c> before the join is checked,
    /// so the gate has to count a de-adminned rank. Goes through the real <c>joingame</c> command with the same
    /// arguments the lobby sends, and lands each job on its own spawners.
    /// </summary>
    [Test]
    public async Task AdminsLateJoinThroughDeadminOnJoin()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            InLobby = true,
            DummyTicker = false,
            Connected = true,
            Dirty = true,
            Fresh = true,
        });
        var server = pair.Server;
        var admins = server.ResolveDependency<IAdminManager>();
        var console = server.ResolveDependency<IServerConsoleHost>();
        var ticker = server.System<GameTicker>();
        var client = pair.Player!;

        // The client's new body stands in walled rooms full of ambient sound, where Mono's area echo can hand the
        // audio system an auxiliary it already deleted and trip a debug assert. Echo has nothing to do with this test.
        pair.Client.CfgMan.SetCVar(MonoCVars.AreaEchoEnabled, false);
        await CloseLobbyVotes(pair);
        server.CfgMan.SetCVar(CCVars.AdminDeadminOnJoin, true);
        server.CfgMan.SetCVar(CCVars.ConsoleLoginLocal, false);
        var plain = await server.AddDummySession();
        var intern = await server.AddDummySession();
        await server.WaitPost(() => admins.PromoteHost(intern));
        await PoolManager.WaitUntil(server, () => admins.IsAdmin(intern));
        Assert.Multiple(() =>
        {
            Assert.That(admins.IsAdmin(client), "Fixture: the pool client should start as a host admin.");
            Assert.That(admins.IsAdmin(plain, includeDeAdmin: true), Is.False, "Fixture: the plain dummy holds an admin rank.");
        });

        var outpost = await StartRoundWithOutpost(pair);
        await WaitUntilJoinable(pair, plain, intern, client);
        var station = server.EntMan.GetNetEntity(outpost.Station).Id;

        await server.WaitPost(() =>
        {
            console.ExecuteCommand(plain, $"joingame {Intern} {station}");
            console.ExecuteCommand(intern, $"joingame {Intern} {station}");
        });
        await pair.WaitClientCommand($"joingame {Rep} {station}");
        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(ticker.PlayerGameStatuses[plain.UserId], Is.Not.EqualTo(PlayerGameStatus.JoinedGame),
                    "A player with no admin rank joined an admin-gated job.");
                Assert.That(admins.IsAdmin(client), Is.False,
                    "Fixture: joingame did not de-admin the client, so deadmin_on_join is not being exercised.");

                AssertJoinedAt(pair, client, Rep, outpost);
                AssertJoinedAt(pair, intern, Intern, outpost);
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// An admin who sets a High Command job in the character editor and readies up starts the round on that job's
    /// spawners, dressed by the job's default loadout. The rule is added in the lobby the way a preset adds it, so the
    /// outpost stands before round-start assignment runs.
    /// </summary>
    [Test]
    public async Task AdminsReadyIntoHighCommandAtRoundStart()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            InLobby = true,
            DummyTicker = false,
            Connected = true,
            Dirty = true,
            Fresh = true,
        });
        var server = pair.Server;
        var admins = server.ResolveDependency<IAdminManager>();
        var ticker = server.System<GameTicker>();
        var client = pair.Player!;

        // Same walled rooms as AdminsLateJoinThroughDeadminOnJoin, same area echo assert.
        pair.Client.CfgMan.SetCVar(MonoCVars.AreaEchoEnabled, false);
        await CloseLobbyVotes(pair);
        Assert.That(admins.IsAdmin(client), "Fixture: the pool client should start as a host admin.");

        // Saved through IServerPreferencesManager.SetProfile, so the priority meets the same EnsureValid a save from
        // the character editor does.
        await pair.SetJobPriorities((Rep, JobPriority.High));

        var rule = EntityUid.Invalid;
        await server.WaitPost(() =>
        {
            ticker.SetGamePreset("Greenshift");
            rule = ticker.AddGameRule(RuleId);
            ticker.ToggleReadyAll(true);
            ticker.StartRound();
        });
        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound), "Fixture: the round did not start.");
            AssertJoinedAt(pair, client, Rep, ReadOutpost(server.EntMan, rule));

            // The profile holds no loadout for the job, so it spawns on the loadout's defaults. Starting gear keeps
            // the slots the loadout leaves empty.
            var mob = client.AttachedEntity!.Value;
            var inventory = server.System<InventorySystem>();
            string? Worn(string slot) => inventory.TryGetSlotEntity(mob, slot, out var item)
                ? server.EntMan.GetComponent<MetaDataComponent>(item.Value).EntityPrototype?.ID
                : null;

            Assert.Multiple(() =>
            {
                Assert.That(Worn("jumpsuit"), Is.EqualTo("ClothingUniformJumpsuitTfaHighCommand"), "Default jumpsuit.");
                Assert.That(Worn("head"), Is.EqualTo("ClothingHeadHatTfaHighCommand"), "Default hat.");
                Assert.That(Worn("outerClothing"), Is.EqualTo("ClothingOuterCoatTfaHighCommand"), "Default coat.");
                Assert.That(Worn("back"), Is.EqualTo("ClothingBackpackSatchelLeather"), "Default bag.");
                Assert.That(Worn("belt"), Is.EqualTo("WeaponEnergyRevolver"), "Starting gear's belt.");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A ghost with no admin rank can neither list nor warp to a player standing on the outpost. An admin ghost
    /// still lists them, and players elsewhere stay reachable.
    /// </summary>
    [Test]
    public async Task GhostsWithoutARankCannotReachTheOutpost()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            InLobby = true,
            DummyTicker = false,
            Connected = true,
            Dirty = true,
            Fresh = true,
        });
        var server = pair.Server;
        var entMan = server.EntMan;
        var ticker = server.System<GameTicker>();
        var minds = server.System<MindSystem>();
        var admins = server.ResolveDependency<IAdminManager>();
        var clientGhosts = pair.Client.System<ClientGhostSystem>();
        var clientNet = pair.Client.ResolveDependency<IEntityNetworkManager>();
        var session = pair.Player!;

        // Warp targets. Whether they hold a rank does not matter.
        await CloseLobbyVotes(pair);
        var inside = await server.AddDummySession();
        var outside = await server.AddDummySession();
        var outpost = await StartRoundWithOutpost(pair);

        var insideMob = EntityUid.Invalid;
        var outsideMob = EntityUid.Invalid;
        await server.WaitPost(() =>
        {
            var marker = Markers(entMan, outpost.Grid).First(m => m.Job == Rep);
            insideMob = entMan.SpawnEntity("MobHuman", new EntityCoordinates(outpost.Grid, marker.Position));
            outsideMob = entMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, ticker.DefaultMap));
            minds.TransferTo(minds.CreateMind(inside.UserId), insideMob);
            minds.TransferTo(minds.CreateMind(outside.UserId), outsideMob);
            ticker.JoinAsObserver(session);
        });
        await pair.RunTicksSync(10);

        // The test map has no observer spawn points, so the observer lands on a random grid, possibly the
        // outpost's. Put it on the round's map so a ghost on the outpost below can only mean a warp went through.
        var ghost = session.AttachedEntity ?? EntityUid.Invalid;
        await server.WaitPost(() => server.System<SharedTransformSystem>()
            .SetMapCoordinates(ghost, new MapCoordinates(Vector2.Zero, ticker.DefaultMap)));
        await pair.RunTicksSync(5);

        var insideNet = entMan.GetNetEntity(insideMob);
        var outsideNet = entMan.GetNetEntity(outsideMob);
        Assert.Multiple(() =>
        {
            Assert.That(entMan.HasComponent<GhostComponent>(ghost), "Fixture: the client did not become a ghost.");
            Assert.That(entMan.GetComponent<TransformComponent>(ghost).MapUid, Is.Not.EqualTo(outpost.Map),
                "Fixture: the ghost starts on the outpost map.");
            Assert.That(entMan.GetComponent<TransformComponent>(insideMob).MapUid, Is.EqualTo(outpost.Map),
                "Fixture: the inside player is not on the outpost map.");
        });

        List<GhostWarp>? warps = null;
        void OnWarps(GhostWarpsResponseEvent ev) => warps = ev.Warps;
        clientGhosts.GhostWarpsResponse += OnWarps;
        try
        {
            await pair.Client.WaitPost(() => clientGhosts.RequestWarps());
            await pair.RunTicksSync(10);
            Assert.That(warps?.Select(w => w.Entity), Does.Contain(insideNet),
                "Fixture: an admin ghost should list the player on the outpost.");

            // Take the client's rank away.
            server.CfgMan.SetCVar(CCVars.ConsoleLoginLocal, false);
            await server.WaitPost(() => admins.ReloadAdmin(session));
            await PoolManager.WaitUntil(server, () => !admins.IsAdmin(session, includeDeAdmin: true));

            warps = null;
            await pair.Client.WaitPost(() => clientGhosts.RequestWarps());
            await pair.Client.WaitPost(() => clientNet.SendSystemNetworkMessage(new GhostWarpToTargetRequestEvent(insideNet)));
            await pair.RunTicksSync(10);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(warps, Is.Not.Null, "Fixture: no warp list came back.");
                    var listed = warps?.Select(w => w.Entity).ToList() ?? new List<NetEntity>();
                    Assert.That(listed, Does.Contain(outsideNet), "Fixture: players elsewhere should stay listed.");
                    Assert.That(listed, Does.Not.Contain(insideNet),
                        "A ghost with no admin rank was offered a warp to a player on the outpost.");
                    Assert.That(Following(entMan, ghost), Is.Not.EqualTo(insideMob),
                        "A ghost with no admin rank warped to a player on the outpost.");
                    Assert.That(entMan.GetComponent<TransformComponent>(ghost).MapUid, Is.Not.EqualTo(outpost.Map),
                        "A ghost with no admin rank ended up on the outpost map.");
                });
            });

            // Control: the same request for a player elsewhere goes through, so the refusal above is the gate.
            await pair.Client.WaitPost(() => clientNet.SendSystemNetworkMessage(new GhostWarpToTargetRequestEvent(outsideNet)));
            await pair.RunTicksSync(10);
            await server.WaitAssertion(() => Assert.That(Following(entMan, ghost), Is.EqualTo(outsideMob),
                "Fixture: a warp-to request is not reaching the server at all."));
        }
        finally
        {
            clientGhosts.GhostWarpsResponse -= OnWarps;
        }

        // Dirty pairs go back to the pool, and the next borrower's recycle disconnects this client. A ghost still
        // following someone then warns while the client tears down, and the warning fails that next test.
        await server.WaitPost(() => server.System<FollowerSystem>().StopFollowingEntity(ghost, outsideMob));
        await pair.RunTicksSync(5);

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The lobby's whitelist check hides admin-gated jobs from a player with no admin rank even with role whitelists
    /// off, keeps them for an admin whether active or de-adminned, and leaves plain whitelisted jobs to the cvar.
    /// </summary>
    [Test]
    public async Task LobbyGateHoldsWithRoleWhitelistOff()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var admins = server.ResolveDependency<IAdminManager>();
        var requirements = pair.Client.ResolveDependency<JobRequirementsManager>();
        var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
        var session = pair.Player!;

        Assert.Multiple(() =>
        {
            Assert.That(pair.Client.CfgMan.GetCVar(CCVars.GameRoleWhitelist), Is.False, "Fixture: the client sees role whitelists off.");
            Assert.That(admins.IsAdmin(session), "Fixture: the pool client should start as a host admin.");
        });

        bool Allowed(ProtoId<JobPrototype> job) => requirements.CheckWhitelist(prototypes.Index(job), out _);

        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(Allowed(Rep), "An active admin is shut out of the Representative job.");
            Assert.That(Allowed(Intern), "An active admin is shut out of the Intern job.");
        });

        await server.WaitPost(() => admins.DeAdmin(session));
        await pair.RunTicksSync(10);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(Allowed(Rep), "A de-adminned admin lost the Representative job.");
            Assert.That(Allowed(Intern), "A de-adminned admin lost the Intern job.");
        });

        server.CfgMan.SetCVar(CCVars.ConsoleLoginLocal, false);
        await server.WaitPost(() => admins.ReloadAdmin(session));
        await PoolManager.WaitUntil(server, () => !admins.IsAdmin(session, includeDeAdmin: true));
        await pair.RunTicksSync(10);
        await pair.Client.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(Allowed(Rep), Is.False, "The lobby offers the Representative job to a player with no admin rank.");
                Assert.That(Allowed(Intern), Is.False, "The lobby offers the Intern job to a player with no admin rank.");
                Assert.That(Allowed(ChiefEnforcer), "A plain whitelisted job was hidden with role whitelists off.");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// For a player with no admin rank, the lobby's job lists drop admin-gated jobs and nothing else. The latejoin list
    /// drops a station whose only jobs are admin-gated and the character editor drops such a department, plain
    /// whitelisted jobs stay, and a station or department that arrived with no jobs is left to its own handling.
    /// </summary>
    [Test]
    public async Task LobbyListsDropOnlyAdminGatedJobs()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var admins = server.ResolveDependency<IAdminManager>();
        var requirements = pair.Client.ResolveDependency<JobRequirementsManager>();
        var prototypes = pair.Client.ResolveDependency<IPrototypeManager>();
        var session = pair.Player!;

        var outpost = new NetEntity(1);
        var tdf = new NetEntity(2);
        var empty = new NetEntity(3);
        var stations = new Dictionary<NetEntity, StationJobInformation>
        {
            [outpost] = new("TFA High Command", new() { [Rep] = null, [Intern] = null }, true, null, null),
            [tdf] = new("TDF Outpost", new() { [ChiefEnforcer] = 1 }, true, null, null),
            [empty] = new("Nothing Open", new(), true, null, null),
        };

        bool EditorDrops(ProtoId<DepartmentPrototype> department) =>
            AdminGatedJobFilter.EmptiesDepartment(prototypes.Index(department), prototypes, requirements);

        await pair.Client.WaitAssertion(() =>
        {
            Assert.That(prototypes.Index(CentralCommand).EditorHidden, Is.False,
                "Fixture: the character editor skips the High Command jobs' department before the filter sees it.");

            var filtered = AdminGatedJobFilter.Filter(stations, prototypes, requirements);
            Assert.Multiple(() =>
            {
                Assert.That(filtered.Keys, Is.EquivalentTo(new[] { outpost, tdf, empty }),
                    "The latejoin list hid a station from an admin.");
                Assert.That(filtered[outpost].JobsAvailable.Keys, Is.EquivalentTo(new[] { Rep, Intern }),
                    "The latejoin list hid an admin-gated job from an admin.");
                Assert.That(EditorDrops(CentralCommand), Is.False,
                    "The character editor hid the High Command jobs from an admin.");
            });
        });

        server.CfgMan.SetCVar(CCVars.ConsoleLoginLocal, false);
        await server.WaitPost(() => admins.ReloadAdmin(session));
        await PoolManager.WaitUntil(server, () => !admins.IsAdmin(session, includeDeAdmin: true));
        await pair.RunTicksSync(10);

        await pair.Client.WaitAssertion(() =>
        {
            var filtered = AdminGatedJobFilter.Filter(stations, prototypes, requirements);
            Assert.Multiple(() =>
            {
                Assert.That(filtered.Keys, Does.Not.Contain(outpost),
                    "The latejoin list names the outpost to a player with no admin rank.");
                Assert.That(filtered.Keys, Does.Contain(tdf), "A station with plain whitelisted jobs was hidden.");
                Assert.That(filtered[tdf].JobsAvailable.Keys, Is.EquivalentTo(new[] { ChiefEnforcer }),
                    "A plain whitelisted job was hidden instead of shown disabled.");
                Assert.That(filtered.Keys, Does.Contain(empty), "A station that arrived with no jobs was dropped.");

                Assert.That(EditorDrops(CentralCommand),
                    "The character editor names the High Command jobs to a player with no admin rank.");
                Assert.That(EditorDrops(Command), Is.False, "A department with plain whitelisted jobs was hidden.");
                Assert.That(EditorDrops(Cargo), Is.False, "A department that lists no jobs was dropped by the filter.");
            });
        });

        await pair.CleanReturnAsync();
    }

    private static (HashSet<ProtoId<JobPrototype>> Disallowed, List<ProtoId<JobPrototype>> Candidates) RaiseAssignmentEvents(
        IEntityManager entMan,
        ICommonSession session)
    {
        var disallowed = new HashSet<ProtoId<JobPrototype>>();
        var disallowedEv = new GetDisallowedJobsEvent(session, disallowed);
        entMan.EventBus.RaiseEvent(EventSource.Local, ref disallowedEv);

        var candidates = new List<ProtoId<JobPrototype>> { Rep, Intern, ChiefEnforcer };
        var candidatesEv = new StationJobsGetCandidatesEvent(session.UserId, candidates);
        entMan.EventBus.RaiseEvent(EventSource.Local, ref candidatesEv);

        return (disallowed, candidates);
    }

    /// <summary>
    /// SpawnPlayer returns without a word while a session's user data or job bans are still loading, which would
    /// look exactly like the admin gate refusing it.
    /// </summary>
    private static async Task WaitUntilJoinable(TestPair pair, params ICommonSession[] sessions)
    {
        var userDb = pair.Server.ResolveDependency<UserDbDataManager>();
        var bans = pair.Server.ResolveDependency<IBanManager>();
        await PoolManager.WaitUntil(pair.Server,
            () => sessions.All(s => userDb.IsLoadComplete(s) && bans.GetJobBans(s.UserId) != null));
    }

    private static void AssertJoinedAt(TestPair pair, ICommonSession session, ProtoId<JobPrototype> job, RunningOutpost outpost)
    {
        var entMan = pair.Server.EntMan;
        var ticker = pair.Server.System<GameTicker>();

        Assert.That(ticker.PlayerGameStatuses[session.UserId], Is.EqualTo(PlayerGameStatus.JoinedGame),
            $"{session.Name} did not join as {job}.");
        if (session.AttachedEntity is not { } mob)
        {
            Assert.Fail($"{session.Name} joined as {job} with no body.");
            return;
        }

        var xform = entMan.GetComponent<TransformComponent>(mob);
        var mind = pair.Server.System<MindSystem>().GetMind(mob);
        var markers = Markers(entMan, outpost.Grid).Where(m => m.Job == job).ToList();

        Assert.That(xform.GridUid, Is.EqualTo(outpost.Grid), $"{session.Name} spawned off the outpost as {job}.");
        Assert.That(pair.Server.System<SharedJobSystem>().MindTryGetJobId(mind, out var actual) ? actual : null, Is.EqualTo(job),
            $"{session.Name} spawned with the wrong job.");
        Assert.That(markers.Any(m => (m.Position - xform.LocalPosition).Length() < 0.01f),
            $"{session.Name} did not spawn on a {job} marker.");
    }

    private static EntityUid Following(IEntityManager entMan, EntityUid ghost)
    {
        return entMan.TryGetComponent<FollowerComponent>(ghost, out var follower) ? follower.Following : EntityUid.Invalid;
    }
}
