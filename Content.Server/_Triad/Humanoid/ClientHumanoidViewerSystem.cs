using Content.Shared.GameTicking;
using Content.Shared._Triad.Humanoid;
using Content.Shared.Humanoid;
using Robust.Shared.Prototypes;
using Content.Shared.Roles;
using Content.Shared.Clothing;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Station;
using Content.Shared.Inventory;

namespace Content.Server._Triad.Humanoid;

public sealed partial class ClientHumanoidViewerSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IDependencyCollection _dependencyCollection = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private MetaDataSystem _metaData = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedHumanoidAppearanceSystem _humanoid = default!;
    [Dependency] private SharedStationSpawningSystem _stationSpawning = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private static readonly SlotFlags RemoveDummyClothingFlags =
        SlotFlags.OUTERCLOTHING | SlotFlags.HEAD | SlotFlags.SUITSTORAGE | SlotFlags.MASK | SlotFlags.BACK | SlotFlags.EYES;

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

        var query = AllEntityQuery<HumanoidViewerEntityComponent>();
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

        // Reused doll: refresh to the current profile in case the character changed
        if (viewerMob != null)
        {
            _humanoid.LoadProfile(viewerMob.Value, ev.Profile);
            _metaData.SetEntityName(viewerMob.Value, ev.Profile.Name);
        }

        if (viewerMob == null && _prototypes.TryIndex(ev.Profile.Species, out var speciesProto))
        {
            viewerMob = Spawn(speciesProto.DollPrototype);
            _humanoid.LoadProfile(viewerMob.Value, ev.Profile); // Copy humanoid appearance
            _metaData.SetEntityName(viewerMob.Value, ev.Profile.Name);

            // Give the dummy a starting gear as well
            if (ev.JobId != null && _prototypes.TryIndex<JobPrototype>(ev.JobId, out var jobPrototype))
            {
                var jobLoadout = LoadoutSystem.GetJobPrototype(ev.JobId);

                if (_prototypes.TryIndex<RoleLoadoutPrototype>(jobLoadout, out var roleProto))
                {
                    ev.Profile.Loadouts.TryGetValue(jobLoadout, out var loadout);

                    // Set to default if not present
                    if (loadout == null)
                    {
                        loadout = new RoleLoadout(jobLoadout);
                        loadout.SetDefault(ev.Profile, ev.Player, _prototypes);
                        loadout.EnsureValid(ev.Profile, ev.Player, _dependencyCollection);
                    }

                    _stationSpawning.EquipRoleLoadout(viewerMob.Value, loadout, roleProto);
                }

                if (jobPrototype.StartingGear != null)
                    _stationSpawning.EquipStartingGear(viewerMob.Value, jobPrototype.StartingGear, raiseEvent: false);
            }

            // Unequip items that wouldn't make sense in a photograph of the dummy, like hardsuits and masks
            var enumerator = _inventory.GetSlotEnumerator(viewerMob.Value, RemoveDummyClothingFlags);
            while (enumerator.MoveNext(out var slot))
            {
                if (slot.ContainedEntity is not { } item)
                    continue;

                QueueDel(item);
            }

            _transform.SetParent(viewerMob.Value, PausedMap.Value);

            var viewerComp = EnsureComp<HumanoidViewerEntityComponent>(viewerMob.Value);
            viewerComp.Session = ev.Player;
            Dirty(viewerMob.Value, viewerComp);
        }

        var humanoidView = EnsureComp<HumanoidViewComponent>(mob);
        humanoidView.ViewEntity = viewerMob;
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
