using System.IO;
using Content.Server.Construction.Components;
using Content.Server.Spreader;
using Content.Server._HL.Shipyard;
using Content.Shared._Common.Consent;
using Content.Shared._HL.Shipyard;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.DeviceLinking;
using Content.Shared.DeviceLinking.Components;
using Content.Shared.Materials.OreSilo;
using Content.Shared.Mind.Components;
using Content.Shared.Wall;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Markdown.Mapping;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using Content.Server.Lathe.Components;
using Content.Server.Light.Components;
using Content.Shared._Triad.Shipyard.Save;
using Content.Shared.Lathe;
using Content.Shared._Triad.Shipyard.Load;
using Content.Shared._Triad.Shipyard.Save.Contraband;
using System.Linq;
using Content.Shared.Containers;
using Content.Shared.Doors.Components;
using Content.Shared._Mono.ShipRepair.Components;
using Robust.Shared.Collections;
using Content.Server.Station.Systems;
using Content.Server._NF.ShuttleRecords;
using Content.Server.Cargo.Systems;
using Content.Shared._Triad.CCVar;
using Robust.Shared.Configuration;
using Content.Server.GameTicking;
using Content.Server.StationRecords.Components;
using Content.Server.StationRecords.Systems;
using Content.Shared._Triad.ContrabandPermit;

namespace Content.Server._Triad.Shipyard;

/// <summary>
/// System for saving ships using the MapLoaderSystem infrastructure.
/// Saves ships as complete YAML files similar to savegrid command,
/// after cleaning them of problematic components and moving to exports folder.
/// </summary>
public sealed partial class ShipyardGridSaveSystem : EntitySystem
{
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private IEntitySystemManager _entitySystemManager = default!;
    [Dependency] private PricingSystem _pricing = default!;
    [Dependency] private IConfigurationManager _configManager = default!;
    [Dependency] private SharedContainerSystem _containerSystem = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedDeviceLinkSystem _deviceLink = default!;
    [Dependency] private SharedOreSiloSystem _oreSilo = default!;
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private ShuttleRecordsSystem _shuttleRecords = default!;
    [Dependency] private TriadTamperPolicyService _tamperPolicy = default!;
    [Dependency] private GameTicker _gameTicker = default!;

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

            // Destroy the station entity hooked to the shuttle
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
            return false;

        try
        {
            // The part of a save that can actually fail lives in TryBuildShipSaveYaml, so it can be
            // exercised without a session, a signing key or a client to deliver to.
            if (!TryBuildShipSaveYaml(gridUid, out var yaml, out var appraisalCost))
                return false;

            // Triad ship anti-tamper start
            var shipFileBox = _tamperPolicy.SignSave(yaml, appraisalCost);
            _ = _tamperPolicy.RecordSaveAsync(
                shipFileBox,
                playerSession.UserId,
                playerSession.Name,
                shipName,
                appraisalCost,
                signingKeyId: null,
                roundId: _gameTicker.RoundId > 0 ? _gameTicker.RoundId : null,
                serverName: null,
                vesselId: null,
                mapId: null,
                deedHolderEntity: null);
            // Triad ship antitamper end

            // 4) Send to client for local saving
            // Triad: send the signed envelope (ShipFileString) so the client stores the wrapped form,
            // not the raw inner YAML. Fix for the bug acknowledged in the base patch's commit message.
            var saveMessage = new SendShipSaveDataClientMessage(shipName, shipFileBox.ShipFileString());
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

    // The save body, split out of TrySaveGridAsShip so it is testable.
    /// <summary>
    /// The serialization options every ship save runs with. Exposed so tests exercise the options the
    /// live save uses rather than a copy that can drift from them.
    /// </summary>
    internal static readonly SerializationOptions ShipSaveSerializationOptions = SerializationOptions.Default with
    {
        // Do NOT auto-include referenced entities (players/admin observers/etc.).
        // This prevents exceptions when encountering unserializable entities and keeps saves scoped to the grid.
        MissingEntityBehaviour = MissingEntityBehaviour.Ignore,
        ErrorOnOrphan = false,
        // Disable auto-include logging to avoid excessive log spam/lag during saves.
        LogAutoInclude = null
    };

    /// <summary>
    /// Runs the pre-save passes over the live grid, serializes it, sanitizes the node and renders the
    /// YAML. This is everything in a ship save that can throw; signing and delivery stay in
    /// <see cref="TrySaveGridAsShip"/>. Mutates the grid: the purge and the link cleanup are permanent.
    /// </summary>
    /// <returns>False only when <paramref name="gridUid"/> is not a grid. Serialization failures throw.</returns>
    internal bool TryBuildShipSaveYaml(
        EntityUid gridUid,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? yaml,
        out int appraisalCost)
    {
        yaml = null;
        appraisalCost = 0;

        if (!_gridQuery.HasComp(gridUid))
            return false;

        // Purge invalid entities
        PurgeInvalidEntities(gridUid);

        // Runs AFTER the purge so it sees the final entity set. In practice the engine already drops
        // links to purged sinks (SharedDeviceLinkSystem.OnSinkRemoved fires on ComponentRemove during
        // Del), so this ordering is defensive rather than load-bearing; the fix that matters is the
        // off-grid test inside CleanupBrokenDeviceLinks.
        // Clean up broken device links before serialization
        CleanupBrokenDeviceLinks(gridUid);

        // Same treatment for ore-silo links, which are the other two-way EntityUid link on a ship.
        CleanupOffGridOreSiloLinks(gridUid);

        // remove any edge spreaders, we cannot save these
        RemoveEdgeSpreaderComponentComponentsOnGrid(gridUid);

        // Reset fabricators: disable loop/skip and cancel active jobs to prevent mid-save exceptions
        ResetFabricatorsOnGrid(gridUid);

        // reset station records computers to prevent errors with StationRecordsFilter
        ResetGeneralRecordsConsolesOnGrid(gridUid);

        // Remove repair data, it is re-added on load
        RemComp<ShipRepairDataComponent>(gridUid);

        // Remove SpreaderGrid component from grid;
        RemComp<SpreaderGridComponent>(gridUid);

        // 1) Serialize the grid and its children to a MappingDataNode (engine-standard format)
        var entities = new HashSet<EntityUid> { gridUid };

        // these three lines were lifted from the loading code, and should be refactored into a function at some point
        var loadShipPrice = _configManager.GetCVar(TriadCCVars.LoadShipPrice);
        var fullAppraisal = _pricing.AppraiseGrid(gridUid, null);
        appraisalCost = (int)MathF.Round((float)fullAppraisal * loadShipPrice);

        var (node, category) = _mapLoader.SerializeEntitiesRecursive(entities, ShipSaveSerializationOptions);
        /* if (category != FileCategory.Grid)
        {
            _sawmill.Warning($"Expected FileCategory.Grid but got {category}; continuing with sanitation");
        } */

        // 2) Sanitize the node to match blueprint conventions
        SanitizeShipSaveNode(node);

        // 3) Convert MappingDataNode to YAML text without touching disk
        yaml = WriteYamlToString(node);
        return true;
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
    /// Resets all fabricators on the grid before saving: disables loop mode, disables skip mode,
    /// clears the queue, and cancels any active production job.
    /// This prevents the serializer from hitting an in-progress fab state and breaking mid-save.
    /// </summary>
    private void ResetFabricatorsOnGrid(EntityUid gridUid)
    {
        var latheQuery = _entityManager.EntityQueryEnumerator<LatheComponent, TransformComponent>();
        while (latheQuery.MoveNext(out var uid, out var lathe, out var xform))
        {
            if (xform.GridUid != gridUid)
                continue;

            lathe.Loop = false;
            lathe.SkipBad = false;
            lathe.Queue.Clear();
            lathe.CurrentRecipe = null;
            RemComp<LatheProducingComponent>(uid);
        }
    }

    /// <summary>
    /// Resets station records consoles on the grid to prevent serialization issues.
    /// </summary>
    private void ResetGeneralRecordsConsolesOnGrid(EntityUid gridUid)
    {
        var consoleQuery = _entityManager.EntityQueryEnumerator<GeneralStationRecordConsoleComponent, TransformComponent>();
        while (consoleQuery.MoveNext(out var uid, out var console, out var xform))
        {
            if (xform.GridUid != gridUid)
                continue;

            GeneralStationRecordConsoleSystem.SetActiveKey((uid, console), null);
            GeneralStationRecordConsoleSystem.SetFilter((uid, console), null);
        }
    }

    /// <summary>
    /// Cleans up device links that cannot be serialized: links whose sink no longer exists, and links
    /// whose sink is not on this grid. Preserves links whose sink is on the grid being saved.
    /// </summary>
    /// <remarks>
    /// Entity existence is NOT the right test here, which is why this ran for months while
    /// DeviceLinkSource stayed the top cause of failed ship saves. The save runs with
    /// <see cref="MissingEntityBehaviour.Ignore"/>, and EntitySerializer.Write returns the literal
    /// string "invalid" for ANY EntityUid outside the set being serialized, alive or not. Because
    /// DeviceLinkSourceComponent.LinkedPorts is keyed by EntityUid, a link to a live off-grid entity
    /// writes "invalid" exactly like a dead one does, and two such links collide as a duplicate
    /// dictionary key: ArgumentException, and the entire save dies.
    ///
    /// Off-grid links are cleared for good rather than restored after serializing. Their sinks are
    /// round scoped for our purposes, typically remote triggers, so a link that cannot be carried
    /// into the save is not worth carrying on the live ship either -- and clearing it leaves the ship
    /// in the state that was actually saved.
    /// </remarks>
    private void CleanupBrokenDeviceLinks(EntityUid gridUid)
    {
        try
        {
            // Collect all entities on the grid with device link source components
            var sourceQuery = _entityManager.EntityQueryEnumerator<DeviceLinkSourceComponent, TransformComponent>();
            while (sourceQuery.MoveNext(out var sourceEnt, out var sourceComp, out var xform))
            {
                if (xform.GridUid != gridUid)
                    continue;

                // Anything that will not serialize to a real uid: the sink is gone, or it is off-grid.
                var unserializableSinks = new List<EntityUid>();
                foreach (var sinkEnt in sourceComp.LinkedPorts.Keys)
                {
                    if (!_entityManager.EntityExists(sinkEnt) || _entityManager.IsQueuedForDeletion(sinkEnt))
                    {
                        unserializableSinks.Add(sinkEnt);
                        continue;
                    }

                    if (!_transformQuery.TryComp(sinkEnt, out var sinkXform) || sinkXform.GridUid != gridUid)
                        unserializableSinks.Add(sinkEnt);
                }

                // Use the DeviceLinkSystem to properly remove broken links
                foreach (var sinkEnt in unserializableSinks)
                {
                    _deviceLink.RemoveSinkFromSource(sourceEnt, sinkEnt, sourceComp);
                }
            }
        }
        catch (Exception e)
        {
            _sawmill.Warning($"CleanupBrokenDeviceLinks: Exception while cleaning device links on grid {gridUid}: {e.Message}");
        }
    }

    /// <summary>
    /// Clears ore-silo links whose silo is not on the grid being saved, so the save never writes a
    /// reference it cannot resolve.
    /// </summary>
    /// <remarks>
    /// A cross-grid silo link is already dead weight: SharedOreSiloSystem.CanTransmitMaterials refuses
    /// any pair whose grids differ, so a link to a silo on another grid transmits nothing even before
    /// the ship is saved. Carrying it into the save only produces the literal string "invalid" in the
    /// YAML, one deserializer error per load, and a uid that resolves to entity 0 downstream.
    ///
    /// Only the client half needs clearing: OreSiloComponent.Clients is no longer persisted and is
    /// rebuilt from the surviving clients on startup.
    /// </remarks>
    private void CleanupOffGridOreSiloLinks(EntityUid gridUid)
    {
        try
        {
            var query = _entityManager.EntityQueryEnumerator<OreSiloClientComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out var client, out var xform))
            {
                if (xform.GridUid != gridUid)
                    continue;

                if (client.Silo is not { } silo)
                    continue;

                // Keep links whose silo rides along in this same save.
                if (_entityManager.EntityExists(silo)
                    && !_entityManager.IsQueuedForDeletion(silo)
                    && _transformQuery.TryComp(silo, out var siloXform)
                    && siloXform.GridUid == gridUid)
                {
                    continue;
                }

                _oreSilo.ClearSiloLink((uid, client));
            }
        }
        catch (Exception e)
        {
            _sawmill.Warning($"CleanupOffGridOreSiloLinks: Exception while clearing silo links on grid {gridUid}: {e.Message}");
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
        if (HasComp<SavingContrabandComponent>(uid) && !HasComp<ContrabandPermitItemComponent>(uid))
            return true; // No contra, but a permit will allow it
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
            if (_persistOnSaveQuery.TryComp(owner, out var persist) && persist.SaveContents)
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

    private static string WriteYamlToString(MappingDataNode node)
    {
        // Based on MapLoaderSystem.Write but to a string instead of file
        var document = new YamlDocument(node.ToYaml());
        using var writer = new StringWriter();
        var stream = new YamlStream { document };
        stream.Save(new YamlMappingFix(new Emitter(writer)), false);
        return writer.ToString();
    }
}
