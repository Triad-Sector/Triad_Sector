#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// THE DENOMINATOR: walks every registered component's [DataField]s and finds every
    /// field whose type the engine YAML serializer cannot write — the complete, authoritative
    /// set of state the drydock must capture/strip/rehydrate itself. Static and exhaustive:
    /// no ship-by-ship discovery, no waiting for a populated grid to trip it. The empirical
    /// roster sweep test (ported with the drydock systems) is the cross-check on this predictor.
    ///
    /// It is a GATE, not a report: the result is asserted against <see cref="ExpectedLeaves"/>,
    /// so a merge that adds or removes a serializer path fails here and forces a decision,
    /// instead of quietly changing what a stored ship keeps.
    ///
    /// Writability models the engine's own write dispatch (SerializationManager.Writing):
    /// primitives / string / enum / ISelfSerialize / arrays / generic collections
    /// (recurse the args — a Dictionary&lt;Type,object&gt; HAS a serializer but its args
    /// don't) / registered ITypeWriter types / [DataDefinition] (recurse its fields).
    /// Fields carrying a customTypeSerializer are handled by that serializer, so they're skipped.
    /// </summary>
    [TestFixture]
    public sealed class DrydockSerializabilityAuditTest
    {
        [Test]
        public async Task AuditUnserializableComponentFields()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var compFactory = server.ResolveDependency<IComponentFactory>();
            var serialization = server.ResolveDependency<ISerializationManager>();

            var serializerTypes = CollectRegisteredWriterTypes();

            // STEP 0 (control), before anything is measured: prove both stages still detect a
            // gap they cannot miss. The empirical probe reads the engine's own exception wording,
            // which has already drifted once (287 throws through two doors). A silently broken
            // probe reports zero gaps, which reads as "everything serializes" — the worst
            // available false negative for a system whose whole job is knowing what does not
            // survive a write.
            var controlLeaf = FindUnwritableLeaf(typeof(AuditControlUnwritable), serializerTypes, new HashSet<Type>());
            Assert.That(controlLeaf, Is.EqualTo(typeof(AuditControlUnwritable)),
                "Static predictor failed its control: a type with no serializer and no data definition was not flagged.");

            var controlConfirmed = false;
            await server.WaitPost(() => controlConfirmed = IsConfirmedUnwritable(serialization, typeof(AuditControlUnwritable)));
            Assert.That(controlConfirmed, Is.True,
                "Empirical probe failed its control: the live serializer did not report a gap for a type it cannot possibly "
                + "write. The engine's exception type or wording has drifted. Fix "
                + "DrydockSerializationGap.IsNoCoverage before trusting any result this test produces, and note that "
                + "the fidelity layer's store path is reading the same broken classification right now.");

            // STEP 1 (static): the predictor's candidate leaves. Over-reports — it can't
            // see every path Robust makes a type writable (implicit-data-def-for-inheritors,
            // polymorphic/abstract serializers), so it must be filtered empirically.
            var candidates = new Dictionary<Type, List<string>>();
            foreach (var compType in compFactory.AllRegisteredTypes)
            {
                foreach (var (memberName, memberType) in DataFields(compType))
                {
                    var leaf = FindUnwritableLeaf(memberType, serializerTypes, new HashSet<Type>());
                    if (leaf == null)
                        continue;
                    if (!candidates.TryGetValue(leaf, out var list))
                        candidates[leaf] = list = new List<string>();
                    list.Add($"{compType.Name}.{memberName}");
                }
            }

            // STEP 2 (empirical): confirm each candidate by actually asking the live
            // serializer to write an instance. Only a real "No data definition found" throw
            // is a true gap. Abstract/interface leaves are polymorphic-serializable (they're
            // abstract *because* the hierarchy has a serializer), so they drop out.
            var byLeaf = new Dictionary<Type, List<string>>();
            await server.WaitPost(() =>
            {
                foreach (var (leaf, sites) in candidates)
                {
                    if (IsConfirmedUnwritable(serialization, leaf))
                        byLeaf[leaf] = sites;
                }
            });

            TestContext.Out.WriteLine($"[serializability-audit] {candidates.Count} static candidate leaves -> {byLeaf.Count} EMPIRICALLY CONFIRMED unserializable.");

            var lines = new List<string> { $"{byLeaf.Count} unserializable leaf type(s) across component [DataField]s:" };
            foreach (var (leaf, sites) in byLeaf.OrderByDescending(kv => kv.Value.Count))
            {
                lines.Add($"{leaf.FullName}  ({sites.Count} field(s))");
                lines.Add($"    {string.Join(", ", sites.OrderBy(s => s))}");
            }
            var outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ship_serializability_audit.txt");
            System.IO.File.WriteAllLines(outPath, lines);
            foreach (var l in lines)
                TestContext.Out.WriteLine($"[serializability-audit] {l}");

            var confirmed = byLeaf.Keys.ToDictionary(StableName, t => byLeaf[t]);
            var candidateCount = candidates.Count;

            await pair.CleanReturnAsync();

            // Discrimination control: the empirical stage exists to clear static candidates the
            // predictor cannot know are writable. If it ever confirms every one of them, it has
            // stopped filtering and the set below is meaningless even when it happens to match.
            Assert.That(confirmed, Has.Count.LessThan(candidateCount),
                "Empirical probe confirmed every static candidate; it is no longer discriminating.");

            var appeared = confirmed.Keys.Where(n => !ExpectedLeaves.ContainsKey(n)).OrderBy(n => n).ToList();
            var vanished = ExpectedLeaves.Keys.Where(n => !confirmed.ContainsKey(n)).OrderBy(n => n).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(appeared, Is.Empty,
                    "A component [DataField] now bottoms out in a type the serializer cannot write, and nobody has decided "
                    + "what the drydock does with it. Read the type, decide capture or strip, then record the verdict on the "
                    + "Drydock State Fidelity Design wiki page, then add it to StrippedLeaves here or to "
                    + "DrydockSerializationGap.CapturedTypes. Field sites: "
                    + string.Join("; ", appeared.Select(n => $"{n} -> {string.Join(", ", confirmed[n])}")));

                Assert.That(vanished, Is.Empty,
                    "A type the drydock plans around now serializes natively. If it was on the capture list, capturing it by "
                    + "hand is now redundant work that will drift from the engine's own output. Re-read it and update both "
                    + "the list below and the Drydock State Fidelity Design wiki page.");
            });
        }

        /// <summary>
        /// The audit's locked verdict, one entry per confirmed unserializable leaf type, keyed by
        /// <see cref="StableName"/>. Established 2026-08-25 against engine 287; the reasoning for
        /// every entry is on the Drydock State Fidelity Design wiki page and belongs there, not here.
        ///
        /// Field counts are deliberately NOT gated. Upstream adds and removes fields of these types
        /// constantly and it changes nothing: the verdict is per type, so a new
        /// <c>EntityCoordinates</c> field inherits the existing strip decision. What must never
        /// change silently is the set of types itself.
        ///
        /// The <see cref="Verdict.Capture"/> entries are the fidelity layer's capture manifest.
        /// When that manifest lands as a real constant, assert it equals this set rather than
        /// maintaining the two by hand.
        /// </summary>
        private static readonly Dictionary<string, Verdict> ExpectedLeaves = BuildExpectedLeaves();

        /// <summary>
        /// The capture half comes from <see cref="DrydockSerializationGap.CapturedTypes"/> rather
        /// than being restated here, so the store path and this gate cannot disagree about what is
        /// captured. The strip half is the audit's own record: those types have no runtime
        /// representation at all, since stripping is what happens when nothing captures them.
        /// </summary>
        private static Dictionary<string, Verdict> BuildExpectedLeaves()
        {
            var expected = new Dictionary<string, Verdict>();

            foreach (var captured in DrydockSerializationGap.CapturedTypes)
                expected[StableName(captured)] = Verdict.Capture;

            foreach (var stripped in StrippedLeaves)
            {
                // A type cannot be both, and a merge that lands it in both lists would otherwise
                // resolve silently to whichever ran last.
                Assert.That(expected.ContainsKey(stripped), Is.False,
                    $"{stripped} is on the capture manifest and on the strip list at once.");
                expected[stripped] = Verdict.Strip;
            }

            return expected;
        }

        // A property, not a static field: a static field is initialized in textual order, so
        // ExpectedLeaves would read it as null from above. Evaluating on access removes the
        // hazard instead of relying on these two staying in the right order forever.
        private static IReadOnlyList<string> StrippedLeaves => new[]
        {
            // Round-scoped positions of things in motion.
            "Robust.Shared.Map.EntityCoordinates",
            "Robust.Shared.Map.MapCoordinates",

            // console and playback state that repopulates or simply stops.
            "Content.Shared.StationRecords.StationRecordsFilter",
            "Content.Shared.Instruments.MidiTrack",
            "Content.Shared._Triad.ContrabandPermit.ContrabandPermitConsoleEntry",

            // mob-scoped state on occupants the store evicts.
            "Content.Shared._Common.Consent.PlayerConsentSettings",
            "Content.Shared.Alert.AlertKey",

            // sector-level records that live off-grid and never ride a blob.
            "Content.Shared._NF.BountyContracts.BountyContract",
            "Content.Shared._NF.ShuttleRecords.ShuttleRecord",
            "Content.Shared.MassMedia.Systems.NewsArticle",

            // dead across rounds whether captured or not, or regenerated per tick.
            "Content.Shared.StationRecords.StationRecordKey",
            "System.Collections.Generic.Dictionary+Enumerator<Robust.Shared.GameObjects.EntityUid,Content.Shared.Climbing.Components.BonkableComponent>",
            "System.ValueTuple<System.Single,System.Numerics.Vector2,System.Single>",
        };

        private enum Verdict
        {
            /// <summary>The fidelity layer serializes and restores this itself.</summary>
            Capture,

            /// <summary>Dropped on store, by decision, with the reasoning recorded on the wiki.</summary>
            Strip,
        }

        /// <summary>
        /// Assembly-version-free type identity. <see cref="Type.FullName"/> embeds the assembly
        /// version of every generic argument, so keying on it would churn this list on each engine
        /// bump for reasons that have nothing to do with serializability.
        /// </summary>
        private static string StableName(Type type)
        {
            if (type.IsGenericParameter)
                return type.Name;
            if (type.IsArray)
                return StableName(type.GetElementType()!) + "[]";

            var name = Unticked(type.Name);
            for (var declaring = type.DeclaringType; declaring != null; declaring = declaring.DeclaringType)
                name = Unticked(declaring.Name) + "+" + name;
            if (!string.IsNullOrEmpty(type.Namespace))
                name = type.Namespace + "." + name;
            if (type.IsGenericType)
                name += "<" + string.Join(",", type.GetGenericArguments().Select(StableName)) + ">";

            return name;
        }

        private static string Unticked(string name)
        {
            var tick = name.IndexOf('`');
            return tick < 0 ? name : name[..tick];
        }

        /// <summary>
        /// The control the probe must trip: no serializer, no data definition, no path to write it.
        /// Declared here rather than found in content so the control never depends on content
        /// staying broken in some particular way.
        /// </summary>
        private sealed class AuditControlUnwritable
        {
            public int Value = 1;
        }

        // Ground truth: does the LIVE serializer actually fail to write this type? Only a
        // "No data definition found" throw counts (an NRE on an uninitialized instance is a
        // bad sample, not a serializability gap). Uninstantiable/abstract -> not confirmable
        // -> assume serializable (polymorphic).
        private static bool IsConfirmedUnwritable(ISerializationManager serialization, Type type)
        {
            if (type.IsAbstract || type.IsInterface)
                return false;

            object instance;
            try { instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type); }
            catch { return false; }

            try
            {
                serialization.WriteValue(type, instance, alwaysWrite: true);
                return false; // wrote fine -> serializable.
            }
            catch (Exception e)
            {
                // The classification lives with the store path, not here. Sharing it is the point:
                // this test's control is what proves the classification still works, and that proof
                // is only worth something while the fidelity layer asks the same code.
                return DrydockSerializationGap.IsNoCoverage(e);
            }
        }

        // All [DataField]/[DataDefinition] members on a type, excluding those whose attribute
        // names a customTypeSerializer (that serializer makes an otherwise-unwritable type fine).
        private static IEnumerable<(string, Type)> DataFields(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var m in t.GetFields(flags).Cast<MemberInfo>().Concat(t.GetProperties(flags)))
                {
                    var attr = m.GetCustomAttribute<DataFieldBaseAttribute>();
                    if (attr == null || attr.CustomTypeSerializer != null)
                        continue;
                    var mt = m is FieldInfo fi ? fi.FieldType : ((PropertyInfo)m).PropertyType;
                    yield return (m.Name, mt);
                }
            }
        }

        // Returns the specific leaf type that can't be written, or null if the whole type writes.
        private static Type? FindUnwritableLeaf(Type type, HashSet<Type> writerTypes, HashSet<Type> seen)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (!seen.Add(type))
                return null; // cycle: assume fine.

            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type.IsEnum)
                return null;
            if (typeof(ISelfSerialize).IsAssignableFrom(type))
                return null;

            if (type.IsArray)
                return FindUnwritableLeaf(type.GetElementType()!, writerTypes, seen);

            // Generic collection: the engine collection serializer recurses into the args, so
            // its writability is the AND of its args — regardless of the dict/list serializer.
            if (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type))
            {
                foreach (var arg in type.GetGenericArguments())
                {
                    var leaf = FindUnwritableLeaf(arg, writerTypes, seen);
                    if (leaf != null)
                        return leaf;
                }
                return null;
            }

            if (HasWriter(type, writerTypes))
                return null;

            if (type.GetCustomAttribute<DataDefinitionAttribute>() != null)
            {
                foreach (var (_, memberType) in DataFields(type))
                {
                    var leaf = FindUnwritableLeaf(memberType, writerTypes, seen);
                    if (leaf != null)
                        return leaf;
                }
                return null;
            }

            return type; // no path to write this type.
        }

        private static bool HasWriter(Type type, HashSet<Type> writerTypes)
        {
            if (writerTypes.Contains(type))
                return true;
            return type.IsGenericType && writerTypes.Contains(type.GetGenericTypeDefinition());
        }

        // Every type T for which some class implements ITypeWriter<T> (closed types + open
        // generic definitions like Dictionary<,>).
        private static HashSet<Type> CollectRegisteredWriterTypes()
        {
            var result = new HashSet<Type>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }

                foreach (var type in types)
                {
                    if (type is null || type.IsAbstract || type.IsInterface)
                        continue;
                    foreach (var iface in type.GetInterfaces())
                    {
                        if (!iface.IsGenericType || iface.GetGenericTypeDefinition() != typeof(ITypeWriter<>))
                            continue;
                        var target = iface.GetGenericArguments()[0];
                        result.Add(target.IsGenericType ? target.GetGenericTypeDefinition() : target);
                    }
                }
            }
            return result;
        }
    }
}
