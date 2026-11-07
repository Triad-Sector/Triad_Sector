using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Content.Server.Maps;
using Robust.Shared.ContentPack;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Utility;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The entity prototype renames and deletions the loader applies to every document it reads, as an
/// immutable value. The drift detector and the tier 1 re-bake take one of these as a parameter
/// instead of reading the migration files themselves, so tests can hand them a synthetic table.
/// </summary>
/// <remarks>
/// <para>This is the table <see cref="MapMigrationSystem"/> hands the loader on each
/// <c>BeforeEntityReadEvent</c> (it calls <see cref="Load"/>), and only that. <c>HolidaySystem</c> also adds renames to the same
/// event while a holiday runs (holiday-themed replacements, <c>TryAdd</c>); those are left out on
/// purpose, because a re-bake that applied them would make a seasonal swap permanent.</para>
/// </remarks>
public sealed class DrydockMigrationTable
{
    public static readonly DrydockMigrationTable Empty =
        new(FrozenDictionary<string, string>.Empty, FrozenSet<string>.Empty);

    /// <summary>Old id to new id. Applied once, never chained, exactly as the loader does.</summary>
    public FrozenDictionary<string, string> Renamed { get; }

    /// <summary>Ids the loader deletes after reading, with a warning rather than a failure.</summary>
    public FrozenSet<string> Deleted { get; }

    private DrydockMigrationTable(FrozenDictionary<string, string> renamed, FrozenSet<string> deleted)
    {
        Renamed = renamed;
        Deleted = deleted;
    }

    /// <summary>
    /// Builds the table from parsed mapping files, in file order: a value that is not a scalar is
    /// skipped, an empty, whitespace or <c>null</c> value is a deletion, anything else a rename. This is
    /// the loader's rule because it is the loader's code path (<see cref="MapMigrationSystem"/>).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The same key is renamed twice. A later file never overrides an earlier one: the throw fails
    /// every map load on the server, loudly. A key deleted twice is harmless (a set), and a key both
    /// renamed and deleted is kept in both, which the engine's deserializer then resolves.
    /// </exception>
    public static DrydockMigrationTable FromMappings(IEnumerable<MappingDataNode> mappings)
    {
        var renamed = new Dictionary<string, string>();
        var deleted = new HashSet<string>();

        foreach (var mapping in mappings)
        {
            foreach (var (key, value) in mapping)
            {
                if (value is not ValueDataNode valueNode)
                    continue;

                if (string.IsNullOrWhiteSpace(valueNode.Value) || valueNode.Value == "null")
                {
                    deleted.Add(key);
                    continue;
                }

                if (!renamed.TryAdd(key, valueNode.Value))
                {
                    throw new ArgumentException(
                        $"Prototype '{key}' is renamed twice, to '{renamed[key]}' and to '{valueNode.Value}'. "
                        + "MapMigrationSystem throws on this at every map load.",
                        nameof(mappings));
                }
            }
        }

        return new DrydockMigrationTable(renamed.ToFrozenDictionary(), deleted.ToFrozenSet());
    }

    /// <summary>
    /// The files <see cref="MapMigrationSystem"/> reads, in its order, parsed from content. A file that
    /// is missing or holds no document is skipped, as there.
    /// </summary>
    public static DrydockMigrationTable Load(IResourceManager resources)
    {
        return FromMappings(ReadMappings(resources));
    }

    internal static List<MappingDataNode> ReadMappings(IResourceManager resources)
    {
        var mappings = new List<MappingDataNode>();

        foreach (var file in MapMigrationSystem.MigrationFiles)
        {
            if (!resources.TryContentFileRead(new ResPath(file), out var stream))
                continue;

            using var reader = new StreamReader(stream, EncodingHelpers.UTF8);
            var document = DataNodeParser.ParseYamlStream(reader).FirstOrDefault();

            if (document == null)
                continue;

            mappings.Add((MappingDataNode) document.Root);
        }

        return mappings;
    }
}
