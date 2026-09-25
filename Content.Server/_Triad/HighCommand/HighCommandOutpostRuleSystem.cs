using Content.Server._NF.Station.Systems;
using Content.Server._Triad.Ghost;
using Content.Server.GameTicking.Rules;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Systems;
using Content.Shared.GameTicking.Components;
using Content.Shared.Whitelist;
using Robust.Server.GameObjects;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;

namespace Content.Server._Triad.HighCommand;

/// <summary>
/// Stands the TFA High Command outpost up on its own map, reachable only by admin ghost warp or by a hull
/// an admin has cleared.
/// </summary>
/// <remarks>
/// The outpost is deliberately not a <see cref="Content.Server._NF.GameRule.PointOfInterestPrototype"/>. A POI
/// loads onto the sector map (PointOfInterestSystem.TrySpawnPoiGrid takes the sector's MapId), which leaves the
/// grid physically reachable by anything with a thruster and enough patience; HideWarp and IFF only unlist it.
/// A private map has no route in at all, so every remaining way through is one this system opens on purpose: an
/// admin ghost (the map is an <see cref="AdminOnlyWarpMapComponent"/>), a cleared hull, or an admin-gated job slot.
/// </remarks>
public sealed class HighCommandOutpostRuleSystem : GameRuleSystem<HighCommandOutpostRuleComponent>
{
    [Dependency] private readonly MapLoaderSystem _loader = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly StationRenameWarpsSystems _renameWarps = default!;

    protected override void Started(EntityUid uid,
        HighCommandOutpostRuleComponent component,
        GameRuleComponent gameRule,
        GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);

        // Every addgamerule spawns a fresh rule entity with a fresh component, so an outpost that already stands
        // is found by looking across rules, not at this one.
        var rules = EntityQueryEnumerator<HighCommandOutpostRuleComponent>();
        while (rules.MoveNext(out var otherRule, out var other))
        {
            if (otherRule == uid || other.MapEntity is not { } otherMap || TerminatingOrDeleted(otherMap))
                continue;

            Log.Warning($"{ToPrettyString(uid)} built nothing: the High Command outpost already stands on " +
                        $"{ToPrettyString(otherMap)}, built by {ToPrettyString(otherRule)}.");
            return;
        }

        var map = _map.CreateMap(out var mapId);

        if (!_loader.TryLoadGrid(mapId, component.GridPath, out var grid))
        {
            Log.Error($"Failed to load the High Command outpost grid from {component.GridPath}.");
            QueueDel(map);
            return;
        }

        // Same assertion EmergencyShuttleSystem.AddCentcomm makes: a grid that ended up parented somewhere
        // other than its own map means the load silently landed on the sector, which is the failure this
        // whole design exists to prevent.
        var xform = Transform(grid.Value);
        if (xform.ParentUid != map || xform.MapUid != map)
        {
            Log.Error("High Command outpost grid is not parented to its own map, tearing it down.");
            QueueDel(grid.Value);
            QueueDel(map);
            return;
        }

        component.MapEntity = map;
        component.GridEntity = grid;
        _metaData.SetEntityName(map, Loc.GetString("map-name-tfa-high-command"));

        // Ghosts without an admin rank can neither list nor warp to anything here, players included.
        EnsureComp<AdminOnlyWarpMapComponent>(map);

        component.Station = _station.InitializeNewStation(component.StationConfig, new[] { grid.Value.Owner });

        // The outpost's warp points are admin-only too, enforced in GhostSystem both when listing warps and
        // when performing one.
        _renameWarps.SyncWarpPointsToStation(component.Station.Value, forceAdminOnly: true);

        // InitializeNewStation has already made this map an FTL destination anyone can use (ShuttleSystem
        // registers every station's map on StationPostInitEvent), so it is narrowed to cleared hulls here.
        // requireDisk false: the clearance component is the gate, and stacking a coordinate disk on top would mean
        // an admin has to hand out two things instead of one.
        if (!_shuttle.TryAddFTLDestination(mapId, true, false, false, out var destination))
        {
            Log.Error("Failed to lock down the High Command outpost's FTL destination.");
            return;
        }

        // Tested against the shuttle grid, not the pilot, so this gates hulls rather than players.
        _shuttle.SetFTLWhitelist((map, destination),
            new EntityWhitelist { Components = ["TfaHighCommandClearance"] });
    }

    protected override void Ended(EntityUid uid,
        HighCommandOutpostRuleComponent component,
        GameRuleComponent gameRule,
        GameRuleEndedEvent args)
    {
        base.Ended(uid, component, gameRule, args);

        if (component.Station is { } station)
            _station.DeleteStation(station);

        if (component.MapEntity is { } map && !TerminatingOrDeleted(map))
            QueueDel(map);

        component.Station = null;
        component.GridEntity = null;
        component.MapEntity = null;
    }
}
