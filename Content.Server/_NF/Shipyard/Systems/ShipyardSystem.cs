using Content.Server.Shuttles.Systems;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Station.Systems;
using Content.Shared.Doors.Components;
using Content.Shared.Station.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared.GameTicking;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Content.Shared._NF.CCVar;
using Robust.Shared.Configuration;
using System.Diagnostics.CodeAnalysis;
using System.IO; // HardLight
using System.Linq;
using System.Numerics;
using System.Text; // HardLight
using System.Text.RegularExpressions; // HardLight
using Robust.Shared.Serialization; // HardLight
using Robust.Shared.Serialization.Markdown; // HardLight
using Robust.Shared.Serialization.Markdown.Mapping; // HardLight
using Robust.Shared.Serialization.Markdown.Sequence; // HardLight
using Robust.Shared.Serialization.Markdown.Value; // HardLight
using Content.Shared._NF.Shipyard.Events;
using Content.Shared.Mobs.Components;
using Robust.Shared.Containers;
using Content.Server._NF.Station.Components;
using Content.Server.Storage.Components;
using Content.Server.Shuttles.Save;
using Content.Shared._Mono.Shipyard;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Utility;
using Robust.Shared.ContentPack;
using Content.Shared.Shuttles.Components; // For IFFComponent
using Content.Shared.Timing;
using Content.Server.Gravity;
using Robust.Shared.Physics;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components; // For GravitySystem
using Robust.Shared.Map.Events; // HardLight
using YamlDotNet.Core; // HardLight
using YamlDotNet.RepresentationModel;
using Content.Shared._Crescent.ShipShields; // HardLight

namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem : SharedShipyardSystem
{
    private static readonly Regex ShipSaveProtoLineRegex = new(@"^(\s*)- proto:\s*(.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant); // HardLight
    private static readonly Regex ShipSaveUidLineRegex = new(@"^\s*- uid:\s*\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant); // HardLight
    private static readonly Regex ShipSaveUidCaptureLineRegex = new(@"^\s*-\s*uid:\s*(\d+)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant); // HardLight
    private static readonly Regex ShipSaveEntitiesSectionRegex = new(@"^(\s*)entities\s*:\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant); // HardLight
    private static readonly Regex ShipSaveLegacyUidLineRegex = new(@"^(\s*)-\s*uid\s*:\s*\d+\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant); // HardLight
    private static readonly Regex ShipSaveLegacyTypeLineRegex = new(@"^\s*type\s*:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant); // HardLight

    // HardLight: Set of tokens that, if found as UIDs in the YAML, indicate a stale or invalid UID
    // that should be sanitized during load to prevent deserialization failures.
    private static readonly HashSet<string> StaleSerializedUidTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "invalid",
        "null",
        "~",
        "0",
    };

    [Dependency] private readonly IConfigurationManager _configManager = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly DockingSystem _docking = default!;
    [Dependency] private readonly PricingSystem _pricing = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly MapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ShipOwnershipSystem _shipOwnership = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IEntitySystemManager _entitySystemManager = default!;
    [Dependency] private readonly IResourceManager _resources = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!; // For safe container removal before deletion
    [Dependency] private readonly UseDelaySystem _useDelay = default!;
    [Dependency] private readonly GravitySystem _gravitySystem = default!; // For post-load gravity refresh
    [Dependency] private readonly ShipSerializationSystem _shipSerialization = default!; // HardLight
    [Dependency] private readonly ShipyardGridSaveSystem _shipyardGridSave = default!; // Triad

    private EntityQuery<TransformComponent> _transformQuery;

    public MapId? ShipyardMap { get; private set; }
    private float _shuttleIndex;
    private const float ShuttleSpawnBuffer = 1f;
    private ISawmill _sawmill = default!;
    private bool _enabled;
    private float _baseSaleRate;
    private readonly Dictionary<EntityUid, TimeSpan> _lastLoadCharge = new(); // Per-player load charge cooldown
    private readonly Dictionary<EntityUid, TimeSpan> _shipyardActionDelayUntil = new(); // HardLight
    private static readonly TimeSpan ShipyardActionDelay = TimeSpan.FromSeconds(1); // HardLight
    private HashSet<string>? _activeLoadDeletedPrototypes; // HardLight

    // The type of error from the attempted sale of a ship.
    public enum ShipyardSaleError
    {
        Success, // Ship can be sold.
        Undocked, // Ship is not docked with the station.
        OrganicsAboard, // Sapient intelligence is aboard, cannot sell, would delete the organics
        InvalidShip, // Ship is invalid
        MessageOverwritten, // Overwritten message.
    }

    // TODO: swap to strictly being a formatted message.
    public struct ShipyardSaleResult
    {
        public ShipyardSaleError Error; // Whether or not the ship can be sold.
        public string? OrganicName; // In case an organic is aboard, this will be set to the first that's aboard.
        public string? OverwrittenMessage; // The message to write if Error is MessageOverwritten.
    }

    public override void Initialize()
    {
        base.Initialize();

        _transformQuery = GetEntityQuery<TransformComponent>();

        // FIXME: Load-bearing jank - game doesn't want to create a shipyard map at this point.
        _enabled = _configManager.GetCVar(NFCCVars.Shipyard);
        _configManager.OnValueChanged(NFCCVars.Shipyard, SetShipyardEnabled); // NOTE: run immediately set to false, see comment above

        _configManager.OnValueChanged(NFCCVars.ShipyardSellRate, SetShipyardSellRate, true);
        _sawmill = Logger.GetSawmill("shipyard");

        SubscribeLocalEvent<ShipyardConsoleComponent, ComponentStartup>(OnShipyardStartup);
        SubscribeLocalEvent<ShipyardConsoleComponent, BoundUIOpenedEvent>(OnConsoleUIOpened);
        SubscribeLocalEvent<ShipyardConsoleComponent, ShipyardConsoleSellMessage>(OnSellMessage);
        SubscribeLocalEvent<ShipyardConsoleComponent, ShipyardConsolePurchaseMessage>(OnPurchaseMessage);
        SubscribeLocalEvent<ShipyardConsoleComponent, ShipyardConsoleUnassignDeedMessage>(OnUnassignDeedMessage);
        SubscribeLocalEvent<ShipyardConsoleComponent, ShipyardConsoleRenameMessage>(OnRenameMessage);
        // Ship saving/loading functionality
        SubscribeLocalEvent<ShipyardConsoleComponent, ShipyardConsoleSaveMessage>(OnSaveMessage); // Triad
        SubscribeLocalEvent<ShipyardConsoleComponent, ShipyardConsoleLoadMessage>(OnLoadMessage);
        SubscribeLocalEvent<ShipyardConsoleComponent, EntInsertedIntoContainerMessage>(OnItemSlotChanged);
        SubscribeLocalEvent<ShipyardConsoleComponent, EntRemovedFromContainerMessage>(OnItemSlotChanged);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<StationDeedSpawnerComponent, MapInitEvent>(OnInitDeedSpawner);
        SubscribeLocalEvent<BeforeEntityReadEvent>(OnBeforeEntityRead); // HardLight
    }

    public override void Shutdown()
    {
        _configManager.UnsubValueChanged(NFCCVars.Shipyard, SetShipyardEnabled);
        _configManager.UnsubValueChanged(NFCCVars.ShipyardSellRate, SetShipyardSellRate);
    }
    private void OnShipyardStartup(EntityUid uid, ShipyardConsoleComponent component, ComponentStartup args)
    {
        if (!_enabled)
            return;
        InitializeConsole();
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        CleanupShipyard();
    }

    // HardLight: Injects deleted prototype IDs into a temporary set used during ship load to allow YAML loads to succeed by ignoring missing prototypes,
    // while still logging their absence for debugging and future cleanup.
    private void OnBeforeEntityRead(BeforeEntityReadEvent ev)
    {
        if (_activeLoadDeletedPrototypes == null || _activeLoadDeletedPrototypes.Count == 0)
            return;

        foreach (var prototypeId in _activeLoadDeletedPrototypes)
        {
            ev.DeletedPrototypes.Add(prototypeId);
        }
    }

    private void SetShipyardEnabled(bool value)
    {
        if (_enabled == value)
            return;

        _enabled = value;

        if (value)
            SetupShipyardIfNeeded();
        else
            CleanupShipyard();
    }

    private void SetShipyardSellRate(float value)
    {
        _baseSaleRate = Math.Clamp(value, 0.0f, 1.0f);
    }

    /// <summary>
    /// Adds a ship to the shipyard, calculates its price, and attempts to ftl-dock it to the given station
    /// </summary>
    /// <param name="stationUid">The ID of the station to dock the shuttle to</param>
    /// <param name="shuttlePath">The path to the shuttle file to load. Must be a grid file!</param>
    /// <param name="shuttleEntityUid">The EntityUid of the shuttle that was purchased</param>
    public bool TryPurchaseShuttle(EntityUid stationUid, ResPath shuttlePath, [NotNullWhen(true)] out EntityUid? shuttleEntityUid)
    {
        if (!TryComp<StationDataComponent>(stationUid, out var stationData)
            || !TryAddShuttle(shuttlePath, out var shuttleGrid)
            || !TryComp<ShuttleComponent>(shuttleGrid, out var shuttleComponent))
        {
            shuttleEntityUid = null;
            return false;
        }

        var price = _pricing.AppraiseGrid(shuttleGrid.Value, null);
        var targetGrid = _station.GetLargestGrid(stationData);

        if (targetGrid == null) //how are we even here with no station grid
        {
            QueueDel(shuttleGrid);
            shuttleEntityUid = null;
            return false;
        }

        _sawmill.Info($"Shuttle {shuttlePath} was purchased at {ToPrettyString(stationUid)} for {price:f2}");
        var ev = new ShipBoughtEvent();
        RaiseLocalEvent(shuttleGrid.Value, ev);
        //can do TryFTLDock later instead if we need to keep the shipyard map paused
        _shuttle.TryFTLDock(shuttleGrid.Value, shuttleComponent, targetGrid.Value);
        shuttleEntityUid = shuttleGrid;
        return true;
    }

    /// <summary>
    /// Loads a shuttle from a file and docks it to the grid the console is on, like ship purchases.
    /// This is used for loading saved ships.
    /// </summary>
    /// <param name="consoleUid">The entity of the shipyard console to dock to its grid</param>
    /// <param name="shuttlePath">The path to the shuttle file to load. Must be a grid file!</param>
    /// <param name="shuttleEntityUid">The EntityUid of the shuttle that was loaded</param>
    public bool TryPurchaseShuttleFromFile(EntityUid consoleUid, ResPath shuttlePath, [NotNullWhen(true)] out EntityUid? shuttleEntityUid)
    {
        if (!TryAddShuttle(shuttlePath, out var shuttleGrid)) // HardLight
        {
            shuttleEntityUid = null;
            return false;
        }

        return TryFinalizeLoadedShuttle(consoleUid, shuttleGrid.Value, out shuttleEntityUid); // HardLight
    }

    /// <summary>
    /// HardLight: Loads a shuttle into the ShipyardMap from a file path
    /// </summary>
    /// <param name="shuttlePath">The path to the grid file to load. Must be a grid file!</param>
    /// <returns>Returns the EntityUid of the shuttle</returns>
    private bool TryAddShuttle(ResPath shuttlePath, [NotNullWhen(true)] out EntityUid? shuttleGrid)
    {
        shuttleGrid = null;
        SetupShipyardIfNeeded();
        if (ShipyardMap == null)
            return false;

        if (!_mapLoader.TryLoadGrid(ShipyardMap.Value, shuttlePath, out var grid, offset: new Vector2(500f + _shuttleIndex, 1f)))
        {
            //_sawmill.Error($"Unable to spawn shuttle {shuttlePath}");
            return false;
        }

        _shuttleIndex += grid.Value.Comp.LocalAABB.Width + ShuttleSpawnBuffer;

        shuttleGrid = grid.Value.Owner;
        return true;
    }

    /// <summary>
    /// HardLight: Writes YAML data to a temporary file and attempts the same initial strict load path as purchase-from-file.
    /// If that fails, applies compatibility recovery stages before falling back to tolerant ship-data reconstruction.
    /// </summary>
    private bool TryPurchaseShuttleFromYamlData(EntityUid consoleUid, string yamlData, [NotNullWhen(true)] out EntityUid? shuttleEntityUid)
    {
        shuttleEntityUid = null;
        ResPath tempPath = default;
        try
        {
            // Create a temp path under UserData/ShipyardTemp
            var fileName = $"shipyard_load_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.yml";
            var dir = new ResPath("/") / "UserData" / "ShipyardTemp";
            tempPath = dir / fileName;

            // Ensure directory exists and write file
            _resources.UserData.CreateDir(dir);
            using (var writer = _resources.UserData.OpenWriteText(tempPath))
            {
                writer.Write(yamlData);
            }

            // Fast path: strict load with original YAML; no extra scanning work.
            if (TryPurchaseShuttleFromFileSafe(consoleUid, tempPath, out shuttleEntityUid))
                return true;

            _sawmill.Debug("[ShipLoad] Strict grid YAML load failed; attempting compatibility recovery stages.");

            var recoveryYaml = yamlData;

            // Recovery stage A: sanitize missing prototypes and inject deleted prototype IDs.
            var sanitizedYaml = SanitizeLoadYamlMissingPrototypes(yamlData, out var removedProtoBlocks, out var removedEntities);
            var deletedPrototypeIds = FindMissingPrototypeIdsForLoad(sanitizedYaml);
            var needsSanitizedRetry = removedProtoBlocks > 0
                                      || deletedPrototypeIds.Count > 0
                                      || !string.Equals(sanitizedYaml, yamlData, StringComparison.Ordinal);

            if (needsSanitizedRetry)
            {
                if (removedProtoBlocks > 0)
                {
                    _sawmill.Warning($"[ShipLoad] Removed {removedProtoBlocks} invalid prototype block(s) containing {removedEntities} entities from ship YAML before load.");
                }

                if (deletedPrototypeIds.Count > 0)
                {
                    _sawmill.Warning($"[ShipLoad] Ignoring {deletedPrototypeIds.Count} missing prototype id(s) during ship load.");
                }

                _activeLoadDeletedPrototypes = deletedPrototypeIds.Count > 0 ? deletedPrototypeIds : null;
                recoveryYaml = sanitizedYaml;

                using (var retryWriter = _resources.UserData.OpenWriteText(tempPath))
                {
                    retryWriter.Write(recoveryYaml);
                }

                if (TryPurchaseShuttleFromFileSafe(consoleUid, tempPath, out shuttleEntityUid))
                    return true;
            }

            // Recovery path: strip serialized component payloads and retry strict load.
            // This salvages ships when component schemas changed between versions.
            var strippedYaml = StripSerializedComponentsForRecovery(recoveryYaml);
            if (!string.Equals(strippedYaml, recoveryYaml, StringComparison.Ordinal))
            {
                using (var retryWriter = _resources.UserData.OpenWriteText(tempPath))
                {
                    retryWriter.Write(strippedYaml);
                }

                if (TryPurchaseShuttleFromFileSafe(consoleUid, tempPath, out shuttleEntityUid))
                {
                    _sawmill.Warning("[ShipLoad] Loaded ship after stripping serialized component payloads for compatibility recovery.");
                    return true;
                }

                _sawmill.Debug("[ShipLoad] Component-stripped recovery load failed.");
            }

            // Fallback: ship-data YAML path tolerates per-entity failures and skips bad entities.
            if (TryPurchaseShuttleFromShipDataYaml(consoleUid, recoveryYaml, out shuttleEntityUid))
                return true;

            _sawmill.Warning("[ShipLoad] Ship-data tolerant fallback also failed.");

            return false;
        }
        catch (Exception ex)
        {
            _sawmill.Warning($"Failed to purchase shuttle from YAML data: {ex.Message}"); // HardLight: Error<Warning
            return false;
        }
        finally
        {
            _activeLoadDeletedPrototypes = null;

            try
            {
                if (tempPath != default && _resources.UserData.Exists(tempPath))
                    _resources.UserData.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    // HardLight: Wraps TryPurchaseShuttleFromFile in a try-catch to prevent exceptions from bubbling up and crashing the load process
    private bool TryPurchaseShuttleFromFileSafe(EntityUid consoleUid, ResPath shuttlePath, [NotNullWhen(true)] out EntityUid? shuttleEntityUid)
    {
        shuttleEntityUid = null;

        try
        {
            return TryPurchaseShuttleFromFile(consoleUid, shuttlePath, out shuttleEntityUid);
        }
        catch (Exception ex)
        {
            _sawmill.Debug($"[ShipLoad] Strict load stage threw exception: {ex.Message}");
            return false;
        }
    }

    // HardLight: Loads a shuttle from YAML data using a tolerant fallback path that skips over entities with missing prototypes.
    private bool TryPurchaseShuttleFromShipDataYaml(EntityUid consoleUid, string yamlData, [NotNullWhen(true)] out EntityUid? shuttleEntityUid)
    {
        shuttleEntityUid = null;

        SetupShipyardIfNeeded();
        if (ShipyardMap == null)
            return false;

        try
        {
            var shipData = _shipSerialization.DeserializeShipGridDataFromYaml(yamlData, Guid.Empty, out _);
            var grid = _shipSerialization.ReconstructShipOnMap(shipData, ShipyardMap.Value, new Vector2(500f + _shuttleIndex, 1f));

            if (!TryComp<MapGridComponent>(grid, out var gridComp))
            {
                _sawmill.Warning("[ShipLoad] Ship-data fallback created no grid component.");
                return false;
            }

            _shuttleIndex += gridComp.LocalAABB.Width + ShuttleSpawnBuffer;

            if (!TryFinalizeLoadedShuttle(consoleUid, grid, out shuttleEntityUid))
            {
                SafeDelete(grid);
                return false;
            }

            _sawmill.Info("[ShipLoad] Loaded ship via tolerant ship-data fallback path.");
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Warning($"[ShipLoad] Ship-data fallback failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Mono: Adds ShipAccessReaderComponent to all doors and lockers on a ship grid.
    /// </summary>
    private void AddShipAccessToEntities(EntityUid gridUid)
    {
        // Get the grid bounds to find all entities on the grid
        if (!TryComp<MapGridComponent>(gridUid, out var grid))
            return;

        var gridBounds = grid.LocalAABB;
        var gridEntities = new HashSet<EntityUid>();
        _lookup.GetLocalEntitiesIntersecting(gridUid, gridBounds, gridEntities);

        foreach (var entity in gridEntities)
        {
            // Add ship access to doors
            if (HasComp<DoorComponent>(entity))
            {
                EnsureComp<ShipAccessReaderComponent>(entity);
            }
            // Add ship access to entity storage (lockers, crates, etc.)
            else if (HasComp<EntityStorageComponent>(entity))
            {
                EnsureComp<ShipAccessReaderComponent>(entity);
            }
        }
    }

    // HardLight: Performs final setup and docking for a loaded shuttle, with error handling to prevent load crashes.
    private bool TryFinalizeLoadedShuttle(EntityUid consoleUid, EntityUid grid, [NotNullWhen(true)] out EntityUid? shuttleEntityUid)
    {
        shuttleEntityUid = null;

        // Get the grid the console is on
        if (!_transformQuery.TryComp(consoleUid, out var consoleXform) || consoleXform.GridUid == null)
            return false;

        if (!TryComp<ShuttleComponent>(grid, out var shuttleComponent))
            return false;

        if (TryComp<ShipShieldedComponent>(grid, out var shielded))
        {
            TryQueueDel(shielded.Shield);
            RemComp<ShipShieldedComponent>(grid);
        }

        // Ensure required components for docking and identification
        EnsureComp<PhysicsComponent>(grid);
        EnsureComp<ShuttleComponent>(grid);
        EnsureComp<IFFComponent>(grid);

        // Reset use delays on objects with the component so delays from previous rounds don't carry over
        TryResetUseDelays(grid);

        // Spawn the entities from entities with SpawnOnShipLoadComponent
        _shipyardGridSave.CreateSpawnOnShipLoadEntities(grid);

        // Load-time sanitation: purge any deserialized joints and reset dock joint references
        // to avoid physics processing invalid joint bodies (e.g., Entity 0) from YAML.
        try
        {
            PurgeJointsAndResetDocks(grid);
        }
        catch (Exception ex)
        {
            _sawmill.Warning($"[ShipLoad] PurgeJointsAndResetDocks failed on {grid}: {ex.Message}");
        }
        // Add new grid to the same station as the console's grid (for IFF / ownership), if any
        var consoleGridUid = consoleXform.GridUid.Value;
        if (TryComp<StationMemberComponent>(consoleGridUid, out var stationMember))
        {
            _station.AddGridToStation(stationMember.Station, grid);
        }

        _shuttle.TryFTLDock(grid, shuttleComponent, consoleGridUid);
        shuttleEntityUid = grid;
        return true;
    }

    /// <summary>
    /// HardLight: Removes serialized entity groups whose prototype IDs no longer exist in code.
    /// This lets old ship exports load even after content deprecations.
    /// </summary>
    private string SanitizeLoadYamlMissingPrototypes(string yamlData, out int removedPrototypeBlocks, out int removedEntities)
    {
        removedPrototypeBlocks = 0;
        removedEntities = 0;

        if (string.IsNullOrWhiteSpace(yamlData))
            return yamlData;

        // Quick exit: if no grouped entity prototype declarations exist there is nothing to strip here.
        if (yamlData.IndexOf("- proto:", StringComparison.Ordinal) < 0)
            return yamlData;

        var normalized = yamlData.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var output = new StringBuilder(normalized.Length);
        var removedEntityUids = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var protoMatch = ShipSaveProtoLineRegex.Match(line);

            if (!protoMatch.Success)
            {
                output.AppendLine(line);
                continue;
            }

            var indent = protoMatch.Groups[1].Value;
            var rawProto = protoMatch.Groups[2].Value;
            var commentIndex = rawProto.IndexOf('#');
            if (commentIndex >= 0)
                rawProto = rawProto[..commentIndex];

            var protoId = rawProto.Trim().Trim('"', '\'');

            // Empty proto blocks contain runtime/synthetic entities and should be retained.
            var keepBlock = string.IsNullOrWhiteSpace(protoId) || _prototypeManager.TryIndex<Robust.Shared.Prototypes.EntityPrototype>(protoId, out _);
            if (keepBlock)
            {
                output.AppendLine(line);
                continue;
            }

            removedPrototypeBlocks++;

            // Skip this whole proto block until the next proto declaration at the same indentation level.
            for (i += 1; i < lines.Length; i++)
            {
                var blockLine = lines[i];
                if (ShipSaveUidLineRegex.IsMatch(blockLine))
                {
                    removedEntities++;
                    var uidMatch = ShipSaveUidCaptureLineRegex.Match(blockLine);
                    if (uidMatch.Success)
                        removedEntityUids.Add(uidMatch.Groups[1].Value);
                }

                var nextProto = ShipSaveProtoLineRegex.Match(blockLine);
                if (!nextProto.Success)
                    continue;

                var nextIndent = nextProto.Groups[1].Value;
                if (nextIndent != indent)
                    continue;

                i -= 1;
                break;
            }
        }

        var sanitizedYaml = output.ToString();
        if (removedEntityUids.Count == 0)
            return sanitizedYaml;

        // Keep parent containers while removing only missing-prototype entities by pruning stale UID references.
        return PruneLoadYamlReferencesToRemovedEntities(sanitizedYaml, removedEntityUids);
    }

    /// <summary>
    /// HardLight: Removes stale references to entities stripped during missing-prototype sanitation.
    /// This preserves container/storage owner entities when only their contained items were removed.
    /// </summary>
    private static string PruneLoadYamlReferencesToRemovedEntities(string yamlData, HashSet<string> removedEntityUids)
    {
        if (string.IsNullOrWhiteSpace(yamlData) || removedEntityUids.Count == 0)
            return yamlData;

        // Structured path: parse YAML into a data node tree and prune stale UID references precisely.
        // Falls back to line-based pruning if parsing fails on malformed legacy input.
        try
        {
            using var reader = new StringReader(yamlData);
            var documents = DataNodeParser.ParseYamlStream(reader).ToArray();
            if (documents.Length != 1 || documents[0].Root is not MappingDataNode root)
                return PruneLoadYamlReferencesToRemovedEntitiesLineBased(yamlData, removedEntityUids);

            PruneLoadNodeReferencesToRemovedEntities(root, removedEntityUids);
            return WriteLoadYamlNodeToString(root);
        }
        catch
        {
            return PruneLoadYamlReferencesToRemovedEntitiesLineBased(yamlData, removedEntityUids);
        }
    }

    // HardLight: Structured node pass that prunes stale/invalid UID references from container/storage data during load recovery.
    private static void PruneLoadNodeReferencesToRemovedEntities(MappingDataNode root, HashSet<string> removedEntityUids)
    {
        if (!root.TryGet("entities", out SequenceDataNode? protoSeq) || protoSeq == null)
            return;

        foreach (var protoNode in protoSeq)
        {
            if (protoNode is not MappingDataNode protoMap)
                continue;

            if (!protoMap.TryGet("entities", out SequenceDataNode? entitiesSeq) || entitiesSeq == null)
                continue;

            foreach (var entityNode in entitiesSeq)
            {
                if (entityNode is not MappingDataNode entMap)
                    continue;

                if (!entMap.TryGet("components", out SequenceDataNode? comps) || comps == null)
                    continue;

                foreach (var compNode in comps)
                {
                    if (compNode is not MappingDataNode compMap)
                        continue;

                    if (!compMap.TryGet("type", out ValueDataNode? typeNode) || typeNode == null)
                        continue;

                    var componentType = typeNode.Value;

                    if (componentType == "ContainerContainer")
                    {
                        if (!compMap.TryGet("containers", out MappingDataNode? containersMap) || containersMap == null)
                            continue;

                        foreach (var (_, containerNode) in containersMap)
                        {
                            if (containerNode is not MappingDataNode containerMap)
                                continue;

                            if (containerMap.TryGet("ents", out SequenceDataNode? entsNode) && entsNode != null)
                            {
                                for (var idx = entsNode.Count - 1; idx >= 0; idx--)
                                {
                                    if (entsNode[idx] is not ValueDataNode entValue || entValue.IsNull)
                                        continue;

                                    if (IsStaleSerializedUidReference(entValue.Value, removedEntityUids))
                                        entsNode.RemoveAt(idx);
                                }
                            }

                            if (containerMap.TryGet("ent", out ValueDataNode? entNode) && entNode != null && !entNode.IsNull)
                            {
                                if (IsStaleSerializedUidReference(entNode.Value, removedEntityUids))
                                    containerMap["ent"] = ValueDataNode.Null();
                            }
                        }

                        continue;
                    }

                    if (componentType != "Storage"
                        || !compMap.TryGet("storedItems", out MappingDataNode? storedItemsMap)
                        || storedItemsMap == null)
                    {
                        continue;
                    }

                    var removeKeys = new List<string>();
                    foreach (var (itemUid, _) in storedItemsMap)
                    {
                        if (IsStaleSerializedUidReference(itemUid, removedEntityUids))
                            removeKeys.Add(itemUid);
                    }

                    foreach (var key in removeKeys)
                        storedItemsMap.Remove(key);
                }
            }
        }
    }

    // HardLight: Serializes a YAML data node tree back into a string, ensuring consistent formatting.
    private static string WriteLoadYamlNodeToString(MappingDataNode root)
    {
        var document = new YamlDocument(root.ToYaml());
        using var writer = new StringWriter();
        var stream = new YamlStream { document };
        stream.Save(new YamlMappingFix(new Emitter(writer)), false);
        return writer.ToString();
    }

    // HardLight: Line-based fallback for pruning stale UID references when YAML is too malformed for structured parsing.
    private static string PruneLoadYamlReferencesToRemovedEntitiesLineBased(string yamlData, HashSet<string> removedEntityUids)
    {
        if (string.IsNullOrWhiteSpace(yamlData))
            return yamlData;

        var normalized = yamlData.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var output = new StringBuilder(normalized.Length);

        var entsIndent = -1;
        var storedItemsIndent = -1;
        var skipSubtreeIndent = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            if (skipSubtreeIndent >= 0)
            {
                if (trimmed.Length == 0)
                    continue;

                if (indent > skipSubtreeIndent)
                    continue;

                skipSubtreeIndent = -1;
            }

            if (trimmed.Length == 0)
            {
                output.AppendLine(line);
                continue;
            }

            if (entsIndent >= 0 && indent <= entsIndent)
                entsIndent = -1;

            if (storedItemsIndent >= 0 && indent <= storedItemsIndent)
                storedItemsIndent = -1;

            if (trimmed.StartsWith("ents:", StringComparison.Ordinal))
            {
                entsIndent = indent;
                output.AppendLine(line);
                continue;
            }

            if (trimmed.StartsWith("storedItems:", StringComparison.Ordinal))
            {
                storedItemsIndent = indent;
                output.AppendLine(line);
                continue;
            }

            // Prune sequence entries in ContainerContainer.ents lists.
            if (entsIndent >= 0 && indent > entsIndent)
            {
                var listEntry = trimmed;
                if (listEntry.StartsWith("- ", StringComparison.Ordinal))
                    listEntry = listEntry[2..].Trim();

                if (IsStaleSerializedUidReference(listEntry, removedEntityUids))
                    continue;
            }

            // Null stale single-reference entries in ContainerContainer.ent fields.
            if (trimmed.StartsWith("ent:", StringComparison.Ordinal))
            {
                var entValue = trimmed[4..].Trim();
                if (IsStaleSerializedUidReference(entValue, removedEntityUids))
                {
                    output.Append(' ', indent);
                    output.AppendLine("ent: null");
                    continue;
                }
            }

            // Remove Storage.storedItems entries keyed by removed entity UID.
            if (storedItemsIndent >= 0 && indent > storedItemsIndent)
            {
                var keySpan = trimmed;
                var colonIndex = keySpan.IndexOf(':');
                if (colonIndex > 0)
                {
                    var rawKey = keySpan[..colonIndex].Trim().Trim('"', '\'');
                    if (IsStaleSerializedUidReference(rawKey, removedEntityUids))
                    {
                        skipSubtreeIndent = indent;
                        continue;
                    }
                }
            }

            output.AppendLine(line);
        }

        return output.ToString();
    }

    // HardLight: Checks if a UID token from YAML matches known patterns of stale references to entities removed during load sanitation.
    private static bool IsStaleSerializedUidReference(string uidToken, HashSet<string> removedEntityUids)
    {
        var normalized = uidToken.Trim().Trim('"', '\'');
        if (normalized.Length == 0)
            return false;

        if (removedEntityUids.Contains(normalized))
            return true;

        return StaleSerializedUidTokens.Contains(normalized);
    }

    /// <summary>
    /// HardLight: Finds missing prototype IDs referenced by ship YAML in both grouped (proto) and legacy (uid/type) entity formats.
    /// Returned IDs are used as a temporary deleted-prototype map during this specific load operation.
    /// </summary>
    private HashSet<string> FindMissingPrototypeIdsForLoad(string yamlData)
    {
        var missing = new HashSet<string>();

        if (string.IsNullOrWhiteSpace(yamlData))
            return missing;

        // Quick exit when there is no entities section.
        if (yamlData.IndexOf("entities:", StringComparison.OrdinalIgnoreCase) < 0)
            return missing;

        var normalized = yamlData.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');

        var inEntities = false;
        var entitiesIndent = -1;
        var currentLegacyEntityIndent = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            var indent = line.Length - trimmed.Length;
            var entitiesSection = ShipSaveEntitiesSectionRegex.Match(line);
            if (!inEntities && entitiesSection.Success)
            {
                inEntities = true;
                entitiesIndent = entitiesSection.Groups[1].Value.Length;
                continue;
            }

            if (!inEntities)
                continue;

            // Left the entities section.
            if (indent <= entitiesIndent)
            {
                inEntities = false;
                currentLegacyEntityIndent = -1;
                continue;
            }

            // Grouped format: "- proto: <id>"
            var protoMatch = ShipSaveProtoLineRegex.Match(line);
            if (protoMatch.Success)
            {
                var protoId = ParseShipSavePrototypeValue(protoMatch.Groups[2].Value);
                if (!string.IsNullOrWhiteSpace(protoId)
                    && !_prototypeManager.TryIndex<Robust.Shared.Prototypes.EntityPrototype>(protoId, out _))
                {
                    missing.Add(protoId);
                }

                currentLegacyEntityIndent = -1;
                continue;
            }

            // Legacy format entity boundary: "- uid: <n>"
            var uidMatch = ShipSaveLegacyUidLineRegex.Match(line);
            if (uidMatch.Success)
            {
                currentLegacyEntityIndent = uidMatch.Groups[1].Value.Length;
                continue;
            }

            // If we're no longer in the current legacy entity block, clear it.
            if (currentLegacyEntityIndent >= 0 && indent <= currentLegacyEntityIndent)
            {
                currentLegacyEntityIndent = -1;
            }

            if (currentLegacyEntityIndent < 0)
                continue;

            // Legacy format prototype: "type: <id>" under the current uid block.
            var legacyTypeMatch = ShipSaveLegacyTypeLineRegex.Match(line);
            if (!legacyTypeMatch.Success)
                continue;

            var legacyProtoId = ParseShipSavePrototypeValue(legacyTypeMatch.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(legacyProtoId)
                && !_prototypeManager.TryIndex<Robust.Shared.Prototypes.EntityPrototype>(legacyProtoId, out _))
            {
                missing.Add(legacyProtoId);
            }
        }

        return missing;
    }

    // HardLight: Extracts the prototype ID from a raw YAML line value,
    // stripping comments and extraneous whitespace/quotes.
    private static string ParseShipSavePrototypeValue(string rawValue)
    {
        var commentIndex = rawValue.IndexOf('#');
        if (commentIndex >= 0)
            rawValue = rawValue[..commentIndex];

        return rawValue.Trim().Trim('"', '\'');
    }

    /// <summary>
    /// HardLight: Best-effort compatibility recovery; remove serialized component/missingComponents blocks so
    /// entities can fall back to prototype defaults when component schemas drift across versions.
    /// </summary>
    private string StripSerializedComponentsForRecovery(string yamlData)
    {
        if (string.IsNullOrWhiteSpace(yamlData))
            return yamlData;

        if (yamlData.IndexOf("components:", StringComparison.Ordinal) < 0
            && yamlData.IndexOf("missingComponents:", StringComparison.Ordinal) < 0)
            return yamlData;

        var normalized = yamlData.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var output = new StringBuilder(normalized.Length);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            var isComponentsStart = trimmed.StartsWith("components:", StringComparison.Ordinal)
                                    || trimmed.StartsWith("missingComponents:", StringComparison.Ordinal);
            if (!isComponentsStart)
            {
                output.AppendLine(line);
                continue;
            }

            // Skip this block and all deeper-indented lines that belong to it.
            for (i += 1; i < lines.Length; i++)
            {
                var nextLine = lines[i];
                var nextTrimmed = nextLine.TrimStart();

                if (nextTrimmed.Length == 0)
                    continue;

                var nextIndent = nextLine.Length - nextTrimmed.Length;
                if (nextIndent > indent)
                    continue;

                i -= 1;
                break;
            }
        }

        return output.ToString(); // HardLight
    }

    /// <summary>
    /// Tries to reset the delays on any entities with the UseDelayComponent.
    /// Needed to ensure items don't have prolonged delays after saving.
    /// </summary>
    private void TryResetUseDelays(EntityUid shuttleGrid)
    {
        var useDelayQuery = EntityManager.EntityQueryEnumerator<UseDelayComponent, TransformComponent>();

        while (useDelayQuery.MoveNext(out var uid, out var comp, out var xform))
        {
            if (xform.GridUid != shuttleGrid)
                continue;

            _useDelay.ResetAllDelays((uid, comp));
        }
    }

    /// <summary>
    /// Safely deletes an entity by ensuring it is first removed from any container relationships, and
    /// recursively clears any contents if the entity itself owns containers. This avoids client-side
    /// asserts when an entity is detached to null-space while still flagged as InContainer.
    /// </summary>
    private void SafeDelete(EntityUid uid)
    {
        try
        {
            // If this entity owns containers, empty them first.
            if (TryComp<ContainerManagerComponent>(uid, out var manager))
            {
                foreach (var container in manager.Containers.Values)
                {
                    // Copy to avoid modifying during iteration
                    foreach (var contained in container.ContainedEntities.ToArray())
                    {
                        try
                        {
                            _container.Remove(contained, container, force: true);
                        }
                        catch { /* best-effort */ }

                        // Recursively ensure any nested containers are emptied then delete.
                        SafeDelete(contained);
                    }
                }
            }

            // Ensure the entity itself is not inside a container anymore (paranoia in case callers misclassify parent).
            _container.TryRemoveFromContainer(uid);
        }
        catch { /* best-effort */ }

        // Finally queue the deletion of the entity itself.
        QueueDel(uid);
    }

    /// <summary>
    /// Removes any JointComponent instances that may have been deserialized with the ship and clears
    /// DockingComponent joint references. This prevents the physics solver from encountering joints
    /// with invalid body UIDs (e.g., default/zero) originating from stale YAML state. The DockingSystem
    /// will recreate proper weld joints during docking.
    /// </summary>
    private void PurgeJointsAndResetDocks(EntityUid gridUid)
    {
        if (!TryComp<MapGridComponent>(gridUid, out var gridComponent))
            return;

        // Remove any JointComponent on the grid or its children
        var removed = 0;
        foreach (var uid in _lookup.GetEntitiesIntersecting(gridUid, gridComponent.LocalAABB))
        {
            // Purge joints first
            if (RemComp<JointComponent>(uid))
                removed++;

            // Reset docking joint references to force clean joint creation later
            if (TryComp<DockingComponent>(uid, out var dock))
            {
                dock.DockJoint = null;
                dock.DockJointId = null;

                // Clear malformed DockedWith values that might have come from YAML
                if (dock.DockedWith != null)
                {
                    var other = dock.DockedWith.Value;
                    if (!other.IsValid() || !HasComp<MetaDataComponent>(other))
                        dock.DockedWith = null;
                }
            }
        }

        if (removed > 0)
            _sawmill.Info($"[ShipLoad] Purged {removed} deserialized JointComponent(s) on grid {gridUid}");
    }

    /// <summary>
    /// Checks a shuttle to make sure that it is docked to the given station, and that there are no lifeforms aboard. Then it teleports tagged items on top of the console, appraises the grid, outputs to the server log, and deletes the grid
    /// </summary>
    /// <param name="stationUid">The ID of the station that the shuttle is docked to</param>
    /// <param name="shuttleUid">The grid ID of the shuttle to be appraised and sold</param>
    /// <param name="consoleUid">The ID of the console being used to sell the ship</param>
    public ShipyardSaleResult TrySellShuttle(EntityUid stationUid, EntityUid shuttleUid, EntityUid consoleUid, out int bill)
    {
        ShipyardSaleResult result = new ShipyardSaleResult();
        bill = 0;

        if (!TryComp<StationDataComponent>(stationUid, out var stationGrid)
            || !_transformQuery.TryComp(shuttleUid, out var xform)
            || !_transformQuery.TryComp(consoleUid, out var consoleXform)
            || consoleXform.GridUid == null) // HardLight
        {
            result.Error = ShipyardSaleError.InvalidShip;
            return result;
        }

        var targetGrid = _station.GetLargestGrid(stationGrid);

        if (targetGrid == null)
        {
            result.Error = ShipyardSaleError.InvalidShip;
            return result;
        }

        var gridDocks = _docking.GetDocks(targetGrid.Value);
        var shuttleDocks = _docking.GetDocks(shuttleUid);
        var isDocked = false;

        foreach (var shuttleDock in shuttleDocks)
        {
            foreach (var gridDock in gridDocks)
            {
                if (shuttleDock.Comp.DockedWith == gridDock.Owner)
                {
                    isDocked = true;
                    break;
                }
            }
            if (isDocked)
                break;
        }

        if (!isDocked)
        {
            _sawmill.Warning($"shuttle is not docked to that station");
            result.Error = ShipyardSaleError.Undocked;
            return result;
        }

        var mobQuery = GetEntityQuery<MobStateComponent>();
        var xformQuery = GetEntityQuery<TransformComponent>();

        var charName = FoundOrganics(shuttleUid, mobQuery, xformQuery);
        if (charName is not null)
        {
            _sawmill.Warning($"organics on board");
            result.Error = ShipyardSaleError.OrganicsAboard;
            result.OrganicName = charName;
            return result;
        }

        //just yeet and delete for now. Might want to split it into another function later to send back to the shipyard map first to pause for something
        //also superman 3 moment
        if (_station.GetOwningStation(shuttleUid) is { Valid: true } shuttleStationUid)
        {
            _station.DeleteStation(shuttleStationUid);
        }

        if (TryComp<ShipyardConsoleComponent>(consoleUid, out var comp))
        {
            CleanGrid(shuttleUid, consoleUid);
        }

        bill = (int)_pricing.AppraiseGrid(shuttleUid, LacksPreserveOnSaleComp);
        QueueDel(shuttleUid);
        _sawmill.Info($"Sold shuttle {shuttleUid} for {bill}");

        // Update all record UI (skip records, no new records)
        _shuttleRecordsSystem.RefreshStateForAll(true);

        result.Error = ShipyardSaleError.Success;
        return result;
    }

    private void CleanGrid(EntityUid grid, EntityUid destination)
    {
        var xform = Transform(grid);
        var enumerator = xform.ChildEnumerator;
        var entitiesToPreserve = new List<EntityUid>();

        while (enumerator.MoveNext(out var child))
        {
            FindEntitiesToPreserve(child, ref entitiesToPreserve);
        }
        foreach (var ent in entitiesToPreserve)
        {
            // Teleport this item and all its children to the floor (or space).
            _transform.SetCoordinates(ent, new EntityCoordinates(destination, 0, 0));
            _transform.AttachToGridOrMap(ent);
        }
    }

    // checks if something has the ShipyardPreserveOnSaleComponent and if it does, adds it to the list
    private void FindEntitiesToPreserve(EntityUid entity, ref List<EntityUid> output)
    {
        if (TryComp<ShipyardSellConditionComponent>(entity, out var comp) && comp.PreserveOnSale == true)
        {
            output.Add(entity);
            return;
        }
        if (TryComp<EntityStorageComponent>(entity, out var storageComp))
        {
            // Make storage containers delete their contents when they are deleted during ship sale
            storageComp.DeleteContentsOnDestruction = true;
            Dirty(entity, storageComp);
        }

        if (TryComp<ContainerManagerComponent>(entity, out var containers))
        {
            foreach (var container in containers.Containers.Values)
            {
                foreach (var ent in container.ContainedEntities)
                {
                    FindEntitiesToPreserve(ent, ref output);
                }
            }
        }
    }

    // returns false if it has ShipyardPreserveOnSaleComponent, true otherwise
    private bool LacksPreserveOnSaleComp(EntityUid uid)
    {
        return !TryComp<ShipyardSellConditionComponent>(uid, out var comp) || comp.PreserveOnSale == false;
    }
    private void CleanupShipyard()
    {
        if (ShipyardMap == null || !_map.MapExists(ShipyardMap.Value))
        {
            ShipyardMap = null;
            return;
        }

        _map.DeleteMap(ShipyardMap.Value);
    }

    public void SetupShipyardIfNeeded()
    {
        if (ShipyardMap != null && _map.MapExists(ShipyardMap.Value))
            return;

        _map.CreateMap(out var shipyardMap);
        ShipyardMap = shipyardMap;

        _map.SetPaused(ShipyardMap.Value, false);
    }

    // <summary>
    // Tries to rename a shuttle deed and update the respective components.
    // Returns true if successful.
    //
    // Null name parts are promptly ignored.
    // </summary>
    public bool TryRenameShuttle(EntityUid uid, ShuttleDeedComponent? shuttleDeed, string? newName, string? newSuffix)
    {
        if (!Resolve(uid, ref shuttleDeed))
            return false;

        var shuttle = shuttleDeed.ShuttleUid;
        if (shuttle != null && Exists(shuttle.Value))
        {
            // Update the primary deed
            shuttleDeed.ShuttleName = newName;
            shuttleDeed.ShuttleNameSuffix = newSuffix;
            Dirty(uid, shuttleDeed);

            // Find and update all other deeds for the same ship
            var query = EntityQueryEnumerator<ShuttleDeedComponent>();
            while (query.MoveNext(out var deedEntity, out var deed))
            {
                // Skip the deed we already updated
                if (deedEntity == uid)
                    continue;

                // Update deeds that reference the same shuttle
                if (deed.ShuttleUid == shuttle)
                {
                    deed.ShuttleName = newName;
                    deed.ShuttleNameSuffix = newSuffix;
                    Dirty(deedEntity, deed);
                }
            }

            var fullName = GetFullName(shuttleDeed);
            _metaData.SetEntityName(shuttle.Value, fullName);

            if (_station.GetOwningStation(shuttle.Value) is EntityUid shuttleStation && shuttleStation.Valid)
            {
                _station.RenameStation(shuttleStation, fullName, loud: false);
                _metaData.SetEntityName(shuttleStation, fullName);
            }
        }
        else
        {
            _sawmill.Error($"Could not rename shuttle {ToPrettyString(shuttle):entity} to {newName}");
            return false;
        }

        //TODO: move this to an event that others hook into.
        if (shuttleDeed.ShuttleUid != null &&
            _shuttleRecordsSystem.TryGetRecord(GetNetEntity(shuttleDeed.ShuttleUid.Value), out var record))
        {
            record.Name = newName ?? "";
            record.Suffix = newSuffix ?? "";
            _shuttleRecordsSystem.TryUpdateRecord(record);
        }

        return true;
    }

    /// <summary>
    /// Returns the full name of the shuttle component in the form of [prefix] [name] [suffix].
    /// </summary>
    public static string GetFullName(ShuttleDeedComponent comp)
    {
        string?[] parts = { comp.ShuttleName, comp.ShuttleNameSuffix };
        return string.Join(' ', parts.Where(it => it != null));
    }

    /// <summary>
    /// Attempts to extract ship name from YAML data
    /// </summary>
    private string? ExtractShipNameFromYaml(string yamlData)
    {
        try
        {
            // Simple YAML parsing to extract ship name
            var lines = yamlData.Split('\n');
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (trimmedLine.StartsWith("shipName:"))
                {
                    var parts = trimmedLine.Split(':', 2);
                    if (parts.Length > 1)
                    {
                        return parts[1].Trim().Trim('"', '\'');
                    }
                }
                // Also check for entity names that might indicate ship name
                if (trimmedLine.StartsWith("name:"))
                {
                    var parts = trimmedLine.Split(':', 2);
                    if (parts.Length > 1)
                    {
                        var name = parts[1].Trim().Trim('"', '\'');
                        // Only use if it looks like a ship name (not generic component names)
                        if (!name.Contains("Component") && !name.Contains("System") && name.Length > 3)
                        {
                            return name;
                        }
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _sawmill.Warning($"Failed to extract ship name from YAML: {ex}");
            return null;
        }
    }
}
