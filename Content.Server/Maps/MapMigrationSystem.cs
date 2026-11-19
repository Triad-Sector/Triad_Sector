using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Robust.Server.GameObjects;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map.Events;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Utility;
using Content.Server._Triad.Drydock; // Triad

namespace Content.Server.Maps;

/// <summary>
///     Performs basic map migration operations by listening for engine <see cref="MapLoaderSystem"/> events.
/// </summary>
public sealed partial class MapMigrationSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _protoMan = default!;
    [Dependency] private IResourceManager _resMan = default!;

    // Triad: removed [private; the drydock's DrydockMigrationTable reads the same files and must not keep its own list]
    // private static readonly string[] MigrationFiles = { "/migration.yml", "/nf_migration.yml", "/mono_migration.yml", "/triad_migration.yml" }; // Triad: custom migration file
    internal static readonly string[] MigrationFiles = { "/migration.yml", "/nf_migration.yml", "/mono_migration.yml", "/triad_migration.yml" }; // Triad: custom migration file; internal for DrydockMigrationTable

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<BeforeEntityReadEvent>(OnBeforeReadEvent);

#if DEBUG
        if (!TryReadFiles(out var mappings)) // Frontier: TryReadFile<TryReadFiles
            return;

        // Verify that all of the entries map to valid entity prototypes.
        // Delta-V: use list of migrations
        foreach (var mapping in mappings)
        {
            foreach (var node in mapping.Values)
            {
                var newId = ((ValueDataNode)node).Value;
                if (!string.IsNullOrEmpty(newId) && newId != "null")
                    DebugTools.Assert(_protoMan.HasIndex<EntityPrototype>(newId), $"{newId} is not an entity prototype.");
            }
        }
        // End Delta-V
#endif
    }

    // Frontier: wrap single file reader
    private bool TryReadFiles([NotNullWhen(true)] out List<MappingDataNode>? mappings)
    {
        mappings = null;

        if (MigrationFiles.Count() <= 0)
            return false;

        foreach (var migrationFile in MigrationFiles)
        {
            if (!TryReadFile(migrationFile, out var mapping))
                continue;

            mappings = mappings ?? new List<MappingDataNode>();
            mappings.Add(mapping);
        }

        return mappings != null && mappings.Count > 0;
    }
    // End Frontier

    private bool TryReadFile(string migrationFile, [NotNullWhen(true)] out MappingDataNode? mappings) // Frontier: add migrationFile
    {
        mappings = null;
        var path = new ResPath(migrationFile); // Frontier: MigrationFile<migrationFile
        if (!_resMan.TryContentFileRead(path, out var stream))
            return false;

        using var reader = new StreamReader(stream, EncodingHelpers.UTF8);
        var documents = DataNodeParser.ParseYamlStream(reader).FirstOrDefault();

        if (documents == null)
            return false;

        mappings = (MappingDataNode) documents.Root;
        return true;
    }

    private void OnBeforeReadEvent(BeforeEntityReadEvent ev)
    {
        // Triad: removed [the drydock drift detector and re-bake must apply this exact rule, so the loader and the drydock both build DrydockMigrationTable]
        // if (!TryReadFiles(out var mappings))
        //     return;
        //
        // // Delta-V: apply a set of mappings
        // foreach (var mapping in mappings)
        // {
        //     foreach (var (key, value) in mapping)
        //     {
        //         if (value is not ValueDataNode valueNode)
        //             continue;
        //
        //         if (string.IsNullOrWhiteSpace(valueNode.Value) || valueNode.Value == "null")
        //             ev.DeletedPrototypes.Add(key);
        //         else
        //             ev.RenamedPrototypes.Add(key, valueNode.Value);
        //     }
        // }
        // // End Delta-V

        // Triad: read fresh per load, as upstream does; a key renamed twice throws, as Dictionary.Add did
        var table = DrydockMigrationTable.Load(_resMan); // Triad
        foreach (var id in table.Deleted) // Triad
            ev.DeletedPrototypes.Add(id); // Triad

        foreach (var (from, to) in table.Renamed) // Triad
            ev.RenamedPrototypes.Add(from, to); // Triad
    }
}
