using System.IO;
using System.Threading.Tasks;
using Content.Server.Construction.Components;
using Content.Server.Spreader;
using Content.Server._HL.Shipyard; // HardLight
using Content.Shared._Common.Consent; // HardLight
using Content.Shared._HL.Shipyard; // HardLight
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.DeviceLinking;
using Content.Shared.DeviceLinking.Components;
using Content.Shared.Mind.Components; // HardLight
using Content.Shared._NF.Shuttles.Save; // For SendShipSaveDataClientMessage
using Content.Shared.Wall; // WallMountComponent for preserving wall-mounted fixtures
using Robust.Server.Player;
using Robust.Shared.Containers;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes; // HardLight
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Utility;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using Content.Server.Light.Components;
using Content.Shared._Triad.Shipyard;
using System.Linq;
using Content.Shared.Containers;
using Content.Shared.Doors.Components;
using Content.Shared._Mono.ShipRepair.Components;
using Robust.Shared.Collections;
using Content.Shared.NodeContainer;
using Content.Server.Station.Systems;
using Content.Server._NF.ShuttleRecords;

namespace Content.Server._NF.Shipyard.Systems;

/// <summary>
/// System for saving ships using the MapLoaderSystem infrastructure.
/// Saves ships as complete YAML files similar to savegrid command,
/// after cleaning them of problematic components and moving to exports folder.
/// </summary>
public sealed class ShipyardGridSaveSystem : EntitySystem
{
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IEntitySystemManager _entitySystemManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IResourceManager _resourceManager = default!;
    [Dependency] private readonly SharedContainerSystem _containerSystem = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedDeviceLinkSystem _deviceLink = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!; // HardLight
    [Dependency] private readonly SharedTransformSystem _transform = default!; // Triad
    [Dependency] private readonly StationSystem _station = default!; // Triad
    [Dependency] private readonly ShuttleRecordsSystem _shuttleRecords = default!; // Triad

    public List<ShipSaveLimitsPrototype> ShipSaveEntityLimits { get; private set; } = new();

    private ISawmill _sawmill = default!;
    private MapLoaderSystem _mapLoader = default!;

    private readonly HashSet<Entity<SpawnOnShipLoadComponent>> _spawnOnShipLoadEntities = new();
    private readonly HashSet<Entity<ShipSaveLimitComponent>> _limitedEntitiesList = new();

    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<ContainerManagerComponent> _containerManagerQuery;
    private EntityQuery<HLPersistOnShipSaveComponent> _persistOnSaveQuery;
    private EntityQuery<TransformComponent> _transformQuery;


    public override void Initialize()
    {
        base.Initialize();

        _gridQuery = GetEntityQuery<MapGridComponent>();
        _containerManagerQuery = GetEntityQuery<ContainerManagerComponent>();
        _persistOnSaveQuery = GetEntityQuery<HLPersistOnShipSaveComponent>();
        _transformQuery = GetEntityQuery<TransformComponent>();

        // Initialize sawmill for logging
        _sawmill = Logger.GetSawmill("shipyard.gridsave");

        // Get the MapLoaderSystem reference
        _mapLoader = _entitySystemManager.GetEntitySystem<MapLoaderSystem>();

        ShipSaveEntityLimits = GetSaveShipEntityLimits(_prototypeManager);
    }

    /// <summary>
    /// Tries to save a ship, remove the given deed's deed component, and clean up the grid after saving.
    /// </summary>
    public bool TrySaveShip(EntityUid grid, EntityUid deedUid, ICommonSession playerSession)
    {
        if (!TryComp<ShuttleDeedComponent>(deedUid, out var deed))
        {
            _sawmill.Warning($"Player {playerSession.Name} tried to save ship with invalid deed UID: {deedUid}");
            return false;
        }

        var shipName = deed.ShuttleName ?? "Unknown_Ship";

        if (deed.ShuttleUid != grid)
            return false;

        // Integrate with ShipyardGridSaveSystem for ship saving functionality
        _sawmill.Info($"Trying to save {playerSession.Name} ship {shipName}");

        var success = TrySaveGridAsShip(grid, shipName, playerSession.UserId.ToString(), playerSession);

        if (success)
        {
            RemComp<ShuttleDeedComponent>(deedUid);

            // Also remove any other shuttle deeds that reference this shuttle
            RemoveAllShuttleDeeds(grid);

            // Destroy the station on the shuttle
            if (_station.GetOwningStation(grid) is { Valid: true } shuttleStationUid)
                _station.DeleteStation(shuttleStationUid);

            // Delete the shuttle
            QueueDel(grid);
        }
        else
        {
            return false;
        }

        // Update all record UI (skip records, no new records)
        _shuttleRecords.RefreshStateForAll(true);

        return true;
    }

    /// <summary>
    /// Removes all ShuttleDeedComponents that reference the specified shuttle EntityUid
    /// </summary>
    public void RemoveAllShuttleDeeds(EntityUid shuttleUid)
    {
        var query = _entityManager.EntityQueryEnumerator<ShuttleDeedComponent>();
        var deedsToRemove = new List<EntityUid>();

        while (query.MoveNext(out var entityUid, out var deed))
        {
            if (deed.ShuttleUid != null && Exists(deed.ShuttleUid.Value) && deed.ShuttleUid.Value == shuttleUid)
            {
                deedsToRemove.Add(entityUid);
            }
        }

        foreach (var deedEntity in deedsToRemove)
        {
            RemComp<ShuttleDeedComponent>(deedEntity);
        }
    }

    /// <summary>
    /// Goes through a grid and checks for any entities with a SpawnOnShipLoadComponent.
    /// </summary>
    public void CreateSpawnOnShipLoadEntities(EntityUid gridUid)
    {
        if (!_gridQuery.HasComp(gridUid))
            return;

        var toDelete = new HashSet<EntityUid>();

        _spawnOnShipLoadEntities.Clear();

        // Get the entities on the grid with the ship save limit comp
        var gridTransform = _transformQuery.GetComponent(gridUid);
        var worldAABB = _lookup.GetWorldAABB(gridUid, gridTransform);
        _lookup.GetEntitiesIntersecting(gridTransform.MapID, worldAABB, _spawnOnShipLoadEntities);

        foreach ((var ent, var comp) in _spawnOnShipLoadEntities)
        {
            if (ent == gridUid)
                continue;

            var position = _transform.GetMoverCoordinates(ent);
            var newEntity = Spawn(comp.Spawn, position);
            _transform.AttachToGridOrMap(newEntity);

            if (comp.DeleteSelfAfterSpawn)
                toDelete.Add(ent);
        }

        foreach (var uid in toDelete)
        {
            QueueDel(uid);
        }
    }

    /// <summary>
    /// Checks if this grid obeys the limits for certain entities
    /// </summary>
    public bool CheckGridEntityLimits(EntityUid gridUid, out string message)
    {
        message = string.Empty;

        if (!_gridQuery.HasComp(gridUid))
            return false;

        _limitedEntitiesList.Clear();

        var entityAmount = new Dictionary<string, int>();

        // Get the entities on the grid with the ship save limit comp
        var gridTransform = _transformQuery.GetComponent(gridUid);
        var worldAABB = _lookup.GetWorldAABB(gridUid, gridTransform);
        _lookup.GetEntitiesIntersecting(gridTransform.MapID, worldAABB, _limitedEntitiesList);

        foreach ((var ent, var limit) in _limitedEntitiesList)
        {
            if (ent == gridUid)
                continue;

            if (!_transformQuery.TryComp(ent, out var entXForm) || entXForm.GridUid != gridUid)
                continue;

            var limitId = limit.LimitId;
            entityAmount.TryGetValue(limitId, out var count);
            entityAmount[limitId] = count + 1;
        }

        var obeysLimit = true;

        foreach (var (id, amount) in entityAmount)
        {
            foreach (var limitProto in ShipSaveEntityLimits)
            {
                if (!limitProto.Limits.TryGetValue(id, out var max))
                    continue;

                var limitIdLoc = Loc.GetString("shipyard-grid-save-limit-" + id);
                var messagePart = Loc.GetString("shipyard-grid-save-limit-message", ("id", limitIdLoc), ("max", max));

                if (amount > max)
                {
                    message += $"{messagePart}\n";
                    obeysLimit = false;
                }
            }
        }

        return obeysLimit;
    }

    public static List<ShipSaveLimitsPrototype> GetSaveShipEntityLimits(IPrototypeManager prototypeManager)
    {
        return prototypeManager
            .EnumeratePrototypes<ShipSaveLimitsPrototype>()
            .ToList();
    }

    /// <summary>
    /// Saves a grid to YAML without mutating live game state. Uses ShipSerializationSystem to serialize in-place.
    /// This avoids moving the grid to temporary maps or deleting any entities, preventing PVS/map deletion issues.
    /// </summary>
    public bool TrySaveGridAsShip(EntityUid gridUid, string shipName, string playerUserId, ICommonSession playerSession)
    {
        if (!_gridQuery.HasComp(gridUid))
        {
            //_sawmill.Error($"Entity {gridUid} is not a valid grid");
            return false;
        }

        try
        {
            // Clean up broken device links before serialization
            CleanupBrokenDeviceLinks(gridUid);

            // Purge invalid entities
            PurgeInvalidEntities(gridUid);

            // Triad: remove any edge spreaders, we cannot save these
            RemoveEdgeSpreaderComponentComponentsOnGrid(gridUid);

            // Remove repair data, it is re-added on load
            RemComp<ShipRepairDataComponent>(gridUid);

            //_sawmill.Info($"Serializing ship grid {gridUid} as '{shipName}' after transient purge using direct serialization");

            // 1) Serialize the grid and its children to a MappingDataNode (engine-standard format)
            var entities = new HashSet<EntityUid> { gridUid };
            // Prefer AutoInclude to pull in dependent entities; we'll sanitize nullspace and parents out below
            var opts = SerializationOptions.Default with
            {
                // Do NOT auto-include referenced entities (players/admin observers/etc.).
                // This prevents exceptions when encountering unserializable entities and keeps saves scoped to the grid.
                MissingEntityBehaviour = MissingEntityBehaviour.Ignore,
                ErrorOnOrphan = false,
                // Disable auto-include logging to avoid excessive log spam/lag during saves.
                LogAutoInclude = null
            };
            var (node, category) = _mapLoader.SerializeEntitiesRecursive(entities, opts);
            /* if (category != FileCategory.Grid)
            {
                _sawmill.Warning($"Expected FileCategory.Grid but got {category}; continuing with sanitation");
            } */

            // 2) Sanitize the node to match blueprint conventions
            SanitizeShipSaveNode(node);

            // 3) Convert MappingDataNode to YAML text without touching disk
            var yaml = WriteYamlToString(node);

            // 4) Send to client for local saving
            var saveMessage = new SendShipSaveDataClientMessage(shipName, yaml);
            RaiseNetworkEvent(saveMessage, playerSession);
            //_sawmill.Info($"Sent ship data '{shipName}' to client {playerSession.Name} for local saving");

            // Fire ShipSavedEvent for bookkeeping; DO NOT delete the grid or maps here.
            var gridSavedEvent = new ShipSavedEvent
            {
                GridUid = gridUid,
                ShipName = shipName,
                PlayerUserId = playerUserId,
                PlayerSession = playerSession
            };
            RaiseLocalEvent(gridSavedEvent);
            //_sawmill.Info($"Fired ShipSavedEvent for '{shipName}'");

            return true;
        }
        catch (Exception ex)
        {
            Logger.GetSawmill("hardlight").Error($"Ship save failed for '{shipName}' on grid {gridUid}: {ex}");
            return false;
        }
    }

    private void RemoveEdgeSpreaderComponentComponentsOnGrid(EntityUid gridUid)
    {
        var toRemove = new HashSet<EntityUid>();

        var edgeSpreader = _entityManager.EntityQueryEnumerator<EdgeSpreaderComponent, TransformComponent>();
        while (edgeSpreader.MoveNext(out var uid, out var _, out var xform))
        {
            if (xform.GridUid != gridUid)
                continue;
            toRemove.Add(uid);
        }

        foreach (var uid in toRemove)
        {
            Del(uid);
        }
    }

    /// <summary>
    /// Cleans up broken device links where one or both linked entities no longer exist.
    /// Preserves valid links where both source and sink entities are still present.
    /// </summary>
    private void CleanupBrokenDeviceLinks(EntityUid gridUid)
    {
        try
        {
            var linksRemoved = 0;
            var sourcesProcessed = 0;

            // Collect all entities on the grid with device link source components
            var sourceQuery = _entityManager.EntityQueryEnumerator<DeviceLinkSourceComponent, TransformComponent>();
            while (sourceQuery.MoveNext(out var sourceEnt, out var sourceComp, out var xform))
            {
                if (xform.GridUid != gridUid)
                    continue;

                sourcesProcessed++;

                // Check LinkedPorts and remove links to entities that no longer exist
                var brokenSinks = new List<EntityUid>();
                foreach (var sinkEnt in sourceComp.LinkedPorts.Keys)
                {
                    if (!_entityManager.EntityExists(sinkEnt) || _entityManager.IsQueuedForDeletion(sinkEnt))
                    {
                        brokenSinks.Add(sinkEnt);
                    }
                }

                // Use the DeviceLinkSystem to properly remove broken links
                foreach (var brokenSink in brokenSinks)
                {
                    _deviceLink.RemoveSinkFromSource(sourceEnt, brokenSink, sourceComp);
                    linksRemoved++;
                }
            }

            /* if (linksRemoved > 0)
                _sawmill.Info($"CleanupBrokenDeviceLinks: Removed {linksRemoved} broken device link(s) from {sourcesProcessed} source(s) on grid {gridUid}"); */
        }
        catch (Exception e)
        {
            _sawmill.Warning($"CleanupBrokenDeviceLinks: Exception while cleaning device links on grid {gridUid}: {e.Message}");
        }
    }

    /// <summary>
    /// Deletes entities on the grid that should not be persisted with the ship, such as unanchored objects or items not inside of a stash.
    /// </summary>
    private void PurgeInvalidEntities(EntityUid gridUid)
    {
        if (!_gridQuery.HasComp(gridUid))
            return;

        if (!_transformQuery.TryComp(gridUid, out var gridTransform))
            return;

        var entitesToDelete = new List<EntityUid>();

        var toProcess = new ValueList<EntityUid>();
        GetAllEntitiesOnGrid(gridTransform, ref toProcess);

        void ProcessEntityForDeletion(EntityUid uid)
        {
            if (IsInvalidEntity(uid))
            {
                entitesToDelete.Add(uid);
                return;
            }

            if (_containerManagerQuery.TryComp(uid, out var manager))
            {
                foreach (var container in manager.Containers.Values)
                {
                    foreach (var containedEntity in container.ContainedEntities)
                    {
                        ProcessEntityForDeletion(containedEntity);
                    }
                }
            }
        }

        foreach (var uid in toProcess)
        {
            ProcessEntityForDeletion(uid);
        }

        DeleteEntityList(entitesToDelete, "ship save sanitization");
    }

    /// <summary>
    /// Checks if this entity being saved is valid for deletion.
    /// </summary>
    private bool IsInvalidEntity(EntityUid uid)
    {
        if (!Exists(uid))
            return false;
        // Skip if terminating
        if (_entityManager.GetComponent<MetaDataComponent>(uid).EntityLifeStage >= EntityLifeStage.Terminating)
            return false;
        if (HasComp<ConsentComponent>(uid) || HasComp<MindContainerComponent>(uid))
            return true; // do not save things with minds
        if (HasComp<SavingContrabandComponent>(uid))
            return true; // no contra
        if (_persistOnSaveQuery.HasComp(uid))
            return false; // preserve stash root outright
        if (_gridQuery.HasComp(uid))
            return false; // never delete grid root or nested grids here
        // Preserve wall-mounted fixtures (buttons, posters, etc.) regardless of anchored state
        if (HasComp<WallMountComponent>(uid))
            return false;
        // Preserve levers
        if (HasComp<TwoWayLeverComponent>(uid))
            return false;
        // Preserve entities with static body types, such as drains or sinks.
        if (TryComp<PhysicsComponent>(uid, out var physics) && physics.BodyType == BodyType.Static)
            return false;
        // Preserve solutions
        if (HasComp<ContainedSolutionComponent>(uid) || HasComp<SolutionComponent>(uid))
            return false;
        // Save anchored entities
        if (_transformQuery.TryComp(uid, out var xform) && xform.Anchored)
            return false;

        var inContainer = _containerSystem.IsEntityInContainer(uid);
        if (inContainer)
        {
            // If this entity (at any ancestor depth) is ultimately inside a secret stash preserve it.
            if (IsInsidePersistentStorage(uid))
                return false;
        }

        // Only unanchored entities are eligible for deletion. If it's unanchored (loose) or unanchored-in-container, delete.
        return true;
    }

    /// <summary>
    /// Returns true if the given entity is contained in a storage that is considered persistent, such as a machine or ship stash.
    /// </summary>
    private bool IsInsidePersistentStorage(EntityUid ent)
    {
        // Fast path: immediately contained?
        if (!_containerSystem.IsEntityInContainer(ent))
            return false;

        EntityUid current = ent;
        var safety = 0;
        while (safety++ < 64 && _containerSystem.TryGetContainingContainer(current, out var container))
        {
            var owner = container.Owner;
            if (!Exists(owner))
                return false;
            // Also treat persistent entities as a preservation root.
            if (_persistOnSaveQuery.HasComp(owner))
                return true; // Found stash root above.
            if (HasComp<MachineComponent>(owner))
                return true; // This is so machines keep their upgraded parts.
            if (HasComp<AirlockComponent>(owner))
            {
                if (!TryComp<ContainerFillComponent>(owner, out var containerFill) || containerFill.Containers.Count == 0)
                    return true; // To ensure airlocks that aren't prefilled don't have their door electronics deleted
            }
            if (TryComp<PoweredLightComponent>(owner, out var light))
            {
                light.HasLampOnSpawn = null;
                return true; // Preserve lights inside tubes and null their on spawn lamp
            }
            current = owner;
        }
        return false;
    }

    private void DeleteEntityList(List<EntityUid> list, string category)
    {
        foreach (var ent in list)
        {
            try
            {
                if (Exists(ent))
                    Del(ent);
            }
            catch (Exception ex)
            {
                _sawmill.Warning($"Failed deleting {category} entity {ent}: {ex.Message}");
            }
        }
    }

    public static void GetAllEntitiesOnGrid(TransformComponent xform, ref ValueList<EntityUid> reference)
    {
        var childEnumerator = xform.ChildEnumerator;
        while (childEnumerator.MoveNext(out var child))
        {
            reference.Add(child);
        }
    }

    /// <summary>
    /// Remove fields and components from the serialized YAML node to match blueprint output:
    /// - Clear nullspace
    /// - Remove mapInit/paused from entities
    /// - Remove Transform.rot entries
    /// - Remove SpreaderGrid update accumulator
    /// - Remove components: Joint, StationMember, NavMap, ShuttleDeed, IFF, LinkedLifecycleGridParent
    /// </summary>
    private void SanitizeShipSaveNode(MappingDataNode root)
    {
        ShipSaveYamlSanitizer.SanitizeShipSaveNode(root, _prototypeManager); // HardLight
    }

    private string WriteYamlToString(MappingDataNode node)
    {
        // Based on MapLoaderSystem.Write but to a string instead of file
        var document = new YamlDocument(node.ToYaml());
        using var writer = new StringWriter();
        var stream = new YamlStream { document };
        stream.Save(new YamlMappingFix(new Emitter(writer)), false);
        return writer.ToString();
    }

    /// <summary>
    /// Writes YAML data to a temporary file in UserData for loading
    /// </summary>
    public async Task<bool> WriteYamlToUserData(string fileName, string yamlData)
    {
        try
        {
            var userDataPath = _resourceManager.UserData;
            var resPath = new ResPath(fileName);

            await using var stream = userDataPath.OpenWrite(resPath);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(yamlData);

            //_sawmill.Info($"Temporary YAML file written: {resPath}");
            return true;
        }
        catch (Exception ex)
        {
            //_sawmill.Error($"Failed to write temporary YAML file {fileName}: {ex}");
            return false;
        }
    }
}
