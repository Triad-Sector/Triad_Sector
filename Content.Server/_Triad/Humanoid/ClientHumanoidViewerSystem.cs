using Content.Shared.GameTicking;
using Content.Server.Station.Systems;
using Content.Shared.Coordinates;
using Content.Shared._Mono.Pvs;
using Content.Shared._Triad.Humanoid;
using Content.Shared.Humanoid;

namespace Content.Server._Triad.Humanoid;

public sealed partial class ClientHumanoidViewerSystem : EntitySystem
{
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private StationSpawningSystem _spawning = default!;

    public EntityUid? PausedMap { get; private set; }

    private EntityQuery<HumanoidAppearanceComponent> _humanoidQuery;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);

        _humanoidQuery = GetEntityQuery<HumanoidAppearanceComponent>();
    }

    private void OnRoundRestart(RoundRestartCleanupEvent _)
    {
        if (PausedMap == null || !Exists(PausedMap))
            return;

        Del(PausedMap.Value);
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        EnsurePausedMap();

        if (PausedMap == null || !Exists(PausedMap))
            return;

        var mob = ev.Mob;

        EntityUid? viewerMob = null;

        var query = EntityQueryEnumerator<ClientHumanoidViewerComponent>();
        while (query.MoveNext(out var uid, out var otherViewer))
        {
            // Cryoing and respawning shouldn't make duplicates
            if (ev.Player != otherViewer.Session)
                continue;

            if (Name(uid) == ev.Profile.Name)
            {
                viewerMob = uid;
                break;
            }

            if (_humanoidQuery.TryComp(uid, out var humanoid))
            {
                if (humanoid.Age == ev.Profile.Age || humanoid.Gender == ev.Profile.Gender)
                {
                    viewerMob = uid;
                    break;
                }
            }
        }

        if (viewerMob == null)
        {
            viewerMob = _spawning.SpawnPlayerMob(mob.ToCoordinates(), null, ev.Profile, null, session: ev.Player);
            _transform.SetParent(viewerMob.Value, PausedMap.Value);

            var viewer = EnsureComp<ClientHumanoidViewerComponent>(viewerMob.Value);
            viewer.Session = ev.Player;
            Dirty(viewerMob.Value, viewer);

            EnsureComp<GlobalPvsComponent>(viewerMob.Value);
        }

        var humanoidView = EnsureComp<HumanoidViewComponent>(mob);
        humanoidView.PvsView = viewerMob;
        Dirty(mob, humanoidView);
    }

    private void EnsurePausedMap()
    {
        if (PausedMap != null && Exists(PausedMap))
            return;

        var newmap = _map.CreateMap();
        _map.SetPaused(newmap, true);
        PausedMap = newmap;
    }
}
