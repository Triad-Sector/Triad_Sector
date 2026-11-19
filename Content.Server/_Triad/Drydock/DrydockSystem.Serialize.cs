using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Content.Shared._Triad.CCVar;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Map.Events;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Utility;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The store's serialize phase, driven one entity at a time against the tick budget.
///
/// <para><see cref="MapLoaderSystem.TrySaveGrid"/> serializes a whole ship inside one call that
/// content cannot interrupt, and that call is the store's entire tick spike: the pipeline's worst
/// slice equals the serialize time to a tenth of a millisecond, every time. Nothing else in the
/// store is above the budget any more.</para>
///
/// <para>This reproduces what <c>TrySaveGrid</c> does, with the per-entity loop hoisted out where a
/// slice can suspend it. That is possible because every part of the engine's serializer this needs
/// is public: the constructor, <c>IsSerializable</c>, <c>ReserveYamlIds</c>, <c>SerializeEntity</c>,
/// <c>Write</c> and <c>GetCategory</c>. The two members the engine's own wrapper uses that are not
/// public are both avoidable rather than blocking:</para>
///
/// <list type="bullet">
/// <item><c>Truncate</c>'s setter marks the grid's parent so a reference to it is not reported as an
/// orphan. <c>SerializationOptions.ErrorOnOrphan</c> is public and suppresses the same report, and
/// under <see cref="MissingEntityBehaviour.Ignore"/> the reference is written as invalid either way,
/// which is what a retrieve expects since it re-parents the grid on load.</item>
/// <item><c>InitializeTileMap</c> re-uses a previously loaded tile map so that saved files do not
/// churn their tile ids. The engine's own comment says it "is not actually required". A drydock
/// document is an opaque blob in a database column that nothing ever diffs, so the churn costs
/// nothing and the ids stay internally consistent because the same document carries its own
/// tilemap.</item>
/// </list>
///
/// <para>Yielding mid-serialize is safe here for the reason the rest of the pipeline is: the ship is
/// frozen on a paused private map before this runs. Nothing ticks it, nothing is in PVS, and no
/// system is iterating it. On a live grid this walk would tear its own snapshot.</para>
/// </summary>
public sealed partial class DrydockSystem
{
    /// <summary>
    /// The sliced twin of <c>TrySaveGrid</c>. Returns the document, or null if the grid would not
    /// serialize, matching <c>TrySaveGrid</c>'s false.
    /// </summary>
    /// <remarks>
    /// The event pair is raised because the engine raises it and handlers act on it: a
    /// <see cref="BeforeSerializationEvent"/> handler may ADD entities to the set being written, and
    /// one engine handler does. Skipping it would produce a document that differs from the engine's
    /// for reasons nothing in this file could explain later.
    /// </remarks>
    private async Task<string?> SerializeGridSliced(
        DrydockStoreContext ctx,
        IDrydockSlice slice,
        SerializationOptions options)
    {
        var gridUid = ctx.GridUid;

        if (!HasComp<MapGridComponent>(gridUid))
        {
            Log.Error($"{ToPrettyString(gridUid)} is not a grid.");
            return null;
        }

        var opts = options;
        opts.Category = FileCategory.Grid;

        // The engine sets Truncate instead; see the class remarks. Without this every store logs one
        // orphan error for the staging map the grid is parented to.
        opts.ErrorOnOrphan = false;

        var roots = new HashSet<EntityUid> { gridUid };
        var maps = new HashSet<MapId> { Transform(gridUid).MapID };

        // Handlers may add to roots. Raised before the walk for that reason.
        RaiseLocalEvent(new BeforeSerializationEvent(roots, maps, opts.Category));

        var serializer = new EntitySerializer(_dependency, opts);

        // Built to match EntitySerializer.RecursivelyIncludeChildren exactly, and the parity is
        // deliberate down to the insertion order. Pruning has to match because an unserializable
        // entity takes its whole subtree with it rather than being skipped in place, and getting
        // that wrong writes a document holding a mech's contents but not the mech. ORDER has to
        // match for a subtler reason: ReserveYamlIds walks the set to hand out yaml ids, so a set
        // built in a different order hands out different ids, and two documents that describe the
        // same ship in different numbering cannot be compared to each other. The shadow check below
        // is what that buys.
        var set = new HashSet<EntityUid>();

        foreach (var root in roots)
        {
            if (!serializer.IsSerializable(root))
            {
                Log.Error($"Drydock store cannot serialize {ToPrettyString(root)}: the root is not serializable.");
                return null;
            }

            IncludeChildren(serializer, root, set);
        }

        // Every id reserved before the first entity is written, which is what the batch path does
        // and what keeps a reference from an early entity to a late one resolving to the same id the
        // late one is eventually filed under. ReserveYamlId is idempotent, so SerializeEntity
        // re-reserving per entity below is a no-op.
        serializer.ReserveYamlIds(set);

        // The serialize order is the set's own, which is what SerializeEntitiesInternal iterates.
        var order = new List<EntityUid>(set);

        await slice.Begin(DrydockPhase.Serialize, order.Count);

        for (var i = 0; i < order.Count; i++)
        {
            var uid = order[i];

            // The tree was fixed before the first suspension and the ship is frozen, so this should
            // never fire. It is here because "should never" and "cannot" are different, and the
            // engine throws rather than skipping.
            if (TerminatingOrDeleted(uid))
            {
                Log.Error($"Drydock store lost {ToPrettyString(uid)} mid-serialize; the document would be short one entity.");
                return null;
            }

            serializer.SerializeEntity(uid);
            await StoreStep(ctx, slice, i);
        }

        var data = serializer.Write();
        var category = serializer.GetCategory();

        // Before the AfterSerializationEvent, because a handler may edit the node tree and the point
        // of the shadow is to compare what the two WALKS produced, not what a handler did to one of
        // them afterwards.
        if (_cfg.GetCVar(TriadCCVars.DrydockSerializeShadowCompare))
            ShadowCompare(gridUid, roots, opts, data);

        RaiseLocalEvent(new AfterSerializationEvent(roots, data, category));

        if (category != FileCategory.Grid)
        {
            Log.Error($"Drydock store failed to save {ToPrettyString(gridUid)} as a grid. Output: {category}.");
            return null;
        }

        // Pure byte work from here: the node tree holds no entity reference, so turning it into text
        // is the same kind of work as the hash and the compression and goes the same place.
        return await slice.Await(Task.Run(() => EmitDocument(data)));
    }

    /// <summary>
    /// <see cref="EntitySerializer.RecursivelyIncludeChildren"/>, which is private. Recursive rather
    /// than stack-driven so the insertion order into <paramref name="set"/> is the engine's; see the
    /// caller for why order is load-bearing here and not just tidiness.
    /// </summary>
    private void IncludeChildren(EntitySerializer serializer, EntityUid uid, HashSet<EntityUid> set)
    {
        if (!serializer.IsSerializable(uid))
            return;

        set.Add(uid);

        var children = Transform(uid).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            IncludeChildren(serializer, child, set);
        }
    }

    /// <summary>
    /// Runs the engine's own <c>SerializeEntityRecursive</c> over the same grid, in the same tick,
    /// and reports anything the sliced walk wrote differently. This is the fidelity guard: a store
    /// is allowed to be slow, and it is not allowed to write a different ship.
    ///
    /// <para>The control is what makes it worth anything. Both documents come from one grid at one
    /// instant, with no reload, no map init and no random roll in between, so a difference is the
    /// walk's and cannot be content nondeterminism. An A/B across two separately loaded copies of a
    /// hull cannot make that claim: measured on the roster, two loads of the same vessel disagree by
    /// tens of entities all on their own.</para>
    ///
    /// <para>Doubles the cost of a store while it is on, which is why it is a cvar and not the
    /// default. It is meant for a soak on the test box and for the roster sweep, not for prod.</para>
    /// </summary>
    private void ShadowCompare(
        EntityUid gridUid,
        HashSet<EntityUid> roots,
        SerializationOptions opts,
        MappingDataNode sliced)
    {
        MappingDataNode engine;
        try
        {
            var shadow = new EntitySerializer(_dependency, opts);
            shadow.SerializeEntityRecursive(roots);
            engine = shadow.Write();
        }
        catch (Exception e)
        {
            Log.Error($"Drydock shadow compare could not run the engine walk on {ToPrettyString(gridUid)}: {e}");
            return;
        }

        var differences = new List<string>();
        CompareEntitySections(engine, sliced, differences);

        if (differences.Count == 0)
        {
            Log.Info($"Drydock shadow compare: {ToPrettyString(gridUid)} identical across both walks.");
            return;
        }

        Log.Error(
            $"Drydock shadow compare found {differences.Count} difference(s) on {ToPrettyString(gridUid)}. "
            + "The sliced walk wrote a different ship than the engine walk:\n  "
            + string.Join("\n  ", differences.Take(25)));
    }

    /// <summary>
    /// Compares the two documents' entity sections, keyed by yaml uid, and names what differs.
    ///
    /// <para>One exclusion, stated rather than silent: the grid's own <c>MapGrid</c> component. Its
    /// chunk payload encodes tiles as integer ids resolved through the document's own tilemap, and
    /// the sliced walk skips <c>InitializeTileMap</c>, which only exists to re-use a previously
    /// loaded numbering so saved files do not churn. Each document is internally consistent, they
    /// simply number the same tiles differently, so comparing those bytes reports a difference that
    /// is not one. Everything else is compared in full: every entity, every component, every
    /// field.</para>
    /// </summary>
    private static void CompareEntitySections(MappingDataNode engine, MappingDataNode sliced, List<string> into)
    {
        var left = IndexEntities(engine);
        var right = IndexEntities(sliced);

        foreach (var (uid, entry) in left)
        {
            if (!right.TryGetValue(uid, out var other))
            {
                into.Add($"uid {uid} ({entry.Proto}) is in the engine document and not in the sliced one");
                continue;
            }

            var a = Canonical(entry.Node);
            var b = Canonical(other.Node);

            if (a != b)
                into.Add($"uid {uid} ({entry.Proto}) differs {Divergence(a, b)}");
        }

        foreach (var (uid, entry) in right)
        {
            if (!left.ContainsKey(uid))
                into.Add($"uid {uid} ({entry.Proto}) is in the sliced document and not in the engine one");
        }
    }

    /// <summary>
    /// Where two canonical strings first disagree, with a window either side. Printing the first 300
    /// characters instead is useless here: two serializations of one entity agree for most of their
    /// length and differ somewhere in the middle, so a fixed prefix shows two identical strings and
    /// says a difference exists somewhere you cannot see.
    /// </summary>
    private static string Divergence(string a, string b)
    {
        var limit = Math.Min(a.Length, b.Length);
        var at = 0;
        while (at < limit && a[at] == b[at])
            at++;

        const int window = 140;
        var from = Math.Max(0, at - 40);

        return $"at char {at} of {a.Length}/{b.Length}:\n"
               + $"      engine: ...{Window(a, from, window)}\n"
               + $"      sliced: ...{Window(b, from, window)}";

        static string Window(string value, int from, int length)
        {
            if (from >= value.Length)
                return "<end of string>";

            return value.Substring(from, Math.Min(length, value.Length - from));
        }
    }

    /// <summary>
    /// Every entity node in a document, keyed by its yaml uid, carrying the prototype id from the
    /// group that holds it. The id lives on the GROUP and not on the entity, so an entity node read
    /// on its own has no prototype and reports as one, which made the first version of this report
    /// call every difference <c>&lt;no-prototype&gt;</c>.
    /// </summary>
    private static Dictionary<string, (string Proto, MappingDataNode Node)> IndexEntities(MappingDataNode document)
    {
        var result = new Dictionary<string, (string, MappingDataNode)>();

        if (!document.TryGet<SequenceDataNode>("entities", out var groups))
            return result;

        foreach (var group in groups)
        {
            if (group is not MappingDataNode groupNode
                || !groupNode.TryGet<SequenceDataNode>("entities", out var entities))
            {
                continue;
            }

            var proto = groupNode.TryGet<ValueDataNode>("proto", out var protoNode) && protoNode.Value.Length > 0
                ? protoNode.Value
                : "<no-prototype>";

            foreach (var entity in entities)
            {
                if (entity is not MappingDataNode entityNode
                    || !entityNode.TryGet<ValueDataNode>("uid", out var uid))
                {
                    continue;
                }

                result[uid.Value] = (proto, entityNode);
            }
        }

        return result;
    }

    /// <summary>
    /// A node as a stable string. Mapping keys are sorted, so two documents that agree on content
    /// but not on key order compare equal; sequence order is preserved, because for a component's
    /// list field the order is content. A <c>MapGrid</c> component is replaced by a placeholder, for
    /// the reason <see cref="CompareEntitySections"/> gives.
    /// </summary>
    private static string Canonical(DataNode node)
    {
        var builder = new StringBuilder();
        WriteNode(node, builder);
        return builder.ToString();

        static void WriteNode(DataNode node, StringBuilder builder)
        {
            switch (node)
            {
                case ValueDataNode value:
                    builder.Append('"').Append(value.Value).Append('"');
                    break;

                case SequenceDataNode sequence:
                    builder.Append('[');
                    foreach (var item in sequence)
                    {
                        if (item is MappingDataNode comp
                            && comp.TryGet<ValueDataNode>("type", out var type)
                            && type.Value == "MapGrid")
                        {
                            builder.Append("<MapGrid chunks excluded: tilemap numbering>,");
                            continue;
                        }

                        WriteNode(item, builder);
                        builder.Append(',');
                    }

                    builder.Append(']');
                    break;

                case MappingDataNode mapping:
                    builder.Append('{');
                    var keys = new List<string>(mapping.Keys);
                    keys.Sort(StringComparer.Ordinal);
                    foreach (var key in keys)
                    {
                        builder.Append(key).Append(':');
                        WriteNode(mapping[key], builder);
                        builder.Append(',');
                    }

                    builder.Append('}');
                    break;

                default:
                    builder.Append(node.GetType().Name);
                    break;
            }
        }
    }

    /// <summary>
    /// The node tree as YAML text, byte for byte the way the engine writes it
    /// (<c>MapLoaderSystem.Write</c>). The mapping fix and the emitter are both public, so this is
    /// the engine's own emission rather than a lookalike, which matters because the deserializer on
    /// the other end is the engine's.
    /// </summary>
    internal static string EmitDocument(MappingDataNode data) => EmitDocument(data, EmitterSettings.Default.NewLine);

    /// <summary>
    /// <see cref="EmitDocument(MappingDataNode)"/> with the line ending chosen, so a document re-written
    /// on a different platform from the one that stored it keeps its own. The emitter takes its line
    /// ending from its settings, not from the writer; every other setting is the default
    /// (<c>new Emitter(writer)</c> is <c>EmitterSettings.Default</c>).
    /// </summary>
    internal static string EmitDocument(MappingDataNode data, string newLine)
    {
        using var writer = new StringWriter();
        var document = new YamlDocument(data.ToYamlNode());
        var stream = new YamlStream { document };
        stream.Save(new YamlMappingFix(new Emitter(writer, EmitterSettings.Default.WithNewLine(newLine))), false);
        return writer.ToString();
    }
}
