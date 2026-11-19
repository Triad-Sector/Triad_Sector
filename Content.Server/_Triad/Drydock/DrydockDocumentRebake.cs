using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server._Triad.Drydock;

/// <summary>What <see cref="DrydockDocumentRebake.Transform(string, DrydockMigrationTable, int)"/> did.</summary>
/// <param name="Yaml">The re-baked document, or the input string itself when the text did not change.</param>
/// <param name="Changed">
/// There is something to commit: the text changed, or a format step advanced
/// <paramref name="DrydockFormatVer"/> without changing it.
/// </param>
/// <param name="AppliedRenames">Group ids rewritten, ordinal sorted by the id as written.</param>
/// <param name="AppliedSteps">Names of the format steps run and the repairs that changed something, in run order.</param>
/// <param name="DrydockFormatVer">The drydock format the document now satisfies.</param>
public sealed record DrydockDocumentRebakeResult(
    string Yaml,
    bool Changed,
    IReadOnlyList<DrydockRename> AppliedRenames,
    IReadOnlyList<string> AppliedSteps,
    int DrydockFormatVer);

/// <summary>
/// Tier 1 of the re-bake ladder: a stored ship document in, the same document with the migration
/// mappings baked into it out. Pure text to text, no entity or prototype manager contact, so it can run
/// off the game thread and be tested with synthetic tables.
/// </summary>
/// <remarks>
/// <para><b>Renames only.</b> A group whose id the table renames gets the new id, and when that id
/// already has a group the two instance lists merge, because the engine writes one group per
/// prototype. Deleted ids are left in the document on purpose: removing an entity would dangle every
/// uid that points at it (children, container contents, device links), while the loader already
/// deletes those entities after the whole document has been read and resolved. An id that is both
/// renamed and deleted is left alone as well, because the loader deletes it before it would rename it,
/// and baking the rename in would turn a deletion into a spawn.</para>
/// <para><b>Round trip.</b> A document that needs nothing is returned as the same string, untouched.
/// One that does is parsed into a node tree, edited, and written back through
/// <see cref="DrydockSystem.EmitDocument"/>, which is the engine's own emitter. Splicing the edits
/// into the text would keep every byte by construction, but a group merge moves a block of YAML under
/// a different parent, which a splice can only do by reimplementing block layout. Every stored
/// document was written by that same emitter from that same kind of tree, so re-emission reproduces
/// the untouched parts byte for byte, keeping the input's line ending rather than the platform's;
/// <c>DrydockDocumentRebakeTest</c> holds that on synthetic
/// documents and the integration test holds it on real ships. For a document some other writer
/// produced, the guarantee falls back to semantic equality under <c>DataNodeParser</c>.</para>
/// </remarks>
public static class DrydockDocumentRebake
{
    /// <summary>
    /// Migrates a document written at drydock format <paramref name="FromDrydockFormat"/> to the next
    /// one. Returns whether it changed the tree.
    /// </summary>
    public sealed record FormatStep(int FromDrydockFormat, string Name, Func<MappingDataNode, bool> Apply);

    /// <summary>
    /// Fixes a defect in stored values that no format version can key, because documents carrying it
    /// were written at more than one version. Must be idempotent and gated on the bad value itself.
    /// </summary>
    /// <param name="TextHint">A string every affected document contains, so unaffected ones skip the parse.</param>
    public sealed record Repair(string Name, string TextHint, Func<MappingDataNode, bool> Apply);

    /// <summary>
    /// The drydock format ladder, ordered by <see cref="FormatStep.FromDrydockFormat"/>. Empty: the
    /// only bump so far (1 to 2) changed how the reader interprets sidecar times, not the document,
    /// and <see cref="DrydockFormat"/> records that as a delay rather than a misread. A future bump
    /// that changes the document adds its step here.
    /// </summary>
    public static readonly IReadOnlyList<FormatStep> FormatSteps = Array.Empty<FormatStep>();

    public static DrydockDocumentRebakeResult Transform(string yaml, DrydockMigrationTable table, int drydockFormatVer)
    {
        return Transform(yaml, table, drydockFormatVer, FormatSteps, Repairs);
    }

    /// <summary>
    /// <see cref="Transform(string, DrydockMigrationTable, int)"/> with the ladder and repairs as
    /// parameters, so the ladder can be tested before it has a real step.
    /// </summary>
    /// <param name="drydockFormatVer">The revision's stored drydock format.</param>
    public static DrydockDocumentRebakeResult Transform(
        string yaml,
        DrydockMigrationTable table,
        int drydockFormatVer,
        IReadOnlyList<FormatStep> steps,
        IReadOnlyList<Repair> repairs)
    {
        return Transform(yaml, DrydockSystem.ReadDriftIds(yaml).Ids, table, drydockFormatVer, steps, repairs);
    }

    /// <summary>
    /// The transform over group ids the caller has already read from <paramref name="yaml"/> with
    /// <see cref="DrydockSystem.ReadDriftIds"/>, so a caller that also needs them scans the document once.
    /// </summary>
    internal static DrydockDocumentRebakeResult Transform(
        string yaml,
        SortedSet<string> ids,
        DrydockMigrationTable table,
        int drydockFormatVer,
        IReadOnlyList<FormatStep> steps,
        IReadOnlyList<Repair> repairs)
    {
        var needsRename = false;
        foreach (var id in ids)
        {
            if (RenameFor(table, id) != null)
            {
                needsRename = true;
                break;
            }
        }

        var ladder = StepsFrom(steps, drydockFormatVer);
        var hinted = repairs.Where(r => yaml.Contains(r.TextHint, StringComparison.Ordinal)).ToList();

        if (!needsRename && ladder.Count == 0 && hinted.Count == 0)
            return new DrydockDocumentRebakeResult(yaml, false, Array.Empty<DrydockRename>(), Array.Empty<string>(), drydockFormatVer);

        var root = Parse(yaml);
        var treeChanged = false;
        var applied = new List<string>();
        var formatVer = drydockFormatVer;

        foreach (var step in ladder)
        {
            treeChanged |= step.Apply(root);
            applied.Add(step.Name);
            formatVer = step.FromDrydockFormat + 1;
        }

        var renames = needsRename ? ApplyRenames(root, table) : new List<DrydockRename>();
        treeChanged |= renames.Count > 0;

        foreach (var repair in hinted)
        {
            if (!repair.Apply(root))
                continue;

            treeChanged = true;
            applied.Add(repair.Name);
        }

        // The emitter writes the platform's line ending, so a document stored on Linux and re-baked
        // on Windows would otherwise change every line.
        var output = treeChanged
            ? DrydockSystem.EmitDocument(root, yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n")
            : yaml;
        return new DrydockDocumentRebakeResult(output, treeChanged || formatVer != drydockFormatVer, renames, applied, formatVer);
    }

    /// <summary>
    /// The contiguous run of steps starting at <paramref name="version"/> and stopping below
    /// <see cref="DrydockFormat.Current"/>. A gap stops the run, so a document never claims a version
    /// whose migration it did not get.
    /// </summary>
    private static List<FormatStep> StepsFrom(IReadOnlyList<FormatStep> steps, int version)
    {
        var run = new List<FormatStep>();

        foreach (var step in steps.OrderBy(s => s.FromDrydockFormat))
        {
            if (version >= DrydockFormat.Current)
                break;

            if (step.FromDrydockFormat < version)
                continue;

            if (step.FromDrydockFormat != version)
                break;

            run.Add(step);
            version++;
        }

        return run;
    }

    /// <summary>The id a group should carry instead, or null. Null for an id the loader deletes.</summary>
    private static string? RenameFor(DrydockMigrationTable table, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || table.Deleted.Contains(id))
            return null;

        return table.Renamed.TryGetValue(id, out var target) ? target : null;
    }

    private static MappingDataNode Parse(string yaml)
    {
        using var reader = new StringReader(yaml);
        var documents = DataNodeParser.ParseYamlStream(reader).ToArray();

        if (documents.Length != 1 || documents[0].Root is not MappingDataNode root)
            throw new InvalidDataException($"A stored ship document must hold one mapping, found {documents.Length} document(s).");

        return root;
    }

    /// <summary>
    /// Renames groups in place, then merges any group whose new id another group already carries and
    /// puts the groups and the merged instances back in the order the engine writes them
    /// (<c>EntitySerializer</c>: groups by id, invariant culture; instances by uid).
    /// </summary>
    private static List<DrydockRename> ApplyRenames(MappingDataNode root, DrydockMigrationTable table)
    {
        var applied = new List<DrydockRename>();

        if (!root.TryGet<SequenceDataNode>("entities", out var groups))
            return applied;

        foreach (var node in groups.Sequence)
        {
            if (node is not MappingDataNode group
                || !group.TryGet<ValueDataNode>("proto", out var proto)
                || RenameFor(table, proto.Value) is not { } target)
            {
                continue;
            }

            applied.Add(new DrydockRename(proto.Value, target));
            group["proto"] = new ValueDataNode(target);
        }

        if (applied.Count == 0)
            return applied;

        var targets = applied.Select(r => r.To).ToHashSet();
        var rebuilt = new List<DataNode>(groups.Count);
        var merged = new Dictionary<string, MappingDataNode>();

        foreach (var node in groups.Sequence)
        {
            if (node is not MappingDataNode group
                || !group.TryGet<ValueDataNode>("proto", out var proto)
                || !targets.Contains(proto.Value))
            {
                rebuilt.Add(node);
                continue;
            }

            if (!merged.TryGetValue(proto.Value, out var keeper))
            {
                merged[proto.Value] = group;
                rebuilt.Add(group);
                continue;
            }

            var into = keeper.Get<SequenceDataNode>("entities");
            foreach (var entity in group.Get<SequenceDataNode>("entities").Sequence)
            {
                into.Add(entity);
            }
        }

        foreach (var keeper in merged.Values)
        {
            var entities = keeper.Get<SequenceDataNode>("entities");
            keeper["entities"] = new SequenceDataNode(entities.Sequence.OrderBy(Uid).ToList());
        }

        // OrderBy is stable, so a group without a proto keeps its place among equals.
        var sorted = rebuilt.OrderBy(GroupId, StringComparer.InvariantCulture).ToList();
        root["entities"] = new SequenceDataNode(sorted);

        applied.Sort((a, b) => string.CompareOrdinal(a.From, b.From));
        return applied;

        static string GroupId(DataNode node) =>
            node is MappingDataNode g && g.TryGet<ValueDataNode>("proto", out var p) ? p.Value : string.Empty;

        static int Uid(DataNode node) =>
            node is MappingDataNode e
            && e.TryGet<ValueDataNode>("uid", out var uid)
            && int.TryParse(uid.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : int.MaxValue;
    }

    /// <summary>
    /// Anything above this many seconds in a <c>nextVisualUpdate</c> can only be the old sentinel. The
    /// sentinel was written as <c>TimeSpan.MaxValue</c> less the storing server's clock, about 29,000
    /// years less a few days; half of that range is still around 14,600 years, and a real pending
    /// animation deadline is under a second from the clock it was written against.
    /// </summary>
    internal static readonly double ProximitySentinelSeconds = TimeSpan.MaxValue.TotalSeconds / 2;

    /// <summary>
    /// Drops the <c>TimeSpan.MaxValue</c> sentinel from <c>TriggerOnProximity.nextVisualUpdate</c>.
    /// </summary>
    /// <remarks>
    /// The field is <c>TimeSpan?</c>, null for nothing scheduled. A document written while it was a plain
    /// <c>TimeSpan</c> parked at <c>TimeSpan.MaxValue</c> carries that as a huge offset from
    /// <c>TimeOffsetSerializer</c>: it reads
    /// back into the nullable field as a value (clamped to <c>TimeSpan.MaxValue</c> when the retrieving
    /// clock is later than the storing one), and the generated unpause handler's <c>HasValue</c> guard
    /// lets the add through and overflows. Dropping the key reads the field as its default, null, which
    /// is what the sentinel meant. This is a repair and not a format step because documents at drydock
    /// format 2 carry it too: format 2 landed 2026-09-06 and the field change 2026-09-09.
    /// </remarks>
    public static readonly Repair ProximityVisualSentinel = new(
        "TriggerOnProximity.nextVisualUpdate sentinel",
        "nextVisualUpdate",
        DropProximitySentinel);

    /// <summary>Every repair, run on each document whose text carries the repair's hint. Declared after the repairs it lists: static fields initialize in textual order.</summary>
    public static readonly IReadOnlyList<Repair> Repairs = new[] { ProximityVisualSentinel };

    private static bool DropProximitySentinel(MappingDataNode root)
    {
        if (!root.TryGet<SequenceDataNode>("entities", out var groups))
            return false;

        var changed = false;

        foreach (var group in groups.Sequence.OfType<MappingDataNode>())
        {
            if (!group.TryGet<SequenceDataNode>("entities", out var entities))
                continue;

            foreach (var entity in entities.Sequence.OfType<MappingDataNode>())
            {
                if (!entity.TryGet<SequenceDataNode>("components", out var components))
                    continue;

                foreach (var component in components.Sequence.OfType<MappingDataNode>())
                {
                    if (component.TryGet<ValueDataNode>("type", out var type)
                        && type.Value == "TriggerOnProximity"
                        && component.TryGet<ValueDataNode>("nextVisualUpdate", out var value)
                        && double.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                        && seconds > ProximitySentinelSeconds)
                    {
                        component.Remove("nextVisualUpdate");
                        changed = true;
                    }
                }
            }
        }

        return changed;
    }
}
