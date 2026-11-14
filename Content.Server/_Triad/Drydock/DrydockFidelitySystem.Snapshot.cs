using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Containers;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The round trip's own oracle: a canonical snapshot of everything on a grid that ought to survive
/// a store and retrieve unchanged, so the two halves can be compared instead of enumerated.
///
/// <para>Every carrier on this system fixes a gap somebody found by hand. The probe finds one class
/// mechanically, the unserializable declared field, and it has produced no reported bug in two play
/// tests. The reported bugs were all in classes with no detector at all: a value that writes fine
/// and means something different in the next round, and state that is not a declared field, so the
/// probe cannot ask about it. This is the detector for both. Snapshot the grid, do the round trip,
/// snapshot again, and every difference is either understood or a bug.</para>
///
/// <para>It is deliberately not wired into the store path. Its cost is writing every field of every
/// entity on the grid twice over, and its job is to fail a test, not to slow a player down.</para>
/// </summary>
public sealed partial class DrydockFidelitySystem
{
    /// <summary>
    /// Components whose state is expected to differ across a rebuild, with the reason. These are
    /// categories, not an exemption list of differences someone observed and gave up on: each one
    /// is state the loader rebuilds by design, so comparing it would report the loader working
    /// correctly as a fault.
    /// </summary>
    private static readonly HashSet<string> UncomparableComponents = new()
    {
        nameof(TransformComponent),         // parents and coordinates are remapped by the loader
        nameof(MetaDataComponent),          // lifestage and pause time are load bookkeeping
        nameof(PhysicsComponent),           // a stored ship is not moving at the speed it was
        nameof(FixturesComponent),          // rebuilt from the prototype on load
        nameof(JointComponent),             // docking joints are rebuilt, never serialized
        nameof(MapGridComponent),           // chunk storage, compared by the engine's own tests
        nameof(ContainerManagerComponent),  // membership is entity references, and every contained
                                            // entity is visited in its own right by the walk
        nameof(DrydockCapturedStateComponent),
        nameof(DrydockDamageSidecarComponent),
        nameof(DrydockPipeGasComponent),
        nameof(DrydockAppearanceComponent),
        nameof(DrydockInProgressComponent),
        nameof(DrydockIdentityComponent),
    };

    /// <summary>
    /// Walks the grid and renders every comparable field of every entity to a stable key and a
    /// stable text value.
    ///
    /// <para>Identity across the rebuild is the whole difficulty, because uids do not survive. The
    /// key is a path: an entity parented to the grid is named by its prototype and its rounded
    /// grid-local tile, and anything deeper is named by its parent's key and its own prototype. That
    /// is stable across a serialize and reload without needing any correlation table. Where a path
    /// is not unique, every entity sharing it is dropped and counted, because a wrong pairing would
    /// report a difference that is really two different objects.</para>
    /// </summary>
    public DrydockStateSnapshot SnapshotGrid(EntityUid grid)
    {
        var snapshot = new DrydockStateSnapshot();

        // Path, then the entities holding it. Built first so ambiguity is known before any value is
        // read: a path shared by two entities tells us nothing about either. Parents are always
        // resolved before their children in this walk, so each path is computed in one forward pass.
        var byPath = new Dictionary<string, List<EntityUid>>();
        var nodes = GridTreeListWithParents(grid);
        var paths = new string[nodes.Count];

        for (var i = 0; i < nodes.Count; i++)
        {
            var (uid, parent) = nodes[i];
            var path = PathFor(uid, parent is { } p ? paths[p] : null, grid);
            paths[i] = path;

            // The serializer's own exclusion, the same one the roster sweep's census applies. A
            // save: false prototype is never written into a store, so an entity of one aboard at the
            // snapshot is legitimately absent afterwards and comparing it measures timing. Sounds are
            // the case that bites: one playing at grid coordinates is a real grid child until its
            // despawn timer fires. Skipped here rather than filtered downstream, because a whole
            // entity is now reported as a single line with no component name in it for a filter to
            // match on.
            if (MetaData(uid).EntityPrototype?.MapSavable != false)
            {
                if (!byPath.TryGetValue(path, out var sharing))
                    byPath[path] = sharing = new List<EntityUid>();
                sharing.Add(uid);
            }
        }

        foreach (var (path, uids) in byPath)
        {
            if (uids.Count > 1)
            {
                snapshot.Ambiguous += uids.Count;
                continue;
            }

            snapshot.Entities++;
            Render(uids[0], path, snapshot);
        }

        return snapshot;
    }

    private string PathFor(EntityUid uid, string? parentPath, EntityUid grid)
    {
        var proto = MetaData(uid).EntityPrototype?.ID ?? "?";
        if (uid == grid)
            return "grid";

        // A direct child of the grid gets its tile, which is what separates two of the same machine.
        // Deeper entities inherit their container's identity instead, since their own local position
        // is an offset inside that container and carries no information.
        //
        // The tile rather than the position, and this matters more than it looks. An anchored
        // machine sits on a tile centre and would survive either, but a loose item lies wherever it
        // was dropped and settles a fraction of a tile differently after a reload. Keyed by position
        // that reads as the item vanishing and a stranger appearing, and the first fleet-wide run
        // reported exactly that: a thousand phantom losses that were one mask lying still.
        if (parentPath == "grid")
        {
            var local = Transform(uid).LocalPosition;
            return $"{proto}@{(int) MathF.Floor(local.X)},{(int) MathF.Floor(local.Y)}";
        }

        return $"{parentPath}/{proto}";
    }

    private void Render(EntityUid uid, string path, DrydockStateSnapshot snapshot)
    {
        foreach (var comp in AllComps(uid).ToList())
        {
            var compType = comp.GetType();
            if (UncomparableComponents.Contains(compType.Name))
                continue;

            foreach (var member in DataFields(compType))
            {
                var memberType = MemberType(member);

                // An entity reference is remapped by the loader on purpose, so its old and new
                // values differ by design and comparing them says nothing.
                if (IsIdentityType(memberType))
                    continue;

                var key = $"{path}|{compType.Name}.{member.Name}";
                var value = GetMember(comp, member);

                if (value == null)
                {
                    snapshot.Values[key] = "null";
                    continue;
                }

                if (RenderValue(value) is not { } rendered)
                {
                    snapshot.Uncapturable++;
                    continue;
                }

                snapshot.Values[key] = rendered;
            }
        }

        // Appearance is not a data field, which is exactly why it went missing for so long, so the
        // snapshot has to reach for it separately or it would report a clean round trip on a ship
        // whose visuals all came back wrong.
        if (TryComp<AppearanceComponent>(uid, out var appearance))
        {
            foreach (var (dataKey, value) in LiveAppearance(appearance))
            {
                if (RenderValue(value) is not { } rendered)
                {
                    snapshot.Uncapturable++;
                    continue;
                }

                snapshot.Values[$"{path}|Appearance.{dataKey.GetType().Name}.{dataKey}"] = rendered;
            }
        }
    }

    /// <summary>
    /// Renders one value the way the map serializer would write it, which is the only rendering the
    /// comparison should care about: what the serializer writes is what has to survive.
    ///
    /// <para>Through the probe context on purpose, not the reflective capture the carriers use. That
    /// capture decomposes an object graph field by field with no cycle detection, which is safe on
    /// the handful of manifest types it was built for and is not safe here, where it meets every
    /// field on the ship. The container graph is the proof: entity references lead back to their
    /// container and the writer recursed until the test host died. The probe's stub writer cuts
    /// exactly those edges.</para>
    ///
    /// <para>A value the serializer refuses is counted rather than chased. It is state the ship's
    /// document could not have carried either way.</para>
    /// </summary>
    private string? RenderValue(object value)
    {
        // A time is compared to the millisecond, not to the tick of a double. Any timestamp written
        // through TimeOffsetSerializer leaves as (time - CurTime) and returns as (offset + CurTime),
        // and that subtract-and-re-add costs about a hundred nanoseconds through the YAML, so an
        // exact text comparison reports 43.7332906 -> 43.7332905 as lost state on every re-based
        // field. Rounding here rather than exempting the fields by name, because the exemption list
        // would have to grow by one entry for every timestamp the fork ever re-bases.
        //
        // Three decimals is far below a tick at 30 Hz, so a deadline that genuinely moved still
        // shows up: the failure this class exists to catch is a time off by a whole round, not by a
        // microsecond.
        if (value is TimeSpan span)
            return span.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);

        try
        {
            return _serialization.WriteValue(value.GetType(), value, alwaysWrite: true, context: _probe)
                .ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a type is, contains, or is built out of an identity the loader remaps. Checked
    /// structurally rather than by name so a list of coordinates or a dictionary keyed by entity is
    /// caught as well as a bare field.
    /// </summary>
    private static bool IsIdentityType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(EntityUid) || type == typeof(NetEntity)
            || type == typeof(EntityCoordinates) || type == typeof(NetCoordinates)
            || type == typeof(MapId) || type == typeof(MapCoordinates))
        {
            return true;
        }

        if (type.IsArray)
            return IsIdentityType(type.GetElementType()!);

        return type.IsGenericType && type.GetGenericArguments().Any(IsIdentityType);
    }
}

/// <summary>
/// One side of a round-trip comparison. The three counters are the controls: a snapshot that covered
/// almost nothing would otherwise compare clean and prove nothing.
/// </summary>
public sealed class DrydockStateSnapshot
{
    /// <summary>Keyed <c>entityPath|Component.Field</c>, valued as the field rendered to YAML.</summary>
    public readonly Dictionary<string, string> Values = new();

    /// <summary>How many entities were rendered.</summary>
    public int Entities;

    /// <summary>
    /// How many were dropped because two or more shared a path. Their fields are in neither side, so
    /// a large number here means the comparison is quietly narrow.
    /// </summary>
    public int Ambiguous;

    /// <summary>How many populated fields could not be rendered to text at all.</summary>
    public int Uncapturable;

    /// <summary>
    /// Every field that changed, appeared or vanished across the round trip, most interesting first
    /// in the sense that a vanished field usually means a whole component or entity went missing.
    /// </summary>
    public static List<string> Diff(DrydockStateSnapshot before, DrydockStateSnapshot after)
    {
        var lines = new List<string>();

        // An entity that is not on the other side at all is ONE finding, reported once, rather than
        // one per field of every component it carried. The difference is not cosmetic: twenty action
        // entities emptied out of AI cores by design produced 480 rows on the first fleet-wide run,
        // because InstantActionComponent alone declares 24 fields, and 501 of the 669 "kinds" that
        // run reported were that same shape. Counting fields makes a handful of missing entities read
        // as a catastrophe and buries the field-level findings, which are the ones worth reading.
        var beforeEntities = PathsOf(before);
        var afterEntities = PathsOf(after);

        foreach (var (key, was) in before.Values)
        {
            var path = PathOf(key);
            if (!afterEntities.Contains(path))
                continue; // Reported once below, as a whole entity.

            if (!after.Values.TryGetValue(key, out var now))
            {
                lines.Add($"GONE     {key} (was {Trim(was)})");
                continue;
            }

            if (now != was)
                lines.Add($"CHANGED  {key}: {Trim(was)} -> {Trim(now)}");
        }

        foreach (var (key, now) in after.Values)
        {
            var path = PathOf(key);
            if (!beforeEntities.Contains(path))
                continue;

            if (!before.Values.ContainsKey(key))
                lines.Add($"APPEARED {key} (now {Trim(now)})");
        }

        // The whole-entity lines. Shaped with the pipe the field lines use so the sweep's own
        // grouping keys them by prototype, giving one row per kind of entity that went missing
        // rather than one per entity or one per field.
        foreach (var path in beforeEntities)
        {
            if (!afterEntities.Contains(path))
                lines.Add($"GONE     {path}|<entity:{ProtoOf(path)}> (with all its state)");
        }

        foreach (var path in afterEntities)
        {
            if (!beforeEntities.Contains(path))
                lines.Add($"APPEARED {path}|<entity:{ProtoOf(path)}> (with all its state)");
        }

        lines.Sort();
        return lines;
    }

    /// <summary>The entity half of a <c>path|Component.Field</c> key.</summary>
    private static string PathOf(string key)
    {
        var pipe = key.IndexOf('|');
        return pipe < 0 ? key : key[..pipe];
    }

    private static HashSet<string> PathsOf(DrydockStateSnapshot snapshot)
    {
        var paths = new HashSet<string>();
        foreach (var key in snapshot.Values.Keys)
            paths.Add(PathOf(key));

        return paths;
    }

    /// <summary>
    /// The prototype a path names, which is the last segment for a contained entity and the part
    /// before the tile for a direct child of the grid.
    /// </summary>
    private static string ProtoOf(string path)
    {
        var slash = path.LastIndexOf('/');
        var leaf = slash < 0 ? path : path[(slash + 1)..];

        var at = leaf.IndexOf('@');
        return at < 0 ? leaf : leaf[..at];
    }

    private static string Trim(string value)
    {
        value = value.Replace("\n", " ").Replace("\r", "");
        return value.Length <= 120 ? value : value[..120] + "…";
    }
}
