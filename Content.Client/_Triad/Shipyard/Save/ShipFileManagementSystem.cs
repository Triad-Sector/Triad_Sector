using Content.Shared._Triad.Shipyard.Save;
using System.Threading.Tasks;
using System.Linq;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;

namespace Content.Client._Triad.Shipyard.Save;

public sealed class ShipFileManagementSystem : EntitySystem
{
    [Dependency] private readonly IResourceManager _resourceManager = default!;
    [Dependency] private readonly ILogManager _log = default!;

    // Static data shared across all instances to handle multiple system instances
    private static readonly Dictionary<string, string> CachedShipData = new();

    /// <summary>
    ///     Holds all file paths whitelisted for <see cref="DeleteLocalShipFileMessage"/>
    /// </summary>
    /// <remarks>
    ///     If the filepath isn't in this collection, it cannot be deleted by that message.
    ///     This prevents a rogue server from deleting non-ship-related files using path traversal trick shots
    /// </remarks>
    private static readonly List<string> DeletableShipPaths = new();

    private static readonly List<string> AvailableShips = new();
    private static event Action? ShipsUpdated;
    private static event Action<string>? ShipLoaded;
    private static bool _indexUpdateNeeded = false;
    private static DateTime _lastIndexUpdate = DateTime.MinValue;
    private static readonly TimeSpan IndexUpdateCooldown = TimeSpan.FromSeconds(1);

    private ISawmill _sawmill = default!;

    public event Action? OnShipsUpdated
    {
        add => ShipsUpdated += value;
        remove => ShipsUpdated -= value;
    }

    public event Action<string>? OnShipLoaded
    {
        add => ShipLoaded += value;
        remove => ShipLoaded -= value;
    }

    private static int _instanceCounter = 0;
    private readonly int _instanceId;

    public ShipFileManagementSystem()
    {
        _instanceId = ++_instanceCounter;
        // Reduced logging for performance
    }

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _log.GetSawmill("shipsave_file_management");

        SubscribeNetworkEvent<SendShipSaveDataClientMessage>(HandleSaveShipDataClient);
        SubscribeNetworkEvent<DeleteLocalShipFileMessage>(HandleDeleteLocalShipFile);
        // Triad: tamper protection
        SubscribeNetworkEvent<MigrateShipFileMessage>(OnMigrateShipFile);
        // End Triad

        // Ensure saved_ships directory exists on startup
        EnsureSavedShipsDirectoryExists();

        // Only load existing ships if we haven't already loaded them
        if (AvailableShips.Count == 0)
        {
            // Load existing saved ships from user data
            LoadExistingShips();
        }
        // Skip reload if ships already loaded by previous instance
    }

    private void EnsureSavedShipsDirectoryExists()
    {
        // Exports folder already exists, no need to create directories
    }

    private void HandleSaveShipDataClient(SendShipSaveDataClientMessage message)
    {
        // Save ship data to user data directory using sandbox-safe resource manager
        _sawmill.Info($"Client received ship save data for: {message.ShipName}");

        // Ensure directory exists before saving
        EnsureSavedShipsDirectoryExists();

        var fileName = $"/Exports/{message.ShipName}_{DateTime.Now:yyyyMMdd_HHmmss}.yml";

        try
        {
            using var writer = _resourceManager.UserData.OpenWriteText(new(fileName));
            writer.Write(message.ShipData);
            _sawmill.Info($"Saved ship {message.ShipName} to user data: {fileName}");
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to save ship {message.ShipName}: {ex.Message}");
        }

        // Cache the data and update available ships list
        CachedShipData[fileName] = message.ShipData;
        if (!AvailableShips.Contains(fileName))
        {
            AvailableShips.Add(fileName);
        }

        // Mark index update as needed but don't update immediately
        _indexUpdateNeeded = true;

        // Trigger UI update
        ShipsUpdated?.Invoke();
    }

    public async Task<string?> GetShipYamlData(string filePath)
    {
        string? yamlData;

        // Check cache first, load from disk if needed (lazy loading)
        if (CachedShipData.TryGetValue(filePath, out yamlData))
        {
            // Data already cached
        }
        else
        {
            // Load from disk
            try
            {
                using var reader = _resourceManager.UserData.OpenText(new(filePath));
                yamlData = reader.ReadToEnd();
                CachedShipData[filePath] = yamlData;
            }
            catch (Exception ex)
            {
                _sawmill.Error($"Failed to load ship data from {filePath}: {ex.Message}");
                return null;
            }
        }

        await Task.CompletedTask;
        return yamlData;
    }

    // Triad start
    /// <summary>
    ///     This method whitelists a path to be acted on by <see cref="DeleteLocalShipFileMessage"/>
    /// </summary>
    public static void MarkShipPathAsDeletable(string filePath)
    {
        if (!DeletableShipPaths.Contains(filePath))
            DeletableShipPaths.Add(filePath);
    }

    /// <summary>
    ///     Tests if the given filePath was previously marked as deletable and removes it from the list if so.
    /// </summary>
    /// <param name="filePath">The file path to check.</param>
    public static bool WasShipMarkedAsDeletable(string filePath)
    {
        var index = DeletableShipPaths.IndexOf(filePath);
        if (index == -1)
        {
            return false;
        }
        else
        {
            DeletableShipPaths.RemoveAt(index);
            return true;
        }
    }

    /// <summary>
    ///     F5 fix: non-consuming variant of <see cref="WasShipMarkedAsDeletable"/> for the
    ///     <c>MigrateShipFileMessage</c> handler. The Migrate-then-Delete success sequence
    ///     needs to consult the allow-list twice: once for Migrate (non-consuming), then
    ///     once for Delete (consuming, via WasShipMarkedAsDeletable). Single allow-list,
    ///     two consumers with different consumption semantics.
    /// </summary>
    public static bool IsShipPathMarkedAsDeletable(string filePath)
    {
        return DeletableShipPaths.Contains(filePath);
    }
    // Triad end

    private void LoadExistingShips()
    {
        try
        {
            _sawmill.Info($"Instance #{_instanceId}: Attempting to find saved ship files...");

            // Try UserData.Find to enumerate all .yml files
            var (ymlFiles, directories) = _resourceManager.UserData.Find("*.yml", recursive: true);

            var ymlFilesList = ymlFiles.ToList();
            _sawmill.Info($"Instance #{_instanceId}: Found {ymlFilesList.Count.ToString()} .yml files total");

            foreach (var file in ymlFiles)
            {
                var filePath = file.ToString();

                // Accept any .yml file in Exports (not just ship_index), but exclude backups
                if (filePath.Contains("Exports")
                    && !filePath.Contains("Exports/backup")
                    && filePath.EndsWith(".yml")
                    && !filePath.Contains("ship_index"))
                {
                    if (!AvailableShips.Contains(filePath))
                        AvailableShips.Add(filePath);
                }
            }

            _sawmill.Debug($"Instance #{_instanceId}: Final result: Loaded {AvailableShips.Count} saved ships from Exports directory");

            // Trigger UI update
            ShipsUpdated?.Invoke();
        }
        catch (NotImplementedException)
        {
            // In test environments, the Find method may not be implemented
            // This is expected and should not cause test failures
            _sawmill.Debug($"Instance #{_instanceId}: Ship file enumeration not available in test environment");
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Instance #{_instanceId}: Failed to load existing ships: {ex.Message}");
        }
    }

    private void UpdateShipIndex()
    {
        try
        {
            // Rate limit index updates
            var now = DateTime.Now;
            if (!_indexUpdateNeeded || (now - _lastIndexUpdate) < IndexUpdateCooldown)
                return;

            var indexContent = string.Join('\n', AvailableShips);
            using var writer = _resourceManager.UserData.OpenWriteText(new("/Exports/ship_index.txt"));
            writer.Write(indexContent);

            _indexUpdateNeeded = false;
            _lastIndexUpdate = now;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to update ship index: {ex.Message}");
        }
    }

    // Useful for gathering fields inside of a ship YML file, like the stored appraisal value
    public string GetKeyValueFromPath(string filePath, string key)
    {
        using var reader = _resourceManager.UserData.OpenText(new(filePath));
        var content = reader.ReadToEnd();

        // lazy loading
        var lines = content.Split('\n');
        var val = lines.FirstOrDefault(l => l.Trim().StartsWith($"{key}:"))?.Split(':')[1].Trim() ?? "Unknown";

        return val;
    }

    // Update ship index periodically instead of on every change
    public void FlushPendingIndexUpdates()
    {
        if (_indexUpdateNeeded)
        {
            UpdateShipIndex();
        }
    }

    public List<string> GetSavedShipFiles()
    {
        return new List<string>(AvailableShips);
    }

    public static bool HasShipData(string shipName)
    {
        return CachedShipData.ContainsKey(shipName);
    }

    public static string? GetShipData(string shipName)
    {
        return CachedShipData.TryGetValue(shipName, out var data) ? data : null;
    }

    /// <summary>
    ///     Handles the deletion of the client's local ship file, called by the server.
    ///     The message's file path is checked against <see cref="DeletableShipPaths"/> to ensure the server can only delete valid ship files.
    /// </summary>
    private void HandleDeleteLocalShipFile(DeleteLocalShipFileMessage message)
    {
        try
        {
            // Triad start
            // We only allow the server to delete files that we have previously sent to the server
            if (!WasShipMarkedAsDeletable(message.FilePath))
            {
                _sawmill.Warning($"Server asked to move local file '{message.FilePath}' that was not previously loaded");
                return;
            }
            // Triad end

            // Move the loaded ship file into /Exports/backup instead of deleting.
            var originalPath = new ResPath(message.FilePath);
            if (_resourceManager.UserData.Exists(originalPath))
            {
                // Ensure backup directory exists
                var backupDir = new ResPath("/Exports/backup");
                _resourceManager.UserData.CreateDir(backupDir);

                // Compute destination file path under backup directory
                var fileName = ExtractFileNameWithoutExtension(message.FilePath);
                // Reconstruct original extension (assumed .yml)
                var destBase = new ResPath($"/Exports/backup/{fileName}");
                var destinationPath = new ResPath(destBase.ToString() + ".yml");

                // If a file with the same name already exists in backup, append a timestamp
                if (_resourceManager.UserData.Exists(destinationPath))
                {
                    var timestamped = new ResPath($"/Exports/backup/{fileName}_loaded_{DateTime.Now:yyyyMMdd_HHmmss}.yml");
                    destinationPath = timestamped;
                }

                // Triad start
                // Timestamp uniqueness is not something programmers can trust.
                // If we still don't have an unused path, we give up
                if (_resourceManager.UserData.Exists(destinationPath))
                {
                    _sawmill.Warning($"Failed to move local file '{message.FilePath}'. Could not generate safe backup path");
                }
                else
                {
                    // Originally opened the files as text
                    // Now we open them as bytes
                    using (var reader = _resourceManager.UserData.OpenRead(originalPath))
                    {
                        var writer = _resourceManager.UserData.OpenWrite(destinationPath);
                        reader.CopyTo(writer);
                    }

                    // Delete original file
                    _resourceManager.UserData.Delete(originalPath);
                    _sawmill.Info($"Moved local ship file to backup: {message.FilePath} -> {destinationPath}");
                }
                // Triad end
            }

            // Remove original entry from caches and list (do not add backup to menu)
            CachedShipData.Remove(message.FilePath);
            AvailableShips.Remove(message.FilePath);

            // Mark index update and notify UI
            _indexUpdateNeeded = true;
            ShipsUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            _sawmill.Warning($"Failed to move local ship file '{message.FilePath}' to backup: {ex.Message}");
        }
    }

    // Triad: tamper protection
    private void OnMigrateShipFile(MigrateShipFileMessage ev)
    {
        try
        {
            // F5 fix (a): only accept Migrate for paths the client originally vouched for
            // via MarkShipPathAsDeletable. Without this gate, a hostile or compromised server
            // could send MigrateShipFileMessage("/config.toml", evilContents) and the client
            // would silently overwrite any UserData file. Uses the non-consuming variant so
            // the subsequent Delete (which fires alongside Migrate on success in the new S1
            // ordering) still finds the allow-list entry.
            if (!IsShipPathMarkedAsDeletable(ev.TargetPath))
            {
                _sawmill.Warning(
                    $"Refused server-sent MigrateShipFileMessage for path '{ev.TargetPath}' "
                    + "(not previously vouched for via MarkShipPathAsDeletable).");
                return;
            }

            var path = ev.TargetPath.Replace('\\', '/');
            if (!path.StartsWith("/"))
                path = "/" + path;

            // F5 fix (b): explicitly reject path-traversal segments. Don't trust ResPath's
            // sanitization (the review noted it 'may or may not sanitize'); do our own.
            foreach (var segment in path.Split('/'))
            {
                if (segment == "..")
                {
                    _sawmill.Warning(
                        $"Refused server-sent MigrateShipFileMessage with '..' segment: '{ev.TargetPath}'.");
                    return;
                }
            }

            var resPath = new ResPath(path);
            using (var writer = _resourceManager.UserData.OpenWriteText(resPath))
            {
                writer.Write(ev.Contents);
            }

            // F5 fix (c): invalidate cache entries so the next load reads the new envelope
            // from disk rather than serving pre-migration bytes from CachedShipData. Remove
            // rather than overwrite to keep this loop simple - the next legitimate access
            // re-populates from disk.
            // The caches are keyed inconsistently across entry points (save by file name, load by
            // the rooted path), and we just wrote under the normalized `path` while the server sent
            // `ev.TargetPath`. Clear both forms so a leading-slash/separator difference can't leave
            // the stale pre-migration bytes behind (a miss is a harmless no-op).
            CachedShipData.Remove(ev.TargetPath);
            CachedShipData.Remove(path);

            _sawmill.Info($"Migrated local ship file to new envelope format: {path}");
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to migrate local ship file '{ev.TargetPath}': {ex.Message}");
        }
    }
    // End Triad

    private static string ExtractFileNameWithoutExtension(string filePath)
    {
        var fileName = filePath;
        var lastSlash = filePath.LastIndexOf('/');
        if (lastSlash >= 0)
            fileName = filePath.Substring(lastSlash + 1);
        var lastBackslash = fileName.LastIndexOf('\\');
        if (lastBackslash >= 0)
            fileName = fileName.Substring(lastBackslash + 1);
        var lastDot = fileName.LastIndexOf('.');
        if (lastDot >= 0)
            fileName = fileName.Substring(0, lastDot);
        return fileName;
    }
}
