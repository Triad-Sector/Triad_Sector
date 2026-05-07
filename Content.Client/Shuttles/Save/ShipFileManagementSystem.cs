using Content.Shared._NF.Shuttles.Save;
using System.Threading.Tasks;
using System.Linq;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;

namespace Content.Client.Shuttles.Save
{
    public sealed class ShipFileManagementSystem : EntitySystem
    {
        [Dependency] private readonly IResourceManager _resourceManager = default!;
        [Dependency] private readonly ILogManager _log = default!;

        // Static data shared across all instances to handle multiple system instances
        private static readonly Dictionary<string, string> CachedShipData = new();
        private static readonly Dictionary<string, (string shipName, DateTime timestamp)> ShipMetadataCache = new();

        // Triad start
        /// <summary>
        ///     Holds all file paths whitelisted for <see cref="DeleteLocalShipFileMessage"/>
        /// </summary>
        /// <remarks>
        ///     If the filepath isn't in this collection, it cannot be deleted by that message.
        ///     This prevents a rogue server from deleting non-ship-related files using path traversal trick shots
        /// </remarks>
        private static readonly List<string> DeletableShipPaths = new();
        // Triad end

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
            SubscribeNetworkEvent<SendAvailableShipsMessage>(HandleAvailableShipsMessage);
            SubscribeNetworkEvent<ShipConvertedToSecureFormatMessage>(HandleShipConvertedToSecureFormat);
            SubscribeNetworkEvent<AdminRequestPlayerShipsMessage>(HandleAdminRequestPlayerShips);
            SubscribeNetworkEvent<AdminRequestShipDataMessage>(HandleAdminRequestShipData);
            SubscribeNetworkEvent<DeleteLocalShipFileMessage>(HandleDeleteLocalShipFile);

            // Ensure saved_ships directory exists on startup
            EnsureSavedShipsDirectoryExists();

            // Only load existing ships if we haven't already loaded them
            if (AvailableShips.Count == 0)
            {
                // Load existing saved ships from user data
                LoadExistingShips();
            }
            // Skip reload if ships already loaded by previous instance

            // Request available ships from server
            RaiseNetworkEvent(new RequestAvailableShipsMessage());
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

        private void HandleAvailableShipsMessage(SendAvailableShipsMessage message)
        {
            // Don't clear locally loaded ships - server message is for server-side ships only
            // The client handles local ship files independently
            _sawmill.Debug($"Instance #{_instanceId}: Received {message.ShipNames.Count} available ships from server (not clearing local ships)");
            _sawmill.Debug($"Instance #{_instanceId}: Current state before processing: {AvailableShips.Count} ships, {CachedShipData.Count} cached");

            // Only add server ships that aren't already in our local list
            foreach (var serverShip in message.ShipNames)
            {
                if (!AvailableShips.Contains(serverShip))
                {
                    AvailableShips.Add(serverShip);
                    _sawmill.Debug($"Instance #{_instanceId}: Added server ship: {serverShip}");
                }
            }

            _sawmill.Info($"Instance #{_instanceId}: Final state after processing: {AvailableShips.Count} ships");
        }

        private void HandleShipConvertedToSecureFormat(ShipConvertedToSecureFormatMessage message)
        {
            _sawmill.Warning($"Legacy ship '{message.ShipName}' was automatically converted to secure format by server");

            // Find and overwrite the original file with the converted version
            var originalFile = AvailableShips.FirstOrDefault(ship =>
                ship.Contains(message.ShipName) || CachedShipData.ContainsKey(ship) &&
                CachedShipData[ship].Contains($"shipName: {message.ShipName}"));

            if (originalFile != null)
            {
                try
                {
                    // Overwrite the original file with converted data
                    using var writer = _resourceManager.UserData.OpenWriteText(new(originalFile));
                    writer.Write(message.ConvertedYamlData);

                    // Update cached data
                    CachedShipData[originalFile] = message.ConvertedYamlData;

                    _sawmill.Info($"Successfully overwrote legacy ship file '{originalFile}' with secure format");
                    _sawmill.Info($"Ship '{message.ShipName}' is now protected against tampering");
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to overwrite legacy ship file '{originalFile}': {ex.Message}");
                    _sawmill.Warning($"Legacy ship '{message.ShipName}' conversion failed - please manually re-save the ship to get secure format");
                }
            }
            else
            {
                _sawmill.Warning($"Could not find original file for converted ship '{message.ShipName}' - creating new file");

                // Create a new file with the converted data
                var fileName = $"/Exports/{message.ShipName}_converted_{DateTime.Now:yyyyMMdd_HHmmss}.yml";
                try
                {
                    using var writer = _resourceManager.UserData.OpenWriteText(new(fileName));
                    writer.Write(message.ConvertedYamlData);

                    // Add to cache and available ships
                    CachedShipData[fileName] = message.ConvertedYamlData;
                    if (!AvailableShips.Contains(fileName))
                    {
                        AvailableShips.Add(fileName);
                    }

                    _sawmill.Info($"Created new secure format file for converted ship: {fileName}");
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to create converted ship file: {ex.Message}");
                }
            }
        }

        public async Task LoadShipFromFile(string filePath)
        {
            var yamlData = await GetShipYamlData(filePath);
            if (yamlData != null)
            {
                RaiseNetworkEvent(new RequestLoadShipMessage(yamlData));

                // Extract ship name for the event (extract filename without path and extension)
                var shipName = ExtractFileNameWithoutExtension(filePath);
                ShipLoaded?.Invoke(shipName);
            }
            await Task.CompletedTask;
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
                        {
                            AvailableShips.Add(filePath);

                            // Use lazy loading - only cache metadata for now
                            try
                            {
                                CacheShipMetadata(filePath);
                            }
                            catch (Exception shipEx)
                            {
                                _sawmill.Error($"Failed to cache metadata for {filePath}: {shipEx.Message}");
                            }
                        }
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

        private void CacheShipMetadata(string filePath)
        {
            try
            {
                using var reader = _resourceManager.UserData.OpenText(new(filePath));
                var content = reader.ReadToEnd();

                // Parse metadata without caching full content (lazy loading)
                var lines = content.Split('\n');
                var shipName = lines.FirstOrDefault(l => l.Trim().StartsWith("shipName:"))?.Split(':')[1].Trim() ?? "Unknown";
                var timestampStr = lines.FirstOrDefault(l => l.Trim().StartsWith("timestamp:"))?.Split(':', 2)[1].Trim() ?? "";

                if (DateTime.TryParse(timestampStr, out var timestamp))
                {
                    ShipMetadataCache[filePath] = (shipName, timestamp);
                }
            }
            catch (Exception ex)
            {
                _sawmill.Warning($"Failed to cache metadata for {filePath}: {ex.Message}");
            }
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
            /*
            _sawmill.Info($"GetSavedShipFiles called on Instance #{_instanceId}: returning {_staticAvailableShips.Count} ships");
            _sawmill.Info($"Cache contains {_staticCachedShipData.Count} cached ships");
            foreach (var ship in _staticAvailableShips)
            {
                _sawmill.Info($"  - Available: {ship}");
            }
            foreach (var cached in _staticCachedShipData.Keys)
            {
                _sawmill.Info($"  - Cached: {cached}");
            }*/
            // Return list of ships available from server and cached locally
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

        private void HandleAdminRequestPlayerShips(AdminRequestPlayerShipsMessage message)
        {
            try
            {
                // Only respond if this is our player ID
                var playerManager = IoCManager.Resolve<Robust.Client.Player.IPlayerManager>();
                if (playerManager.LocalSession?.UserId != message.PlayerId)
                    return;

                var ships = new List<(string filename, string shipName, DateTime timestamp)>();

                // Use cached metadata instead of re-parsing YAML
                foreach (var filename in AvailableShips)
                {
                    if (ShipMetadataCache.TryGetValue(filename, out var metadata))
                    {
                        ships.Add((filename, metadata.shipName, metadata.timestamp));
                    }
                    else
                    {
                        // Fallback: cache metadata if not already cached
                        try
                        {
                            CacheShipMetadata(filename);
                            if (ShipMetadataCache.TryGetValue(filename, out metadata))
                            {
                                ships.Add((filename, metadata.shipName, metadata.timestamp));
                            }
                        }
                        catch (Exception ex)
                        {
                            _sawmill.Warning($"Failed to get metadata for {filename}: {ex.Message}");
                        }
                    }
                }

                // Send response back to admin
                RaiseNetworkEvent(new AdminSendPlayerShipsMessage(ships, message.AdminName));
                _sawmill.Info($"Sent {ships.Count} ship details to admin {message.AdminName}");
            }
            catch (Exception ex)
            {
                _sawmill.Error($"Failed to handle admin request for player ships: {ex.Message}");
            }
        }

        private void HandleAdminRequestShipData(AdminRequestShipDataMessage message)
        {
            try
            {
                // Check if we have the requested ship data
                if (CachedShipData.TryGetValue(message.ShipFilename, out var shipData))
                {
                    RaiseNetworkEvent(new AdminSendShipDataMessage(shipData, message.ShipFilename, message.AdminName));
                    _sawmill.Info($"Sent ship data for {message.ShipFilename} to admin {message.AdminName}");
                }
                else
                {
                    _sawmill.Warning($"Admin {message.AdminName} requested ship data for {message.ShipFilename} but file not found");
                }
            }
            catch (Exception ex)
            {
                _sawmill.Error($"Failed to handle admin request for ship data: {ex.Message}");
            }
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
                ShipMetadataCache.Remove(message.FilePath);
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
}
