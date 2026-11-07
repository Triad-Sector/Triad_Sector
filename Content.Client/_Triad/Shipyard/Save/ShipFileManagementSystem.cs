using Content.Shared._NF.Shipyard.Events;
using Content.Shared._Triad.Shipyard.Save;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;
using YamlDotNet.RepresentationModel;

namespace Content.Client._Triad.Shipyard.Save;

/// <summary>
/// The client's side of legacy import: finds the old ship saves in the user data Exports folder,
/// describes them to the server, reads the one being imported, and retires it to backup when the
/// server says it has been spent.
/// </summary>
public sealed partial class ShipFileManagementSystem : EntitySystem
{
    [Dependency] private IResourceManager _resourceManager = default!;
    [Dependency] private ILogManager _log = default!;

    /// <summary>
    ///     Holds all file paths whitelisted for <see cref="DeleteLocalShipFileMessage"/>
    /// </summary>
    /// <remarks>
    ///     If the filepath isn't in this collection, it cannot be deleted by that message.
    ///     This prevents a rogue server from deleting non-ship-related files using path traversal trick shots
    /// </remarks>
    private static readonly List<string> DeletableShipPaths = new();

    /// <summary>
    ///     Static so a second instance of the system (integration tests run several clients in one
    ///     process) does not enumerate the folder again.
    /// </summary>
    private static readonly List<string> AvailableShips = new();

    /// <summary>The import manifest's parsed header per path, with the text it was parsed from.</summary>
    private static readonly Dictionary<string, (string Text, DrydockImportCandidate Candidate)> ImportHeaderCache = new();

    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _log.GetSawmill("shipsave_file_management");

        SubscribeNetworkEvent<DeleteLocalShipFileMessage>(HandleDeleteLocalShipFile);

        if (AvailableShips.Count == 0)
            LoadExistingShips();
    }

    /// <summary>Reads one save's full text for import, or null if it cannot be read.</summary>
    public string? ReadShipFile(string filePath)
    {
        try
        {
            using var reader = _resourceManager.UserData.OpenText(new ResPath(filePath));
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to load ship data from {filePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Describes every local save to the server so it can say which it will take: the envelope's
    /// name, its unsigned appraisal, and the signature and public key it claims.
    ///
    /// <para>No hashing here, deliberately. A signature covers a SHA-256 of the ship data and
    /// <c>System.Security.Cryptography</c> is not on the engine's sandbox whitelist, so the client
    /// cannot compute one in a packaged build. It sends the key instead and the server hashes that
    /// itself; the payload, and the real verification, follow only for the ship actually imported.</para>
    /// </summary>
    public List<DrydockImportCandidate> BuildImportManifest()
    {
        var manifest = new List<DrydockImportCandidate>();

        foreach (var path in AvailableShips)
        {
            try
            {
                // The file is read every time but parsed only when its text changed: the Exports
                // folder is shared with other clients, which can re-save a ship over the same path.
                string text;
                using (var reader = _resourceManager.UserData.OpenText(new ResPath(path)))
                    text = reader.ReadToEnd();

                if (ImportHeaderCache.TryGetValue(path, out var cached) && cached.Text == text)
                {
                    manifest.Add(cached.Candidate);
                    continue;
                }

                var node = new YamlStream();
                node.Load(new System.IO.StringReader(text));

                if (node.Documents.Count == 0 || node.Documents[0].RootNode is not YamlMappingNode root)
                    continue;

                var candidate = new DrydockImportCandidate(
                    path,
                    ExtractFileNameWithoutExtension(path),
                    int.TryParse(Scalar(root, "appraisal"), out var appraisal) ? appraisal : null,
                    B64(root, "signature"),
                    B64(root, "signaturePublicKey"));

                ImportHeaderCache[path] = (text, candidate);
                manifest.Add(candidate);
            }
            catch (Exception ex)
            {
                // A file that will not parse is one the server could not load either. Skipping it
                // keeps a corrupt save out of the list instead of out of the whole manifest.
                _sawmill.Warning($"Skipping '{path}' while building the import manifest: {ex.Message}");
            }
        }

        return manifest;
    }

    /// <summary>
    ///     Reads one scalar through the engine's helper rather than <c>YamlMappingNode.Children</c>.
    ///     A packaged client runs sandboxed, and that sandbox whitelists the engine wholesale and
    ///     YamlDotNet's dictionary surface not at all, so reaching for Children here fails the
    ///     assembly type check and the client aborts before it loads any content at all.
    /// </summary>
    private static string? Scalar(YamlMappingNode root, string key)
    {
        return root.TryGetNode<YamlScalarNode>(key, out var scalar) ? scalar.Value : null;
    }

    /// <summary>
    ///     Decodes without letting a malformed string throw, because <c>FormatException</c> is not a
    ///     type the sandbox lets content name: the catch that would be the obvious way to write this
    ///     is the same startup-killing violation as the one above.
    /// </summary>
    private static byte[] B64(YamlMappingNode root, string key)
    {
        var raw = Scalar(root, key);
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<byte>();

        // Base64 carries three bytes in every four characters and never decodes to more than that.
        var decoded = new byte[raw.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64String(raw, decoded, out var written))
            return Array.Empty<byte>();

        Array.Resize(ref decoded, written);
        return decoded;
    }

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
        return DeletableShipPaths.Remove(filePath);
    }

    private void LoadExistingShips()
    {
        try
        {
            var (ymlFiles, _) = _resourceManager.UserData.Find("*.yml", recursive: true);

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

            _sawmill.Debug($"Found {AvailableShips.Count} saved ships in the Exports directory");
        }
        catch (NotImplementedException)
        {
            // In test environments, the Find method may not be implemented
            // This is expected and should not cause test failures
            _sawmill.Debug("Ship file enumeration not available in test environment");
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to load existing ships: {ex.Message}");
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
            // We only allow the server to delete files that we have previously sent to the server
            if (!WasShipMarkedAsDeletable(message.FilePath))
            {
                _sawmill.Warning($"Server asked to move local file '{message.FilePath}' that was not previously loaded");
                return;
            }

            // Move the loaded ship file into /Exports/backup instead of deleting.
            var originalPath = new ResPath(message.FilePath);
            if (_resourceManager.UserData.Exists(originalPath))
            {
                // Ensure backup directory exists
                var backupDir = new ResPath("/Exports/backup");
                _resourceManager.UserData.CreateDir(backupDir);

                // Compute destination file path under backup directory
                var fileName = ExtractFileNameWithoutExtension(message.FilePath);
                var destinationPath = new ResPath($"/Exports/backup/{fileName}.yml");

                // If a file with the same name already exists in backup, append a timestamp
                if (_resourceManager.UserData.Exists(destinationPath))
                    destinationPath = new ResPath($"/Exports/backup/{fileName}_loaded_{DateTime.Now:yyyyMMdd_HHmmss}.yml");

                // Timestamp uniqueness is not something programmers can trust.
                // If we still don't have an unused path, we give up
                if (_resourceManager.UserData.Exists(destinationPath))
                {
                    _sawmill.Warning($"Failed to move local file '{message.FilePath}'. Could not generate safe backup path");
                }
                else
                {
                    // Both disposed before the delete, so the copy is flushed before the original goes.
                    using (var reader = _resourceManager.UserData.OpenRead(originalPath))
                    using (var writer = _resourceManager.UserData.OpenWrite(destinationPath))
                    {
                        reader.CopyTo(writer);
                    }

                    // Delete original file
                    _resourceManager.UserData.Delete(originalPath);
                    _sawmill.Info($"Moved local ship file to backup: {message.FilePath} -> {destinationPath}");
                }
            }

            // Remove original entry from caches and list (do not add backup to menu)
            ImportHeaderCache.Remove(message.FilePath);
            AvailableShips.Remove(message.FilePath);
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
