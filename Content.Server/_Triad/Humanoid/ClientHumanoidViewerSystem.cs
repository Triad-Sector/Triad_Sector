using Content.Shared.GameTicking;
using Content.Shared._Triad.Humanoid;
using Content.Shared.Humanoid;
using Robust.Shared.Prototypes;
using Content.Server.Chat.Managers;

namespace Content.Server._Triad.Humanoid;

public sealed partial class ClientHumanoidViewerSystem : EntitySystem
{
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private MetaDataSystem _metaData = default!;
    [Dependency] private SharedHumanoidAppearanceSystem _humanoid = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IChatManager _admin = default!;

    private static readonly EntProtoId HumanoidViewerMobProto = "MobTriadHumanoidViewer";

    public EntityUid? BlankMap { get; private set; }

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
        if (BlankMap == null || !Exists(BlankMap))
            return;

        Del(BlankMap.Value);
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        EnsureBlankMap();

        if (BlankMap == null || !Exists(BlankMap))
            return;

        var mob = ev.Mob;

        EntityUid? viewerMob = null;

        var query = EntityQueryEnumerator<HumanoidViewerEntityComponent>();
        while (query.MoveNext(out var uid, out var otherViewer))
        {
            // Cryoing and respawning shouldn't make duplicates
            if (otherViewer.Session == null)
                continue;

            if (ev.Player.UserId != otherViewer.Session.UserId)
                continue;

            // Same session and name? Don't make a new viewer mob
            if (Name(uid) == ev.Profile.Name)
            {
                viewerMob = uid;
                break;
            }

            // Name different, but age and species is the same? Likely the same character, use as viewer
            // There is a case where someone changes their character name, gender, and age but it doesn't matter too much
            if (_humanoidQuery.TryComp(uid, out var humanoid))
            {
                if (humanoid.Age == ev.Profile.Age && humanoid.Gender == ev.Profile.Gender)
                {
                    viewerMob = uid;
                    break;
                }
            }
        }

        if (viewerMob == null)
        {
            viewerMob = Spawn(HumanoidViewerMobProto);
            _transform.SetParent(viewerMob.Value, BlankMap.Value);
            _humanoid.LoadProfile(viewerMob.Value, ev.Profile); // Copy humanoid appearance
            _metaData.SetEntityName(viewerMob.Value, ev.Profile.Name);

            var viewerComp = EnsureComp<HumanoidViewerEntityComponent>(viewerMob.Value);
            viewerComp.Session = ev.Player;
            Dirty(viewerMob.Value, viewerComp);
        }

        var humanoidView = EnsureComp<HumanoidViewComponent>(mob);
        humanoidView.PvsView = viewerMob;
        Dirty(mob, humanoidView);
    }

    private void EnsureBlankMap()
    {
        if (BlankMap != null && Exists(BlankMap))
            return;

        var newmap = _map.CreateMap();
        BlankMap = newmap;
    }
}
