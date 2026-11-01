#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared._Triad.Drydock;
using Content.Shared._Triad.ShipSize;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The <c>drydockVesselClass</c> table is what a purchase quotes and charges the berth from, and
    /// it is written by hand from the grid files, so this is what keeps it honest: every vessel a
    /// player can buy, and every vessel the table names, is loaded from its shuttle file the way the
    /// shipyard loads it and measured by the same class rule the drydock stores by.
    ///
    /// <para>A disagreement is a hull whose berth is priced for the wrong class, and a hull sold
    /// into a berth smaller than itself cannot be stored in it. So every failure prints the whole
    /// corrected block, ready to paste into vessel_berth_classes.yml, rather than one line per hull
    /// to be converted by hand.</para>
    /// </summary>
    [TestFixture]
    [TestOf(typeof(DrydockVesselClassPrototype))]
    public sealed class DrydockVesselClassTableTest
    {
        /// <summary>
        /// A wall-clock ceiling on the whole sweep. Each load and measurement is synchronous inside its
        /// own post, so nothing here waits on ticks; this only stops a slow roster from holding the
        /// pool until the harness's own timeout.
        /// </summary>
        private static readonly TimeSpan SweepCeiling = TimeSpan.FromMinutes(10);

        [Test]
        public async Task EveryVesselsTableClassMatchesItsGrid()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapLoader = server.System<MapLoaderSystem>();
            var mapSys = server.System<SharedMapSystem>();
            var sizes = server.System<ShipSizeSystem>();

            var vessels = protoMan.EnumeratePrototypes<VesselPrototype>()
                .Where(v => !v.Abstract)
                .ToDictionary(v => v.ID);

            var table = protoMan.EnumeratePrototypes<DrydockVesselClassPrototype>()
                .ToDictionary(r => r.ID, r => r.Class);

            // The purchasable roster, and anything the table names on top of it.
            var toCheck = vessels.Values
                .Where(v => v.Purchasable)
                .Select(v => v.ID)
                .Union(table.Keys)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            Assert.That(toCheck.Count(id => vessels.TryGetValue(id, out var v) && v.Purchasable), Is.GreaterThan(0),
                "The control: an empty purchasable roster would make this sweep vacuous.");
            Assert.That(table, Is.Not.Empty,
                "The control on the table: no drydockVesselClass rows loaded, so every vessel would read as missing for the wrong reason.");

            var failures = new List<string>();
            var measuredById = new Dictionary<string, (ShipSizeClass Class, int Tiles)>();
            var clock = Stopwatch.StartNew();

            foreach (var id in toCheck)
            {
                if (clock.Elapsed > SweepCeiling)
                {
                    failures.Add($"The sweep ran past {SweepCeiling.TotalMinutes} minutes and stopped before {id}; the vessels from here on were not measured.");
                    break;
                }

                if (!vessels.TryGetValue(id, out var vessel))
                {
                    failures.Add($"{id}: the table has a row for it, but no vessel prototype of that id exists. Delete the row.");
                    continue;
                }

                string? loadFailure = null;
                await server.WaitPost(() =>
                {
                    mapSys.CreateMap(out var mapId);
                    try
                    {
                        // The shipyard's own load: TryLoadGrid of the vessel's shuttle file onto a map.
                        if (!mapLoader.TryLoadGrid(mapId, vessel.ShuttlePath, out var grid))
                        {
                            loadFailure = $"{id}: its shuttle file at {vessel.ShuttlePath} would not load as a single grid.";
                            return;
                        }

                        measuredById[id] = (sizes.GetSizeClass(grid.Value), sizes.GetBuiltTileCount(grid.Value));
                    }
                    finally
                    {
                        mapSys.DeleteMap(mapId);
                    }
                });

                if (loadFailure != null)
                    failures.Add(loadFailure);
            }

            var mismatched = new List<string>();
            var missing = new List<string>();
            foreach (var (id, measured) in measuredById)
            {
                if (!table.TryGetValue(id, out var listed))
                    missing.Add($"{id}: no row; its grid has {measured.Tiles} filled tiles, a {measured.Class}.");
                else if (listed != measured.Class)
                    mismatched.Add($"{id}: the table says {listed}, its grid has {measured.Tiles} filled tiles, a {measured.Class}.");
            }

            if (mismatched.Count > 0 || missing.Count > 0)
            {
                var corrected = new StringBuilder();
                corrected.AppendLine("Corrected rows for Resources/Prototypes/_Triad/Drydock/vessel_berth_classes.yml:");
                foreach (var (id, measured) in measuredById
                             .Where(m => !table.TryGetValue(m.Key, out var listed) || listed != m.Value.Class)
                             .OrderBy(m => m.Key, StringComparer.Ordinal))
                {
                    corrected.AppendLine();
                    corrected.AppendLine("- type: drydockVesselClass");
                    corrected.AppendLine($"  id: {id}");
                    corrected.AppendLine($"  class: {measured.Class}");
                }

                await TestContext.Out.WriteLineAsync(corrected.ToString());

                failures.AddRange(mismatched);
                failures.AddRange(missing);
                failures.Add(corrected.ToString());
            }

            await TestContext.Out.WriteLineAsync(
                $"[vessel-class-table] measured {measuredById.Count} of {toCheck.Count} vessel(s) against {table.Count} table row(s) in {clock.Elapsed.TotalSeconds:F1}s.");

            Assert.That(failures, Is.Empty,
                $"{mismatched.Count} vessel(s) disagree with the drydock class table and {missing.Count} have no row:"
                + Environment.NewLine + string.Join(Environment.NewLine, failures));

            await pair.CleanReturnAsync();
        }
    }
}
