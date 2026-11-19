using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;
using Robust.Shared.Timing;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The fidelity layer. The engine serializer owns a ship's structure; this owns the state it cannot
/// write. At store it walks the grid and, for every populated <c>[DataField]</c> the serializer
/// refuses, either captures the value into a <see cref="DrydockCapturedStateComponent"/> sidecar or
/// strips it, and either way clears the live field so the grid can be written. At retrieve it
/// re-applies what it captured over the reborn entities.
///
/// <para>It touches no content, and the only fork-specific knob is
/// <see cref="DrydockSerializationGap.CapturedTypes"/>. The default for anything the serializer
/// cannot write is to strip it, which is the safe direction: a stripped field comes back at its
/// default, where a forgotten entity would come back not at all.</para>
/// </summary>
public sealed partial class DrydockFidelitySystem : EntitySystem
{
    [Dependency] private ISerializationManager _serialization = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;

    /// <summary>
    /// The two keys the appearance sidecar wraps each value in. Both are part of a persisted
    /// format, like <c>DrydockReflectiveCapture</c>'s type tag: renaming either invalidates the
    /// appearance of every revision written before the change, so they move only behind a
    /// <see cref="DrydockFormat"/> bump.
    /// </summary>
    private const string AppearanceValueType = "t";

    /// <inheritdoc cref="AppearanceValueType"/>
    private const string AppearanceValue = "v";

    private DrydockReflectiveCapture _capture = default!;

    /// <summary>
    /// The probe's context, which gives the base serialization manager the extra capability the
    /// map serializer has over it: writing <see cref="EntityUid"/> and <see cref="NetEntity"/>
    /// references. Without it every field that reaches an entity reference (transform parents,
    /// container graphs, action lists, deed uids, artifact node graphs) reads as unserializable to
    /// the probe, which is a false negative, since the map serializer round-trips all of them
    /// through exactly this kind of context. Probing with it leaves those alone and flags only
    /// genuine gaps.
    /// </summary>
    private DrydockEntityRefProbe _probe = default!;

    /// <summary>
    /// Cached per type, since serializability is structural. A failure is cached unconditionally,
    /// because a type with no serializer never acquires one at runtime. A success is cached only
    /// when it was proven against a non-empty value: an empty collection writes fine even when its
    /// element type cannot, and caching that would poison every later probe of the same type.
    /// </summary>
    private readonly Dictionary<Type, bool> _serializable = new();

    /// <inheritdoc cref="_serializable"/>
    /// <remarks>
    /// The empty half, kept apart on purpose. Membership means "writes when empty", which is all an
    /// empty sample can establish; it never satisfies a probe of a populated value of the same type.
    /// </remarks>
    private readonly HashSet<Type> _emptyWritable = new();

    /// <summary>
    /// Component types whose every [DataField] member type is already proven serializable in
    /// <see cref="_serializable"/>, so the capture walk skips them without reflecting over their
    /// fields; filled as the walk learns, process-lifetime like the caches it derives from.
    /// </summary>
    private readonly HashSet<Type> _nothingToCapture = new();

    public override void Initialize()
    {
        base.Initialize();
        _capture = new DrydockReflectiveCapture(_serialization);
        _probe = new DrydockEntityRefProbe(_serialization);
        _refWriter = new DrydockEntityRefWriter(_serialization);
    }

    /// <summary>
    /// Store step, called before the grid is serialized: capture or strip every unserializable
    /// populated field across the grid so the serializer can write it.
    ///
    /// <para>This mutates live components, and unlike the copying sidecars it must clear the live
    /// field, because the field choking the serializer is the entire reason it is here. So the
    /// ledger holds the original in-memory values of both buckets, and the caller's abort path hands
    /// it to <see cref="RestoreSnapshot"/> to put them straight back with no serialization round trip
    /// in the way.</para>
    ///
    /// <para>The ledger belongs to the caller, not to this walk: it is passed in rather than created
    /// here, since clearing happens per field, per entity, and the walk can now be abandoned
    /// half-way, so a ledger that only became visible on return would leave a ship blanked with no
    /// record of what was taken off it. Anything that throws between here and the commit leaves a
    /// live ship with blanked fields unless the caller restores from that ledger.</para>
    /// </summary>
    /// <remarks>
    /// The tick-budgeted form. Every snapshot entry is appended before the field it records is
    /// cleared, so an abort part-way through still restores every field already blanked, and the
    /// caller has assigned <paramref name="capture"/> to its own context before calling.
    ///
    /// <para>The tree is materialised first. The lazy walk reads each entity's transform after it has
    /// yielded the previous one, which is fine inside one tick and is not fine across fifty, and each
    /// entity is re-checked as it is consumed.</para>
    /// </remarks>
    public async Task CaptureAndStripSliced(EntityUid grid, DrydockFidelityCapture capture, IDrydockSlice slice)
    {
        var tree = GridTreeList(grid);
        await slice.Begin(DrydockPhase.Capture, tree.Count);

        for (var i = 0; i < tree.Count; i++)
        {
            var uid = tree[i];
            if (!TerminatingOrDeleted(uid))
                CaptureAndStripEntity(uid, capture);

            await slice.Step(i);
        }
    }

    /// <summary>One entity's worth of the capture walk. Never yields, so a component's field set is
    /// always taken whole.</summary>
    private void CaptureAndStripEntity(EntityUid uid, DrydockFidelityCapture capture)
    {
        DrydockCapturedStateComponent? sidecar = null;

        foreach (var comp in AllComps(uid).ToList())
        {
            if (comp is DrydockCapturedStateComponent)
                continue;

            var compType = comp.GetType();
            // Every member type already proven; nothing here can need capturing.
            if (_nothingToCapture.Contains(compType))
                continue;

            var fields = DataFields(compType);
            foreach (var member in fields)
            {
                var value = GetMember(comp, member);
                if (value == null)
                    continue;

                var memberType = MemberType(member);
                if (IsSerializable(memberType, value))
                    continue;

                if (IsCaptureType(memberType) && _capture.TryCapture(value) is { } node)
                {
                    if (sidecar == null)
                    {
                        // On the ledger before the component goes on, so an abort between the two
                        // still knows to take it back off.
                        capture.Sidecarred.Add(uid);
                        sidecar = EnsureComp<DrydockCapturedStateComponent>(uid);
                    }

                    var key = $"{compType.Name}|{member.Name}";
                    sidecar.Fields[key] = EncodeNode(node);
                    capture.CapturedKeys.Add(key);
                }
                else
                {
                    capture.Stripped++;
                }

                // Snapshot the live value before clearing, so an aborted store puts it back
                // exactly. Captured or stripped, both were cleared and both restore.
                capture.Snapshot.Add((uid, comp, member, value));

                ClearMember(comp, member, memberType);
                DirtyIfNetworked(uid, comp);
            }

            if (fields.Length == 0 || fields.All(m => _serializable.TryGetValue(MemberType(m), out var proven) && proven))
                _nothingToCapture.Add(compType);
        }
    }

    /// <summary>
    /// Abort path, called from the store's unwind: put every field <see cref="CaptureAndStripSliced"/>
    /// cleared back to its original live value and remove the sidecars it added, leaving the
    /// still-live ship exactly as usable as it was. This works on the same live entities, with no
    /// serialization involved, which is what separates it from <see cref="RestoreCapturedSliced"/>.
    /// </summary>
    /// <remarks>
    /// Synchronous, and it has to stay that way: it runs from an unwind, and an unwind that could
    /// suspend is an unwind a cancelled pipeline could never finish. The per-entry guards are what
    /// slicing added. The component references in the ledger can now be many ticks old and an entity
    /// that died in the meantime would throw on the dirty call - inside the unwind, which would
    /// abandon the ship on its staging map with no return leg.
    /// </remarks>
    public void RestoreSnapshot(DrydockFidelityCapture capture)
    {
        foreach (var (uid, comp, member, original) in capture.Snapshot)
        {
            if (comp.Deleted || TerminatingOrDeleted(uid))
                continue;

            SetMember(comp, member, original);
            DirtyIfNetworked(uid, comp);
        }

        foreach (var uid in capture.Sidecarred)
        {
            if (!TerminatingOrDeleted(uid))
                RemComp<DrydockCapturedStateComponent>(uid);
        }
    }

    /// <summary>
    /// Retrieve step, called after the grid is reloaded: re-apply captured state over the reborn
    /// entities and remove the sidecars.
    ///
    /// <para>Every failure here degrades to a counted, named skip rather than an exception,
    /// deliberately: a ship that comes back with one machine's stock missing beats a ship that will
    /// not come back. The returned report is what makes those skips visible, and a skip count above
    /// zero after an upstream merge is the signature of a rename orphaning a key.</para>
    /// </summary>
    /// <remarks>
    /// The tick-budgeted form. Snapshot-then-apply over a materialised tree, re-checking each entity
    /// as it is consumed, and it opens its own phase rather than stepping inside one the caller
    /// opened: two restores run back to back and neither can know the other's item count.
    /// </remarks>
    public async Task<DrydockFidelityRestore> RestoreCapturedSliced(EntityUid grid, IDrydockSlice slice)
    {
        var report = new DrydockFidelityRestore();
        var tree = GridTreeList(grid);
        await slice.Begin(DrydockPhase.Fidelity, tree.Count);

        for (var i = 0; i < tree.Count; i++)
        {
            var uid = tree[i];
            if (TerminatingOrDeleted(uid) || !TryComp<DrydockCapturedStateComponent>(uid, out var sidecar))
            {
                await slice.Step(i);
                continue;
            }

            // Built once per entity rather than scanned per key. Component names are unique in the
            // registry, so the simple type name the key carries identifies one component.
            var byName = AllComps(uid).ToDictionary(c => c.GetType().Name, c => c);

            foreach (var (key, encoded) in sidecar.Fields)
            {
                if (!TrySplitKey(key, out var compName, out var fieldName))
                {
                    report.Skip(key, "malformed key");
                    continue;
                }

                if (!byName.TryGetValue(compName, out var comp))
                {
                    report.Skip(key, "no such component on the restored entity");
                    continue;
                }

                var member = DataFields(comp.GetType()).FirstOrDefault(m => m.Name == fieldName);
                if (member == null)
                {
                    report.Skip(key, "component no longer has that field");
                    continue;
                }

                try
                {
                    SetMember(comp, member, _capture.Restore(MemberType(member), DecodeNode(encoded)));
                    DirtyIfNetworked(uid, comp);
                    report.Applied++;
                }
                catch (Exception e)
                {
                    report.Skip(key, e.Message);
                }
            }

            RemComp<DrydockCapturedStateComponent>(uid);
            await slice.Step(i);
        }

        WarnSkips(report, grid, "drydock fidelity restore", "captured field(s)");
        return report;
    }

    /// <summary>
    /// Store step for the appearance carrier: copy every entity's live appearance data into a
    /// <see cref="DrydockAppearanceComponent"/> sidecar, leaving the live data alone.
    ///
    /// <para>This is the one carrier the probe above could never have found, because appearance
    /// data is not a <c>[DataField]</c> at all and the probe only ever asks about declared fields.
    /// See the sidecar's own summary for what that costs a retrieved ship.</para>
    ///
    /// <para>Most of what this captures is redundant, because most systems set their appearance in
    /// <c>ComponentStartup</c>, which does re-run on load, and those entities re-derive the same
    /// values a moment later. The capture is for the rest: keys written only on map init or only on
    /// a state change that will not happen again. Capturing both is deliberate, since nothing at
    /// store time can tell them apart, and re-applying a value the system was about to derive
    /// identically costs nothing.</para>
    ///
    /// <para>A key whose value cannot be captured at all is dropped rather than failing the store: an
    /// appearance value is a visual, and no visual is worth refusing a ship over.</para>
    /// </summary>
    /// <remarks>
    /// The tick-budgeted form. Like the ledgers on the store's context, <paramref name="injected"/>
    /// belongs to the caller and is appended to before the sidecar it records is added, so an abort
    /// part-way through the walk still takes back off every sidecar already put on.
    /// </remarks>
    public async Task CaptureAppearanceSliced(EntityUid grid, List<EntityUid> injected, IDrydockSlice slice)
    {
        var tree = GridTreeList(grid);
        await slice.Begin(DrydockPhase.Sidecars, tree.Count);

        for (var i = 0; i < tree.Count; i++)
        {
            var uid = tree[i];
            if (TerminatingOrDeleted(uid) || !TryComp<AppearanceComponent>(uid, out var appearance))
            {
                await slice.Step(i);
                continue;
            }

            DrydockAppearanceComponent? sidecar = null;
            foreach (var (key, value) in LiveAppearance(appearance))
            {
                if (_capture.TryCapture(value) is not { } node)
                    continue;

                // The value's own concrete type rides alongside it. An appearance value is declared
                // as object, so unlike a data field there is no declared type for the restore to
                // read it back as.
                var wrapped = new MappingDataNode();
                wrapped.Add(AppearanceValueType, new ValueDataNode(value.GetType().AssemblyQualifiedName!));
                wrapped.Add(AppearanceValue, node);

                if (sidecar == null)
                {
                    injected.Add(uid);
                    sidecar = EnsureComp<DrydockAppearanceComponent>(uid);
                }

                sidecar.Data[$"{key.GetType().FullName}|{key}"] = EncodeNode(wrapped);
            }

            await slice.Step(i);
        }
    }

    /// <summary>
    /// Retrieve step for the appearance carrier: put every captured key back through the appearance
    /// system and drop the sidecars.
    ///
    /// <para>Runs early in the rehydration block, before the steps that correct specific machines.
    /// That order is load-bearing in one direction: a lathe's captured appearance says it was
    /// mid-production, and the marker that made that true does not ride the save, so the lathe step
    /// afterwards is what settles the animation against the marker the ship actually came back
    /// with. Restoring appearance last would reinstate the frozen animation this carrier exists to
    /// fix.</para>
    /// </summary>
    /// <remarks>Same shape as <see cref="RestoreCapturedSliced"/>: materialised tree, re-checked per
    /// entity, its own phase.</remarks>
    public async Task<DrydockFidelityRestore> RestoreAppearanceSliced(EntityUid grid, IDrydockSlice slice)
    {
        var report = new DrydockFidelityRestore();
        var tree = GridTreeList(grid);
        await slice.Begin(DrydockPhase.Fidelity, tree.Count);

        for (var i = 0; i < tree.Count; i++)
        {
            var uid = tree[i];
            if (TerminatingOrDeleted(uid) || !TryComp<DrydockAppearanceComponent>(uid, out var sidecar))
            {
                await slice.Step(i);
                continue;
            }

            foreach (var (key, encoded) in sidecar.Data)
            {
                try
                {
                    if (!TrySplitKey(key, out var keyTypeName, out var keyMember))
                    {
                        report.Skip(key, "malformed key");
                        continue;
                    }

                    var keyType = DrydockReflectiveCapture.ResolveType(keyTypeName);
                    if (keyType is not { IsEnum: true })
                    {
                        report.Skip(key, "no such appearance key type");
                        continue;
                    }

                    if (!Enum.TryParse(keyType, keyMember, out var parsed) || parsed is not Enum enumKey)
                    {
                        report.Skip(key, "appearance key type no longer has that member");
                        continue;
                    }

                    var node = (MappingDataNode) DecodeNode(encoded);

                    var valueType = DrydockReflectiveCapture.ResolveType(
                        ((ValueDataNode) node[AppearanceValueType]).Value);

                    if (valueType == null)
                    {
                        report.Skip(key, "no such appearance value type");
                        continue;
                    }

                    if (_capture.Restore(valueType, node[AppearanceValue]) is not { } value)
                    {
                        report.Skip(key, "value restored as null");
                        continue;
                    }

                    _appearance.SetData(uid, enumKey, value);
                    report.Applied++;
                }
                catch (Exception e)
                {
                    report.Skip(key, e.Message);
                }
            }

            RemComp<DrydockAppearanceComponent>(uid);
            await slice.Step(i);
        }

        WarnSkips(report, grid, "drydock appearance restore", "key(s)");
        return report;
    }

    /// <summary>
    /// How a captured node is held in a sidecar string: its YAML text, base64 so the map document
    /// carries it as one opaque scalar. <see cref="DecodeNode"/> is the inverse, and the two are a
    /// persisted format, so they move only behind a <see cref="DrydockFormat"/> bump.
    /// </summary>
    private static string EncodeNode(DataNode node)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(node.ToString()));
    }

    /// <inheritdoc cref="EncodeNode"/>
    private static DataNode DecodeNode(string encoded)
    {
        var yaml = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        using var reader = new StringReader(yaml);
        return DataNodeParser.ParseYamlStream(reader).First().Root;
    }

    /// <summary>
    /// Splits a sidecar key at its first <c>|</c>: component and field for a captured field, key
    /// type and member for an appearance entry. False when the key has no separator.
    /// </summary>
    private static bool TrySplitKey(string key, out string head, out string tail)
    {
        var sep = key.IndexOf('|');
        if (sep < 0)
        {
            head = tail = string.Empty;
            return false;
        }

        head = key[..sep];
        tail = key[(sep + 1)..];
        return true;
    }

    /// <summary>One warning per restore naming every skip, and nothing when there were none.</summary>
    private void WarnSkips(DrydockFidelityRestore report, EntityUid grid, string restore, string noun)
    {
        if (report.Skipped.Count == 0)
            return;

        Log.Warning(
            $"{restore} skipped {report.Skipped.Count} {noun} on grid {ToPrettyString(grid)}: "
            + string.Join("; ", report.Skipped.Select(s => $"{s.Key} ({s.Reason})")));
    }

    /// <summary>
    /// The live appearance dictionary. It is internal to the engine and its component is
    /// access-locked to the appearance system, so the component state is the supported read: the
    /// server's handler hands back the whole live dictionary whatever tick is asked for.
    /// </summary>
    private Dictionary<Enum, object> LiveAppearance(AppearanceComponent appearance)
    {
        return EntityManager.GetComponentState(EntityManager.EventBus, appearance, null, GameTick.Zero)
            is AppearanceComponentState state
            ? state.Data
            : new Dictionary<Enum, object>();
    }

    private bool IsSerializable(Type type, object value)
    {
        // Emptiness is decided before the populated cache is ever read. An empty collection writes
        // whatever its element type is, so a populated value's "no serializer" verdict says nothing
        // about it, and reading that cached false first would capture and clear every later empty
        // field of the same type. The captured-key set is persisted in the manifest and hashed into
        // CapturedKeyHash, so it must be a function of the ship alone, never of what the server probed
        // earlier (DrydockCaptureVerdictOrderTest).
        //
        // An empty value therefore has its own memory. It cannot prove its type serializable, so its
        // success never goes into _serializable, but a type that writes when empty writes when empty
        // every time, and remembering that spares a probe per occurrence of the most common field
        // state on a ship.
        var empty = value is ICollection { Count: 0 };
        if (empty)
        {
            if (_emptyWritable.Contains(type))
                return true;
        }
        else if (_serializable.TryGetValue(type, out var cached))
        {
            return cached;
        }

        try
        {
            _serialization.WriteValue(type, value, alwaysWrite: true, context: _probe);

            if (empty)
                _emptyWritable.Add(type);
            else
                _serializable[type] = true;

            return true;
        }
        catch (Exception e)
        {
            // The classification is shared with the audit gate on purpose. The gate's control is
            // what proves it still recognises a real gap, and that proof only covers this code
            // while this code is the same code.
            if (!DrydockSerializationGap.IsNoCoverage(e))
                return true; // Some other failure. Not a gap; leave it to the serializer.

            _serializable[type] = false;
            return false;
        }
    }

    /// <summary>
    /// A type is worth capturing when it, an element of it, or any generic argument anywhere inside
    /// it is on the manifest, so a dictionary of market data is captured because its value type is.
    /// </summary>
    private static bool IsCaptureType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (DrydockSerializationGap.CapturedTypes.Contains(type))
            return true;
        if (type.IsArray)
            return IsCaptureType(type.GetElementType()!);

        return type.IsGenericType && type.GetGenericArguments().Any(IsCaptureType);
    }

    /// <summary>
    /// Every entity on the grid, including entities inside containers, since contained entities are
    /// transform children of their container's owner. Materialised, never lazy: every walk here is
    /// now sliced, and a lazy walk reads the next entity's transform after the consumer has already
    /// parked on the previous one, which is harmless inside one tick and is not harmless across
    /// fifty.
    ///
    /// <para>The same set as an <c>AllEntityQuery</c> filtered on <c>GridUid</c>, because the engine
    /// sets <c>GridUid</c> from the parent chain and from nothing else, at the cost of the ship
    /// rather than the cost of the sector. The store's sidecar and purge scans and the retrieve's
    /// sweeps walk this instead of querying the world: a world query is the one un-yieldable cost
    /// that grows with the round rather than with the hull. It has no paused check either, which the
    /// frozen ship needs.</para>
    /// </summary>
    public List<EntityUid> GridTreeList(EntityUid grid)
    {
        var result = new List<EntityUid>();
        if (TerminatingOrDeleted(grid))
            return result;

        // Index-walked rather than stack-popped, so the list is its own frontier and the grid comes
        // out first.
        result.Add(grid);
        for (var i = 0; i < result.Count; i++)
        {
            var children = Transform(result[i]).ChildEnumerator;
            while (children.MoveNext(out var child))
                result.Add(child);
        }

        return result;
    }

    /// <summary>
    /// Same walk as <see cref="GridTreeList"/>, paired with the index of each entity's parent in the
    /// returned list (the root's parent is null), for a caller that needs to rebuild parent links
    /// without a second pass over the tree.
    /// </summary>
    public List<(EntityUid Uid, int? Parent)> GridTreeListWithParents(EntityUid grid)
    {
        var result = new List<(EntityUid Uid, int? Parent)>();
        if (TerminatingOrDeleted(grid))
            return result;

        result.Add((grid, null));
        for (var i = 0; i < result.Count; i++)
        {
            var children = Transform(result[i].Uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                result.Add((child, i));
        }

        return result;
    }

    /// <summary>
    /// Memoized because the answer is a property of the type and the store asks it once per
    /// component per entity: uncached it built two reflection arrays and asked
    /// <see cref="MemberInfo.GetCustomAttribute{T}"/> per member every time, which on a capital ship
    /// was most of the store's capture phase.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, MemberInfo[]> DataFieldCache = new();

    /// <summary>
    /// Which members of a component type the walk has to look at. Fields carrying a custom type
    /// serializer are skipped on the assumption that serializer handles them, which is the same rule
    /// the serializability audit applies.
    /// </summary>
    private static MemberInfo[] DataFields(Type type) => DataFieldCache.GetOrAdd(type, static t =>
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var members = new List<MemberInfo>();

        for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var m in cur.GetFields(flags).Cast<MemberInfo>().Concat(cur.GetProperties(flags)))
            {
                var attr = m.GetCustomAttribute<DataFieldBaseAttribute>();
                if (attr != null && attr.CustomTypeSerializer == null)
                    members.Add(m);
            }
        }

        return members.ToArray();
    });

    private static Type MemberType(MemberInfo m) =>
        m is FieldInfo fi ? fi.FieldType : ((PropertyInfo) m).PropertyType;

    private static object? GetMember(object obj, MemberInfo m) =>
        m is FieldInfo fi ? fi.GetValue(obj) : ((PropertyInfo) m).GetValue(obj);

    private static void SetMember(object obj, MemberInfo m, object? value)
    {
        if (m is FieldInfo fi)
            fi.SetValue(obj, value);
        else if (m is PropertyInfo { CanWrite: true } pi)
            pi.SetValue(obj, value);
    }

    private static void ClearMember(object obj, MemberInfo m, Type type)
    {
        object? cleared;
        if (type.IsValueType)
            cleared = Activator.CreateInstance(type);
        else if (type.IsArray)
            cleared = Array.CreateInstance(type.GetElementType()!, 0); // An empty array: a null one is refused by the serializer on any non-nullable field.
        else if (typeof(IEnumerable).IsAssignableFrom(type) && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null)
            cleared = Activator.CreateInstance(type); // An empty collection rather than null: no NRE, and it writes fine.
        else
            cleared = null;

        SetMember(obj, m, cleared);
    }
    /// <summary>
    /// Dirty only what the engine will accept being dirtied.
    ///
    /// <para>This layer walks every component carrying a populated field the serializer
    /// cannot write, and nothing about that description says the component is networked.
    /// <c>CargoMarketDataComponent</c> is a live example: it holds one of the two captured
    /// types and carries no <c>[NetworkedComponent]</c>. Dirtying it trips a debug assert in
    /// the entity manager, which on a development server throws inside the capture, aborts
    /// the store, and refuses the ship.</para>
    ///
    /// <para>The test is the component registration's <c>Networked</c> flag, which the factory
    /// sets from that same attribute when it registers the type, so it is the answer the engine
    /// asserts on, already cached per type. Every caller here is on the game thread, touching
    /// live components.</para>
    /// </summary>
    private void DirtyIfNetworked(EntityUid uid, IComponent comp)
    {
        if (Factory.GetRegistration(comp).Networked)
            Dirty(uid, comp);
    }
}

/// <summary>
/// The ledger one <see cref="DrydockFidelitySystem.CaptureAndStripSliced"/> fills, which the caller
/// creates and passes in: the live values it cleared, to put back verbatim on abort, and the
/// entities it gave a sidecar, to take back off. Discarded on a successful store, since the grid
/// despawns and there is nothing to undo.
/// </summary>
public sealed class DrydockFidelityCapture
{
    /// <summary>Every field cleared, with the exact live value to restore on abort.</summary>
    public readonly List<(EntityUid Uid, IComponent Comp, MemberInfo Member, object? Original)> Snapshot = new();

    /// <summary>Entities that received a fresh sidecar, removed again on abort.</summary>
    public readonly List<EntityUid> Sidecarred = new();

    /// <summary>
    /// Every <c>Component|Field</c> key written, sorted, so the hash below is stable regardless of
    /// walk order.
    /// </summary>
    public readonly SortedSet<string> CapturedKeys = new(StringComparer.Ordinal);

    /// <summary>How many fields were cleared without being captured. Recorded, not alarming.</summary>
    public int Stripped;

    /// <summary>
    /// The revision's <c>captured_key_hash</c>. Comparing it against the key set a later build
    /// produces is how a C# rename that would silently orphan a key becomes a detected drift rather
    /// than a field that quietly comes back at its default.
    /// </summary>
    public byte[] ComputeCapturedKeyHash()
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', CapturedKeys)));
    }
}

/// <summary>
/// What a retrieve actually managed to re-apply. Every skip is named, because the counter is the
/// alert: a skip after an upstream merge means a rename orphaned a captured key.
/// </summary>
public sealed class DrydockFidelityRestore
{
    public int Applied;

    public readonly List<(string Key, string Reason)> Skipped = new();

    public void Skip(string key, string reason) => Skipped.Add((key, reason));
}

/// <summary>
/// A minimal serialization context whose only job is to make the probe's answer match the map
/// serializer's. <c>EntitySerializer</c> supplies its own <see cref="EntityUid"/> writer, and the
/// base serialization manager has none, so a naked probe throws on any field touching an entity
/// reference and the fidelity pass would strip state the serializer round-trips perfectly well.
/// This registers a no-op writer that returns a stub node, with no logging and no uid mapping, so
/// the probe succeeds on exactly what the map serializer succeeds on.
/// </summary>
internal sealed class DrydockEntityRefProbe : ISerializationContext, ITypeWriter<EntityUid>, ITypeWriter<NetEntity>
{
    private static readonly ValueDataNode Stub = new("0");

    public SerializationManager.SerializerProvider SerializerProvider { get; }

    public bool WritingReadingPrototypes => false;

    // The provider takes the manager as of engine 287.
    public DrydockEntityRefProbe(ISerializationManager serialization)
    {
        SerializerProvider = new SerializationManager.SerializerProvider(serialization);
        SerializerProvider.RegisterSerializer(this);
    }

    public DataNode Write(
        ISerializationManager serializationManager,
        EntityUid value,
        IDependencyCollection dependencies,
        bool alwaysWrite = false,
        ISerializationContext? context = null) => Stub;

    // The map serializer writes a NetEntity exactly the way it writes an EntityUid, remapped to a
    // document id, and the base manager has no writer for it either. Without this stub every
    // NetEntity-bearing field read as a gap and was blanked before the save: an artifact's node
    // graph came out with its vertex array set to null, which the serializer then refused, so a
    // ship carrying an artifact could not be stored at all (reported on Damascus),
    // and an analysis console lost the analyzer it was linked to.
    public DataNode Write(
        ISerializationManager serializationManager,
        NetEntity value,
        IDependencyCollection dependencies,
        bool alwaysWrite = false,
        ISerializationContext? context = null) => Stub;
}
