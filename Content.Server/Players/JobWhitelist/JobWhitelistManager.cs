using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Administration; // Triad: admin-gated jobs
using Content.Server.Administration.Managers; // Triad: admin-gated jobs
using Content.Server.Database;
using Content.Shared.CCVar;
using Content.Shared.Ghost.Roles; // Frontier: Ghost Role handling
using Content.Shared.Players; // DeltaV
using Content.Shared.Players.JobWhitelist;
using Content.Shared.Players.PlayTimeTracking; // Frontier: Global whitelist handling
using Content.Shared.Roles;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server.Players.JobWhitelist;

public sealed partial class JobWhitelistManager : IPostInjectInit
{
    [Dependency] private IAdminManager _admin = default!; // Triad: admin-gated jobs
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private INetManager _net = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private UserDbDataManager _userDb = default!;
    [Dependency] private ILogManager _log = default!;

    private readonly ISawmill _sawmill = default!;

    private readonly Dictionary<NetUserId, HashSet<string>> _whitelists = new();
    private readonly Dictionary<NetUserId, bool> _globalWhitelists = new(); // Frontier

    public void Initialize()
    {
        _net.RegisterNetMessage<MsgJobWhitelist>();
        _net.RegisterNetMessage<MsgWhitelist>();

        _log.GetSawmill(nameof(JobWhitelistManager));

        // Triad: admin-gated jobs follow the admin rank live, so gaining or losing a rank mid-round re-sends the
        // payload and the lobby list changes under the player without a reconnect. A de-admin keeps the rank, so
        // it re-sends the same list.
        _admin.OnPermsChanged += OnAdminPermsChanged;
        // End Triad
    }

    // Triad: admin-gated jobs.
    private void OnAdminPermsChanged(AdminPermsChangedEventArgs args)
    {
        SendJobWhitelist(args.Player);
    }

    /// <summary>
    /// Every job gated on an admin rank rather than on a database whitelist row.
    /// </summary>
    private IEnumerable<string> AdminGatedJobs()
    {
        foreach (var job in _prototypes.EnumeratePrototypes<JobPrototype>())
        {
            if (job.AdminWhitelist)
                yield return job.ID;
        }
    }
    // End Triad

    private async Task LoadData(ICommonSession session, CancellationToken cancel)
    {
        var whitelists = await _db.GetJobWhitelists(session.UserId, cancel);
        cancel.ThrowIfCancellationRequested();
        _whitelists[session.UserId] = whitelists.ToHashSet();
        // Frontier: global whitelists
        var globalWhitelist = await _db.GetWhitelistStatusAsync(session.UserId);
        cancel.ThrowIfCancellationRequested();
        _globalWhitelists[session.UserId] = globalWhitelist;
        // End Frontier
    }

    private void FinishLoad(ICommonSession session)
    {
        SendJobWhitelist(session);
        SendWhitelist(session);
    }

    private void ClientDisconnected(ICommonSession session)
    {
        _whitelists.Remove(session.UserId);
        _globalWhitelists.Remove(session.UserId); // Frontier: global whitelists
    }

    public async void AddWhitelist(NetUserId player, ProtoId<JobPrototype> job)
    {
        if (_whitelists.TryGetValue(player, out var whitelists))
            whitelists.Add(job);

        await _db.AddJobWhitelist(player, job);

        if (_player.TryGetSessionById(player, out var session))
            SendJobWhitelist(session);
    }

    public bool IsAllowed(ICommonSession session, ProtoId<JobPrototype> job)
    {
        // Triad: an admin-gated job answers off holding an admin rank, active or de-adminned, and never off the
        // database whitelist. De-adminned counts because admin.deadmin_on_join de-admins the player inside
        // JoinGameCommand before the join is checked. Ahead of the GameRoleWhitelist cvar so turning that cvar off
        // cannot open the role to everyone.
        if (_prototypes.TryIndex(job, out var adminGated) && adminGated.AdminWhitelist)
            return _admin.IsAdmin(session, includeDeAdmin: true);
        // End Triad

        if (!_config.GetCVar(CCVars.GameRoleWhitelist))
            return true;

        if (!_prototypes.TryIndex(job, out var jobPrototype) ||
            !jobPrototype.Whitelisted)
        {
            return true;
        }

        // DeltaV: Blanket player whitelist allows all roles
        if (session.ContentData()?.Whitelisted ?? false)
            return true;

        return IsWhitelisted(session.UserId, job);
    }

    public bool IsWhitelisted(NetUserId player, ProtoId<JobPrototype> job)
    {
        if (!_whitelists.TryGetValue(player, out var whitelists) || // Frontier: added globalWhitelist check
        !_globalWhitelists.TryGetValue(player, out var globalWhitelist)) // Frontier
        {
            _sawmill.Error("Unable to check if player {Player} is whitelisted for {Job}. Stack trace:\\n{StackTrace}",
                player,
                job,
                Environment.StackTrace);
            return false;
        }

        return globalWhitelist || whitelists.Contains(job); // Frontier: added globalWhitelist
    }

    public async void RemoveWhitelist(NetUserId player, ProtoId<JobPrototype> job)
    {
        _whitelists.GetValueOrDefault(player)?.Remove(job);
        await _db.RemoveJobWhitelist(player, job);

        if (_player.TryGetSessionById(new NetUserId(player), out var session))
            SendJobWhitelist(session);
    }

    public void SendJobWhitelist(ICommonSession player)
    {
        // Triad: copy rather than mutate. The set here is the live per-player cache, so folding the
        // admin-gated ids into it would persist them past a deadmin.
        var whitelist = new HashSet<string>(_whitelists.GetValueOrDefault(player.UserId) ?? new HashSet<string>());

        if (_admin.IsAdmin(player, includeDeAdmin: true))
            whitelist.UnionWith(AdminGatedJobs());
        // End Triad

        var msg = new MsgJobWhitelist
        {
            Whitelist = whitelist
        };

        _net.ServerSendMessage(msg, player.Channel);
    }

    // Frontier: Ghost Role handling
    public async void AddWhitelist(NetUserId player, ProtoId<GhostRolePrototype> ghostRole)
    {
        if (_whitelists.TryGetValue(player, out var whitelists))
            whitelists.Add(ghostRole);

        await _db.AddGhostRoleWhitelist(player, ghostRole);

        if (_player.TryGetSessionById(player, out var session))
            SendJobWhitelist(session);
    }

    public bool IsAllowed(ICommonSession session, ProtoId<GhostRolePrototype> ghostRole)
    {
        if (!_config.GetCVar(CCVars.GameRoleWhitelist))
            return true;

        if (!_prototypes.TryIndex(ghostRole, out var ghostRolePrototype) ||
            !ghostRolePrototype.Whitelisted)
        {
            return true;
        }

        return IsWhitelisted(session.UserId, ghostRole);
    }

    public bool IsWhitelisted(NetUserId player, ProtoId<GhostRolePrototype> ghostRole)
    {
        if (!_whitelists.TryGetValue(player, out var whitelists) ||
        !_globalWhitelists.TryGetValue(player, out var globalWhitelist))
        {
            _sawmill.Error("Unable to check if player {Player} is whitelisted for {GhostRole}. Stack trace:\\n{StackTrace}",
                player,
                ghostRole,
                Environment.StackTrace);
            return false;
        }

        return globalWhitelist || whitelists.Contains(ghostRole);
    }

    public async void RemoveWhitelist(NetUserId player, ProtoId<GhostRolePrototype> ghostRole)
    {
        _whitelists.GetValueOrDefault(player)?.Remove(ghostRole);
        await _db.RemoveGhostRoleWhitelist(player, ghostRole);

        if (_player.TryGetSessionById(new NetUserId(player), out var session))
            SendJobWhitelist(session);
    }

    public async void AddGlobalWhitelist(NetUserId player)
    {
        if (_globalWhitelists.ContainsKey(player))
            _globalWhitelists[player] = true;

        await _db.AddToWhitelistAsync(player);

        if (_player.TryGetSessionById(player, out var session))
            SendWhitelist(session);
    }

    public bool IsGloballyWhitelisted(NetUserId player)
    {
        if (!_config.GetCVar(CCVars.GameRoleWhitelist))
            return true;

        if (!_globalWhitelists.TryGetValue(player, out var whitelist))
        {
            _sawmill.Error("Unable to check if player {Player} is globally whitelisted. Stack trace:\\n{StackTrace}",
                player,
                Environment.StackTrace);
            return false;
        }

        return whitelist;
    }

    public async void RemoveGlobalWhitelist(NetUserId player)
    {
        if (_globalWhitelists.ContainsKey(player))
            _globalWhitelists[player] = false;

        await _db.RemoveFromWhitelistAsync(player);

        if (_player.TryGetSessionById(player, out var session))
            SendWhitelist(session);
    }

    public void SendWhitelist(ICommonSession player)
    {
        var msg = new MsgWhitelist
        {
            Whitelisted = _globalWhitelists.GetValueOrDefault(player.UserId)
        };

        _net.ServerSendMessage(msg, player.Channel);
    }
    // End Frontier

    void IPostInjectInit.PostInject()
    {
        _userDb.AddOnLoadPlayer(LoadData);
        _userDb.AddOnFinishLoad(FinishLoad);
        _userDb.AddOnPlayerDisconnect(ClientDisconnected);
    }
}
