#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Triad
{
    /// <summary>
    /// A gate on visuals seeded in a <see cref="MapInitEvent"/> handler and nowhere else, which are
    /// therefore absent on every load path.
    ///
    /// <para>A map created with <c>runMapInit: false</c> gives component startup without map init,
    /// which is what a load looks like. Deliberately no drydock: the appearance carrier would hide
    /// the class this asserts. Presence is the assertion, not value - the defect is an absent key,
    /// which visualizers read as "nothing to do".</para>
    /// </summary>
    /// <remarks>
    /// Keyed by component rather than prototype id, so a rename cannot quietly empty a row.
    /// </remarks>
    [TestFixture]
    public sealed class AppearanceOnLoadTest
    {
        /// <summary>
        /// Component to the appearance keys its system must seed on a load, as <c>EnumType.Member</c>.
        /// </summary>
        private static readonly (string Component, string[] Keys)[] Expected =
        {
            ("PoweredLight", new[] { "PoweredLightVisuals.BulbState" }),
            ("AtmosAlarmable", new[] { "AtmosMonitorVisuals.AlarmType" }),
            ("BasicEntityAmmoProvider", new[] { "AmmoVisuals.HasAmmo", "AmmoVisuals.AmmoCount", "AmmoVisuals.AmmoMax" }),
            ("MagazineAmmoProvider", new[] { "AmmoVisuals.MagLoaded" }),
            ("PowerCellSlot", new[] { "PowerCellSlotVisuals.Enabled" }),
            ("VendingMachine", new[] { "VendingMachineVisuals.VisualState" }),
            ("Intercom", new[] { "RadioDeviceVisuals.Speaker", "RadioDeviceVisuals.Broadcasting" }),
            ("Smes", new[] { "SmesVisuals.LastChargeLevel", "SmesVisuals.LastChargeState" }),
            ("PowerCharge", new[] { "PowerChargeVisuals.Charge", "PowerChargeVisuals.Active" }),
            ("GasOutletInjector", new[] { "OutletInjectorVisuals.Enabled" }),
            ("Toilet", new[] { "ToiletVisuals.SeatVisualState" }),
            ("Conveyor", new[] { "ConveyorVisuals.State" }),
            ("AmeController", new[] { "AmeControllerVisuals.DisplayState" }),
            ("ReagentGrinder", new[] { "ReagentGrinderVisualState.BeakerAttached" }),
            ("Charger", new[] { "CellVisual.Light" }),
            ("RadiationCollector", new[] { "RadiationCollectorVisuals.TankInserted" }),
            ("AtmosPlaque", new[] { "AtmosPlaqueVisuals.State" }),
            ("EnergySword", new[] { "ToggleableVisuals.Color" }),
        };

        [Test]
        public async Task AStartupWithoutAMapInitStillSeedsAppearance()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapSys = server.System<SharedMapSystem>();

            MapId loaded = default;
            MapId spawned = default;
            EntityUid loadedUid = default;

            await server.WaitPost(() =>
            {
                loadedUid = mapSys.CreateMap(out loaded, runMapInit: false);
                mapSys.CreateMap(out spawned);
            });

            // The control on the whole method. If this map were initialized, every row below would
            // pass by way of the map-init handler the test exists to stop relying on.
            Assert.That(mapSys.IsInitialized(loadedUid), Is.False,
                "The load side has to be an uninitialized map, or this test asserts nothing.");

            var missingOnLoad = new List<string>();
            var staleRows = new List<string>();
            var covered = 0;

            foreach (var (component, keys) in Expected)
            {
                var proto = protoMan.EnumeratePrototypes<EntityPrototype>()
                    .Where(p => !p.Abstract
                                && p.Components.ContainsKey(component)
                                && p.Components.ContainsKey("Appearance"))
                    .OrderBy(p => p.ID)
                    .FirstOrDefault();

                if (proto == null)
                {
                    staleRows.Add($"{component}: no prototype carries it with an Appearance, so this row measures nothing.");
                    continue;
                }

                var onLoad = await KeysAfterSpawn(pair, proto.ID, loaded);
                var onSpawn = await KeysAfterSpawn(pair, proto.ID, spawned);

                foreach (var key in keys)
                {
                    // Reported apart from a real miss: a key absent from BOTH sides is a stale
                    // expectation or a moved prototype, not a system that forgot to seed on load.
                    if (!onSpawn.Contains(key))
                    {
                        staleRows.Add($"{component} ({proto.ID}): {key} is absent even after a map init.");
                        continue;
                    }

                    covered++;
                    if (!onLoad.Contains(key))
                        missingOnLoad.Add($"{component} ({proto.ID}): {key}");
                }
            }

            Assert.Multiple(() =>
            {
                Assert.That(staleRows, Is.Empty,
                    "Rows that measure nothing:" + Environment.NewLine + string.Join(Environment.NewLine, staleRows));

                Assert.That(missingOnLoad, Is.Empty,
                    $"{missingOnLoad.Count} appearance key(s) are seeded only by a map init, so they are "
                    + "absent on every load path - the drydock, the ship save, and anything else that "
                    + "starts an entity up without map-initializing it:"
                    + Environment.NewLine + string.Join(Environment.NewLine, missingOnLoad));

                Assert.That(covered, Is.GreaterThan(0), "The control: an empty walk proves nothing.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Spawns one prototype and reads back its live appearance keys as <c>EnumType.Member</c>.
        /// The component state is the supported read: the dictionary is engine-internal and its
        /// component is access-locked to the appearance system.
        /// </summary>
        private static async Task<HashSet<string>> KeysAfterSpawn(Pair.TestPair pair, string proto, MapId map)
        {
            var server = pair.Server;
            var entMan = server.EntMan;
            var keys = new HashSet<string>();

            await server.WaitPost(() =>
            {
                var uid = entMan.SpawnEntity(proto, new MapCoordinates(0, 0, map));

                if (entMan.TryGetComponent(uid, out AppearanceComponent? appearance)
                    && entMan.GetComponentState(entMan.EventBus, appearance, null, GameTick.Zero)
                        is AppearanceComponentState state)
                {
                    foreach (var key in state.Data.Keys)
                        keys.Add($"{key.GetType().Name}.{key}");
                }

                entMan.DeleteEntity(uid);
            });

            return keys;
        }
    }
}
