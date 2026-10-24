using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Globalization;
using System.Threading.Tasks;
using Content.Shared._Triad.Drydock;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;

namespace Content.Server._Triad.Drydock;

/// <summary>What the transaction does with <c>MapInitEvent</c>: nothing, raise it and only report,
/// raise it and undo what it did to persisted state, or (for a legacy import only) undo its field
/// writes but keep what it spawned.</summary>
public enum DrydockMapInitMode : byte
{
    Off,
    Report,
    Revert,

    /// <summary>
    /// A legacy import's first map init. The old save writer relied on the loader map-initializing
    /// a ship, so its documents never carried what a fill spawns: an airlock's door electronics, a
    /// light's bulb. Those are kept, and the store files them as the document's own. A persisted
    /// field the file left null or empty keeps what map init put in it, for the same reason: a
    /// dispenser's storage slots, a mob's combat-mode action, a radio's channels (and a vending
    /// machine the old file saved with no stock at all is stocked). Any other persisted field is
    /// put back, so a gun does not refill and a rolled value keeps the file's roll, and the fill
    /// guards still hold, so a container the file did fill gets nothing on top. Never a cvar
    /// value: the import picks it.
    /// </summary>
    Import,
}

/// <summary>
/// The map-init transaction. A restored entity arrives marked initialized and the engine never
/// raises <c>MapInitEvent</c> for it again, so everything a system only ever does on map init
/// (device-network joins, wire layouts, research links, visuals) is missing from a retrieved ship.
/// Content also uses the same handler for one-shot authoring: filling lockers, rolling randoms,
/// resetting counts to capacity. The two halves cannot be told apart by reading them, and there are
/// three hundred of them.
///
/// <para>The split is made structurally instead. The document is the authority for exactly what the
/// document contains: a <c>[DataField]</c> the serializer writes is persisted state, and a map init
/// may not change it. Everything else, runtime fields, system registries, appearance and the
/// read-only fields the serializer never writes, is map init's to rebuild. So the event is raised
/// on every entity and then every persisted field that changed is put back, every entity that
/// appeared is deleted, and every entity or component that vanished is reported, because a deletion
/// is the one thing this cannot undo.</para>
///
/// <para>Appearance is never put back, though a handler that rolls a persisted value usually draws it
/// in the same call, which leaves that visual showing the roll (a toilet seat drawn up over a seat
/// stored down). Visuals are set on state changes, so one the fire updated correctly and a revert put
/// back stays stale for good: reverting appearance measured worse on the roster than not raising map
/// init at all.</para>
///
/// <para>The report is the audit: which handlers write persisted state on a re-fire, per field, on
/// every retrieve. A new handler arriving in a merge shows up there before anyone has read it.</para>
/// </summary>
public sealed partial class DrydockFidelitySystem
{
    /// <summary>
    /// Raises the event with its refire flag up, which is what lets a handler tell this re-raise
    /// from a first map init and skip one-shot work the ship already carries.
    /// </summary>
    [Dependency] private MapInitRefireSystem _mapInitRefire = default!;

    /// <summary>
    /// Components the transaction never touches: what the loader owns (parents, coordinates,
    /// physics, fixtures, joints, chunks, containers, all rebuilt by design) and the drydock's own
    /// sidecars. Container membership is covered anyway, since an entity spawned into a container
    /// is an entity that appeared.
    /// </summary>
    private static readonly HashSet<string> MapInitUntouched = new()
    {
        nameof(TransformComponent),
        "PhysicsComponent",
        "FixturesComponent",
        "JointComponent",
        "MapGridComponent",
        "ContainerManagerComponent",
        nameof(DrydockCapturedStateComponent),
        nameof(DrydockDamageSidecarComponent),
        nameof(DrydockPipeGasComponent),
        nameof(DrydockAppearanceComponent),
        nameof(DrydockInProgressComponent),
        nameof(DrydockIdentityComponent),
    };

    /// <summary>
    /// Persisted fields a map init is allowed to rewrite from the live world, as
    /// <c>Component.Field</c>: ones whose live value is what some registry now holds, so putting the
    /// stored one back would leave the component disagreeing with the world about itself.
    ///
    /// <para>The device network's address and frequencies. Map init connects the device, and
    /// <c>DeviceNet.Add</c> files it under its address, generating one when the stored address is
    /// empty or taken; the frequencies it listens and sends on are resolved from their ids in the
    /// same handler. A bought ship carries all three from its purchase, so nothing changes. A legacy
    /// import carries none, and reverting them left every device registered under an address its
    /// component no longer named: device-link signals were sent to an empty address and went
    /// nowhere.</para>
    /// </summary>
    private static readonly HashSet<string> MapInitKeepLive = new(StringComparer.Ordinal)
    {
        "DeviceNetworkComponent.Address",
        "DeviceNetworkComponent.ReceiveFrequency",
        "DeviceNetworkComponent.TransmitFrequency",
    };

    /// <summary>The last transaction's report, for tests. Overwritten per retrieve.</summary>
    public DrydockMapInitReport? LastMapInitReport;

    /// <summary>
    /// Every persisted field of a component type: every data field the serializer writes, so
    /// custom-serialized fields are in (their live value is compared and reverted as its plain
    /// type, which for the offset-serialized times is the absolute time) and read-only ones are out
    /// (never written, so never persisted, so map init's to rebuild). Distinct from the capture
    /// walk's <see cref="DataFields"/>, whose rule is the probe's.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, (MemberInfo Member, Type Type)[]> PersistedFieldCache = new();

    private static (MemberInfo Member, Type Type)[] PersistedFields(Type type) => PersistedFieldCache.GetOrAdd(type, static t =>
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var members = new List<(MemberInfo, Type)>();

        for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var m in cur.GetFields(flags).Cast<MemberInfo>().Concat(cur.GetProperties(flags)))
            {
                var attr = m.GetCustomAttribute<DataFieldBaseAttribute>();
                if (attr is { ReadOnly: false })
                    members.Add((m, MemberType(m)));
            }
        }

        return members.ToArray();
    });

    private sealed class MapInitEntitySnapshot
    {
        public string Proto = "?";
        public readonly HashSet<Type> Components = new();

        /// <summary>The value as it was, safe to hand back to the field on revert.</summary>
        public readonly Dictionary<(Type Comp, MemberInfo Member), Captured> Fields = new();
    }

    /// <summary>
    /// A field's value as it was: the boxed value itself for a string or a value type, a deep copy
    /// for anything the manager copies, and the original reference for a type the manager copies by
    /// reference, which the engine treats as immutable, so reassignment is the only change to look
    /// for.
    /// </summary>
    private readonly record struct Captured(object? Value, bool ByRef);

    /// <summary>
    /// Raises <c>MapInitEvent</c> on every entity of the ship, then reconciles persisted state
    /// against the snapshot taken first. Three sliced walks: snapshot, raise, reconcile. The ship is
    /// frozen throughout, so the state between walks is observed by nothing.
    /// </summary>
    public async Task<DrydockMapInitReport> RefireMapInitSliced(EntityUid grid, IDrydockSlice slice, DrydockMapInitMode mode)
    {
        var report = new DrydockMapInitReport(mode);
        LastMapInitReport = report;
        if (mode == DrydockMapInitMode.Off)
            return report;

        var tree = GridTreeList(grid);
        var before = new Dictionary<EntityUid, MapInitEntitySnapshot>(tree.Count);

        // Snapshot. The grid itself is walked over, not into: nothing content-side subscribes map
        // init on a component the grid carries, and its own map-init work is the station join.
        await slice.Begin(DrydockPhase.MapInit, tree.Count);
        for (var i = 1; i < tree.Count; i++)
        {
            var uid = tree[i];
            if (!TerminatingOrDeleted(uid))
                before[uid] = Snapshot(uid, report);

            await slice.Step(i);
            GuardGrid(grid);
        }

        report.Entities = before.Count;

        // Raise. A handler that throws costs its entity the rest of its handlers and nothing else;
        // a ship that comes back with one machine unready beats one that does not come back.
        await slice.Begin(DrydockPhase.MapInit, tree.Count);
        for (var i = 1; i < tree.Count; i++)
        {
            var uid = tree[i];
            if (before.ContainsKey(uid) && !TerminatingOrDeleted(uid))
            {
                try
                {
                    _mapInitRefire.Raise(uid);
                    report.Fired++;
                }
                catch (Exception e)
                {
                    report.HandlerExceptions.Add((before[uid].Proto, e.GetType().Name + ": " + e.Message));
                }
            }

            await slice.Step(i);
            GuardGrid(grid);
        }

        // Reconcile. Every entity now on the ship is either one that was snapshotted, compared field
        // by field, or one that appeared, which a map init spawned and the document never held.
        var after = GridTreeList(grid);
        await slice.Begin(DrydockPhase.MapInit, after.Count);
        for (var i = 1; i < after.Count; i++)
        {
            var uid = after[i];
            if (TerminatingOrDeleted(uid))
            {
                await slice.Step(i);
                continue;
            }

            if (before.TryGetValue(uid, out var snapshot))
            {
                Reconcile(uid, snapshot, mode, report);
            }
            else
            {
                // Import keeps it: a legacy document never carried what a fill spawns, and deleting
                // it on the way in is what left imported airlocks with no electronics to read access
                // from, locked to everyone.
                report.Count(report.Appeared, ProtoOf(uid));
                if (mode == DrydockMapInitMode.Revert)
                    QueueDel(uid);
            }

            await slice.Step(i);
            GuardGrid(grid);
        }

        // What the walk above could not visit: snapshotted entities no longer on the ship.
        var stillAboard = new HashSet<EntityUid>(after);
        foreach (var (uid, snapshot) in before)
        {
            if (!stillAboard.Contains(uid))
                report.Count(report.Vanished, snapshot.Proto);
        }

        // A leak is something the revert could not undo, so it is the line an operator has to see.
        if (report.HasLeaks)
            Log.Warning($"Drydock map-init transaction on {ToPrettyString(grid)} leaked: {report.Summary()}");
        else
            Log.Info($"Drydock map-init transaction on {ToPrettyString(grid)}: {report.Summary()}");

        return report;
    }

    private void GuardGrid(EntityUid grid)
    {
        if (TerminatingOrDeleted(grid))
            throw new DrydockAbortedException($"{ToPrettyString(grid)} was deleted mid-revive");
    }

    private string ProtoOf(EntityUid uid) => MetaData(uid).EntityPrototype?.ID ?? "?";

    private MapInitEntitySnapshot Snapshot(EntityUid uid, DrydockMapInitReport report)
    {
        var snapshot = new MapInitEntitySnapshot { Proto = ProtoOf(uid) };

        foreach (var comp in AllComps(uid))
        {
            var compType = comp.GetType();
            snapshot.Components.Add(compType);
            if (MapInitUntouched.Contains(compType.Name))
                continue;

            foreach (var (member, _) in PersistedFields(compType))
            {
                if (TryCapture(GetMember(comp, member), out var captured))
                    snapshot.Fields[(compType, member)] = captured;
                else
                    report.Count(report.Uncomparable, $"{compType.Name}.{member.Name}");
            }
        }

        return snapshot;
    }

    private void Reconcile(EntityUid uid, MapInitEntitySnapshot snapshot, DrydockMapInitMode mode, DrydockMapInitReport report)
    {
        var present = new HashSet<Type>();

        foreach (var comp in AllComps(uid).ToList())
        {
            var compType = comp.GetType();
            present.Add(compType);

            if (!snapshot.Components.Contains(compType))
            {
                report.Count(report.ComponentsAdded, $"{compType.Name} on {snapshot.Proto}");
                continue;
            }

            if (MapInitUntouched.Contains(compType.Name))
                continue;

            foreach (var (member, memberType) in PersistedFields(compType))
            {
                if (!snapshot.Fields.TryGetValue((compType, member), out var was))
                    continue;

                var key = $"{compType.Name}.{member.Name}";
                if (Same(memberType, was, GetMember(comp, member), key, report))
                    continue;

                report.Count(report.Changed, key);

                if (mode is not (DrydockMapInitMode.Revert or DrydockMapInitMode.Import) || MapInitKeepLive.Contains(key))
                    continue;

                // Import keeps what map init filled into a field the document left unset: a legacy
                // file never carried it, so there is nothing of the player's to put back.
                if (mode == DrydockMapInitMode.Import && IsUnset(was.Value))
                    continue;

                try
                {
                    SetMember(comp, member, was.Value);
                    DirtyIfNetworked(uid, comp);
                    report.Reverted++;
                }
                catch (Exception e)
                {
                    report.RevertFailures.Add((key, e.Message));
                }
            }
        }

        foreach (var compType in snapshot.Components)
        {
            if (!present.Contains(compType))
                report.Count(report.ComponentsRemoved, $"{compType.Name} on {snapshot.Proto}");
        }
    }

    /// <summary>
    /// Whether a captured value is the shape of a field a document never carried: null (a nullable
    /// reference or value type) or an empty collection. A string, a plain value type, a false or a
    /// zero never counts, because a player's switch turned off stores exactly that.
    /// </summary>
    private static bool IsUnset(object? value)
    {
        if (value == null)
            return true;

        if (value is string || value is not IEnumerable enumerable)
            return false;

        var enumerator = enumerable.GetEnumerator();
        try
        {
            return !enumerator.MoveNext();
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The value as something the field can be handed back later. A string or a value type is its
    /// own copy once boxed. Anything else is deep-copied by the serialization manager, the same walk
    /// a prototype's data definitions take into an entity. A type it hands back by reference (sound
    /// specifiers, prototype layer data: immutable by the engine's contract) is kept as that
    /// reference and later compared as one. A type it cannot copy at all is a type the document
    /// could not carry; that is left out and counted.
    /// </summary>
    private bool TryCapture(object? value, out Captured captured)
    {
        captured = default;
        if (value == null)
            return true;

        var type = value.GetType();
        if (value is string || type.IsValueType)
        {
            captured = new Captured(value, false);
            return true;
        }

        object? copy;
        try
        {
            copy = _serialization.CreateCopy(value);
        }
        catch
        {
            return false;
        }

        if (copy == null)
            return false;

        captured = new Captured(copy, ReferenceEquals(copy, value));
        return true;
    }

    /// <summary>
    /// Whether the live value is what was captured. Strings by value, by-reference captures by
    /// reference, value types by their own equality and everything else structurally through the
    /// manager. Neither cheap test is trusted to say "differs": a type's own equality can be wrong
    /// (<c>ReagentQuantity.Equals</c> tests its quantities with <c>!=</c>, so an unchanged value is
    /// unequal to itself), and the manager reads a nested element it has no data definition for as
    /// a reference, so a deep copy can disagree with an unchanged original. A "differs" verdict
    /// therefore gets a second opinion, both sides rendered to YAML with entity references written
    /// as their live ids and compared as text. That render is the expensive comparison, and it runs
    /// only on the handful the cheap one flags. A field neither can judge is counted and read as
    /// equal, so it is never reverted on a guess.
    /// </summary>
    private bool Same(Type memberType, Captured was, object? now, string key, DrydockMapInitReport report)
    {
        if (was.Value == null || now == null)
            return was.Value == null && now == null;

        if (was.ByRef)
            return ReferenceEquals(was.Value, now);

        if (was.Value is string)
            return was.Value.Equals(now);

        try
        {
            if (was.Value.GetType().IsValueType
                    ? was.Value.Equals(now)
                    : _serialization.DataFieldEquals(was.Value.GetType(), was.Value, now))
            {
                return true;
            }

            return _serialization.WriteValue(memberType, was.Value, alwaysWrite: true, context: _refWriter).ToString()
                   == _serialization.WriteValue(memberType, now, alwaysWrite: true, context: _refWriter).ToString();
        }
        catch
        {
            report.Count(report.Uncomparable, key);
            return true;
        }
    }

    /// <summary>The second opinion's context: entity references written as their live ids, so two
    /// renders differ exactly when the references do.</summary>
    private DrydockEntityRefWriter _refWriter = default!;

    public static DrydockMapInitMode ParseMapInitMode(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "report" => DrydockMapInitMode.Report,
            "revert" => DrydockMapInitMode.Revert,
            _ => DrydockMapInitMode.Off,
        };
    }
}

/// <summary>
/// What one map-init transaction saw. <see cref="Changed"/> is the list that matters: every
/// persisted field a map-init handler rewrote, which in <see cref="DrydockMapInitMode.Revert"/> and
/// <see cref="DrydockMapInitMode.Import"/> was put back. <see cref="Appeared"/> was deleted in the
/// first and kept in the second. Deletions and removed components are the part no revert covers, so they are the lines
/// to read first.
/// </summary>
public sealed class DrydockMapInitReport
{
    public readonly DrydockMapInitMode Mode;
    public int Entities;
    public int Fired;
    public int Reverted;

    /// <summary>Persisted fields that differed after the fire, as <c>Component.Field</c> with the
    /// number of entities it happened on.</summary>
    public readonly SortedDictionary<string, int> Changed = new(StringComparer.Ordinal);

    /// <summary>Entities on the ship after the fire that were not there before, by prototype.</summary>
    public readonly SortedDictionary<string, int> Appeared = new(StringComparer.Ordinal);

    /// <summary>Entities snapshotted before the fire that were gone after it, by prototype.</summary>
    public readonly SortedDictionary<string, int> Vanished = new(StringComparer.Ordinal);

    public readonly SortedDictionary<string, int> ComponentsAdded = new(StringComparer.Ordinal);
    public readonly SortedDictionary<string, int> ComponentsRemoved = new(StringComparer.Ordinal);

    /// <summary>Persisted fields the transaction could neither copy nor compare, so could not see.</summary>
    public readonly SortedDictionary<string, int> Uncomparable = new(StringComparer.Ordinal);

    public readonly List<(string Proto, string Error)> HandlerExceptions = new();
    public readonly List<(string Key, string Reason)> RevertFailures = new();

    public DrydockMapInitReport(DrydockMapInitMode mode)
    {
        Mode = mode;
    }

    /// <summary>Whether anything happened that no revert covers: an entity or component the fire
    /// deleted, a handler that threw, or a field that could not be put back.</summary>
    public bool HasLeaks => Vanished.Count + ComponentsRemoved.Count + HandlerExceptions.Count + RevertFailures.Count > 0;

    public void Count(SortedDictionary<string, int> bucket, string key)
    {
        bucket[key] = bucket.GetValueOrDefault(key) + 1;
    }

    /// <summary>One line: the counts, then the deletions, which are the part nothing undid.</summary>
    public string Summary()
    {
        var sb = new StringBuilder();
        sb.Append($"mode {Mode}, {Fired} of {Entities} fired, {Changed.Values.Sum()} field change(s) on {Changed.Count} kind(s), "
                  + $"{Reverted} reverted, {Appeared.Values.Sum()} appeared, {Vanished.Values.Sum()} vanished, "
                  + $"{ComponentsRemoved.Values.Sum()} component(s) removed, {HandlerExceptions.Count} handler exception(s), "
                  + $"{RevertFailures.Count} revert failure(s)");

        if (Vanished.Count > 0)
            sb.Append("; vanished: ").Append(Join(Vanished));
        if (ComponentsRemoved.Count > 0)
            sb.Append("; removed: ").Append(Join(ComponentsRemoved));

        return sb.ToString();
    }

    /// <summary>Every bucket in full, one entry per line, for a test's output.</summary>
    public string Detail()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Summary());
        Section(sb, "changed (Component.Field: entities)", Changed);
        Section(sb, "appeared (prototype: count)", Appeared);
        Section(sb, "vanished (prototype: count)", Vanished);
        Section(sb, "components added", ComponentsAdded);
        Section(sb, "components removed", ComponentsRemoved);
        Section(sb, "uncomparable", Uncomparable);

        if (HandlerExceptions.Count > 0)
        {
            sb.AppendLine("handler exceptions:");
            foreach (var (proto, error) in HandlerExceptions)
                sb.AppendLine($"  {proto}: {error}");
        }

        if (RevertFailures.Count > 0)
        {
            sb.AppendLine("revert failures:");
            foreach (var (key, reason) in RevertFailures)
                sb.AppendLine($"  {key}: {reason}");
        }

        return sb.ToString();

        static void Section(StringBuilder sb, string title, SortedDictionary<string, int> bucket)
        {
            if (bucket.Count == 0)
                return;

            sb.AppendLine($"{title}:");
            foreach (var (key, count) in bucket.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
                sb.AppendLine($"  {count,6}  {key}");
        }
    }

    private static string Join(SortedDictionary<string, int> bucket) =>
        string.Join(", ", bucket.Select(kv => $"{kv.Key} x{kv.Value}"));
}

/// <summary>
/// A serialization context that writes an entity reference as its live uid. The probe stubs every
/// reference to one value, which is right for "can this be written" and wrong for "did this
/// change": a container's item grid keyed by uid would render the same before and after a fill.
/// </summary>
internal sealed class DrydockEntityRefWriter : ISerializationContext, ITypeWriter<EntityUid>, ITypeWriter<NetEntity>
{
    public SerializationManager.SerializerProvider SerializerProvider { get; }

    public bool WritingReadingPrototypes => false;

    public DrydockEntityRefWriter(ISerializationManager serialization)
    {
        SerializerProvider = new SerializationManager.SerializerProvider(serialization);
        SerializerProvider.RegisterSerializer(this);
    }

    public DataNode Write(ISerializationManager serializationManager, EntityUid value, IDependencyCollection dependencies,
        bool alwaysWrite = false, ISerializationContext? context = null)
        => new ValueDataNode(value.Id.ToString(CultureInfo.InvariantCulture));

    public DataNode Write(ISerializationManager serializationManager, NetEntity value, IDependencyCollection dependencies,
        bool alwaysWrite = false, ISerializationContext? context = null)
        => new ValueDataNode(value.Id.ToString(CultureInfo.InvariantCulture));
}
