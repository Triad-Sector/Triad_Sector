using System.IO;
using System.Linq;
using Robust.Shared.ContentPack;
using Robust.Shared.Map.Events;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Utility;

namespace Content.Client._Triad.ShipPreview;

/// <summary>
///     The server applies prototype migrations using MapMigrationSystem.cs as a midway and reference.
/// </summary>
public sealed partial class ShipPreviewSceneSystem
{
    [Dependency] private IResourceManager _resources = default!;

    // Sync with Content.Server.Maps.MapMigrationSystem.MigrationFiles.
    private static readonly ResPath[] MigrationFiles =
    {
        new("/migration.yml"),
        new("/nf_migration.yml"),
        new("/mono_migration.yml"),
        new("/triad_migration.yml"),
    };

    private Dictionary<string, string>? _renamedPrototypes;
    private HashSet<string>? _deletedPrototypes;

    private void InitializeMigrations()
    {
        SubscribeLocalEvent<BeforeEntityReadEvent>(OnBeforeEntityRead);
    }

    private void OnBeforeEntityRead(BeforeEntityReadEvent ev)
    {
        if (!_loadingShip)
            return;

        EnsureMigrationsRead();

        foreach (var (oldId, newId) in _renamedPrototypes!)
        {
            ev.RenamedPrototypes.TryAdd(oldId, newId);
        }

        ev.DeletedPrototypes.UnionWith(_deletedPrototypes!);
    }

    private void EnsureMigrationsRead()
    {
        if (_renamedPrototypes != null && _deletedPrototypes != null)
            return;

        _renamedPrototypes = new Dictionary<string, string>();
        _deletedPrototypes = new HashSet<string>();

        foreach (var file in MigrationFiles)
        {
            if (!TryReadMigrationFile(file, out var mapping))
                continue;

            foreach (var (oldId, node) in mapping)
            {
                if (node is not ValueDataNode value)
                    continue;

                if (string.IsNullOrWhiteSpace(value.Value) || value.Value == "null")
                    _deletedPrototypes.Add(oldId);
                else
                    _renamedPrototypes.TryAdd(oldId, value.Value);
            }
        }
    }

    private bool TryReadMigrationFile(ResPath file, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MappingDataNode? mapping)
    {
        mapping = null;

        if (!_resources.TryContentFileRead(file, out var stream))
            return false;

        using var reader = new StreamReader(stream, EncodingHelpers.UTF8);
        var document = DataNodeParser.ParseYamlStream(reader).FirstOrDefault();

        mapping = document?.Root as MappingDataNode;
        return mapping != null;
    }
}
