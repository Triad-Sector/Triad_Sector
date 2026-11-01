using System;
using System.Collections.Generic;
using Robust.Shared.EntitySerialization;

namespace Content.Server._Triad.Drydock;

/// <summary>An inclusive range of format versions a reader accepts.</summary>
public readonly record struct DrydockFormatWindow(int Minimum, int Maximum)
{
    public bool Contains(int version) => version >= Minimum && version <= Maximum;
}

/// <summary>One prototype id the loader rewrites, as it is written and what it becomes.</summary>
public readonly record struct DrydockRename(string From, string To);

/// <summary>
/// What <see cref="DrydockDrift.Detect"/> found about one stored document. Each list is ordinal
/// sorted and free of duplicates.
/// </summary>
public sealed record DrydockDriftVerdict(
    IReadOnlyList<string> Unresolved,
    IReadOnlyList<DrydockRename> Renamed,
    IReadOnlyList<string> Deleted,
    int EngineFormatVer,
    DrydockFormatWindow EngineWindow,
    int DrydockFormatVer,
    DrydockFormatWindow DrydockWindow)
{
    public bool EngineFormatOutOfWindow => !EngineWindow.Contains(EngineFormatVer);

    public bool DrydockFormatOutOfWindow => !DrydockWindow.Contains(DrydockFormatVer);

    /// <summary>Nothing moved: no id the migration mappings touch, and both formats readable.</summary>
    public bool IsClean =>
        Unresolved.Count == 0
        && Renamed.Count == 0
        && Deleted.Count == 0
        && !EngineFormatOutOfWindow
        && !DrydockFormatOutOfWindow;

    /// <summary>
    /// The load would fail or misread: an id that resolves to nothing after the mappings, or a format
    /// outside its reader's window. Renames and deletions alone are not a refusal, since the loader
    /// heals both on its own.
    /// </summary>
    public bool IsRefusal => Unresolved.Count > 0 || EngineFormatOutOfWindow || DrydockFormatOutOfWindow;
}

/// <summary>
/// Drift detection for stored ship documents. Pure: every input is a parameter, so a test can hand it
/// any id set, mapping table, prototype registry and format window without causing real drift.
/// </summary>
public static class DrydockDrift
{
    /// <summary>
    /// The map format versions the engine's loader reads, taken from the loader itself
    /// (<see cref="EntityDeserializer.OldestSupportedVersion"/> to
    /// <see cref="EntityDeserializer.NewestSupportedVersion"/>, the latter being the version the
    /// serializer writes). Constants, so this moves with an engine pin bump at compile time.
    /// </summary>
    public static DrydockFormatWindow EngineWindow =>
        new(EntityDeserializer.OldestSupportedVersion, EntityDeserializer.NewestSupportedVersion);

    /// <summary>The drydock format versions a retrieve reads.</summary>
    public static DrydockFormatWindow DrydockWindow =>
        new(DrydockFormat.MinimumSupported, DrydockFormat.Current);

    /// <summary>
    /// Classifies a document's prototype ids against the migration mappings and a prototype registry,
    /// and its two format versions against their windows.
    /// </summary>
    /// <param name="ids">The document's group ids, as <c>DrydockSystem.ReadDriftIds</c> returns them.</param>
    /// <param name="table">The migration mappings the loader will apply.</param>
    /// <param name="isKnownPrototype">Whether an id names an entity prototype that exists now.</param>
    /// <remarks>
    /// Mirrors <c>EntityDeserializer</c>, which reads the mappings twice and not quite the same way
    /// both times. <c>ValidatePrototypes</c>, the pass that can fail the whole load, renames first and
    /// then checks the renamed id against the deleted set and the registry. <c>ReadEntities</c>, the
    /// pass that decides what each entity becomes, checks the id as written against the deleted set
    /// before it renames. So refusal is judged on the renamed id, while deleted and renamed are
    /// reported for the id as written. Renames are applied once and never chained, as there.
    /// A blank id is skipped, as both passes skip it.
    /// </remarks>
    public static DrydockDriftVerdict Detect(
        IEnumerable<string> ids,
        DrydockMigrationTable table,
        Func<string, bool> isKnownPrototype,
        int engineFormatVer,
        DrydockFormatWindow engineWindow,
        int drydockFormatVer,
        DrydockFormatWindow drydockWindow)
    {
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var renamed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var deleted = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var hasRename = table.Renamed.TryGetValue(id, out var target);
            var resolved = hasRename ? target! : id;

            if (!table.Deleted.Contains(resolved) && !isKnownPrototype(resolved))
                unresolved.Add(resolved);

            // A rename whose target is itself in the deleted set passes validation, but the read pass
            // does not delete it: it becomes the target, or a prototype-less entity if the target is
            // gone. MapMigrationSystem asserts every target is a real prototype in debug builds, so
            // this is reported as the rename it is rather than modelled further.
            if (table.Deleted.Contains(id))
                deleted.Add(id);
            else if (hasRename)
                renamed[id] = target!;
        }

        var renames = new List<DrydockRename>(renamed.Count);
        foreach (var (from, to) in renamed)
        {
            renames.Add(new DrydockRename(from, to));
        }

        return new DrydockDriftVerdict(
            new List<string>(unresolved),
            renames,
            new List<string>(deleted),
            engineFormatVer,
            engineWindow,
            drydockFormatVer,
            drydockWindow);
    }
}
