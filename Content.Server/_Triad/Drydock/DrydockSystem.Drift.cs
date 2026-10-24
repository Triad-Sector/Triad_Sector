using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The production inputs to <see cref="DrydockDrift.Detect"/>: the migration mappings this server
/// ships and the prototypes it has loaded. Kept apart from the detector so the detector stays a pure
/// function a test can hand anything.
/// </summary>
public sealed partial class DrydockSystem
{
    [Dependency] private IResourceManager _resources = default!;

    private DrydockMigrationTable? _migrationTable;

    /// <summary>
    /// The four entity migration files as the loader applies them, read once on first use. Holiday
    /// renames are deliberately absent: a re-bake built on them would make a seasonal swap permanent.
    /// Not thread-safe to build: every caller touches it on the main thread before handing work to a worker.
    /// </summary>
    internal DrydockMigrationTable MigrationTable => _migrationTable ??= DrydockMigrationTable.Load(_resources);

    /// <summary>
    /// Classifies one stored document against the mappings and the prototypes loaded now. Reads only,
    /// so it may run off the main thread once <see cref="MigrationTable"/> has been touched there.
    /// </summary>
    /// <param name="yaml">The uncompressed document.</param>
    /// <param name="drydockFormatVer">The revision's <c>drydock_format_ver</c> column.</param>
    internal DrydockDriftVerdict DetectDrift(string yaml, int drydockFormatVer)
    {
        var (ids, engineFormatVer) = ReadDriftIds(yaml);
        return DetectDrift(ids, engineFormatVer, drydockFormatVer);
    }

    /// <summary><see cref="DetectDrift(string, int)"/> over ids already read with <see cref="ReadDriftIds"/>.</summary>
    internal DrydockDriftVerdict DetectDrift(SortedSet<string> ids, int engineFormatVer, int drydockFormatVer)
    {
        return DrydockDrift.Detect(
            ids,
            MigrationTable,
            id => _protoMan.HasIndex<EntityPrototype>(id),
            engineFormatVer,
            DrydockDrift.EngineWindow,
            drydockFormatVer,
            DrydockDrift.DrydockWindow);
    }
}
