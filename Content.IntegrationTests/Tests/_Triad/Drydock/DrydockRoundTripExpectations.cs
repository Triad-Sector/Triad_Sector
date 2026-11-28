#nullable enable

using System.Linq;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// What a round trip is allowed to change, shared by the hand-built comparison and the roster
    /// sweep so the two cannot drift into disagreeing about what counts as a fault.
    ///
    /// <para>Every entry names why. An entry without a reason is a bug somebody gave up on rather
    /// than a difference by design, and that is the whole difference between this list and the
    /// hand-maintained tables the drydock exists to replace.</para>
    /// </summary>
    public static class DrydockRoundTripExpectations
    {
        /// <summary>
        /// Differences the retrieve deliberately produces. These are state the ship is supposed to
        /// come back with a new value for, not state it lost.
        /// </summary>
        private static readonly string[] ByDesign =
        {
            // Minted fresh for whoever retrieved the ship, which is the point of a retrieve.
            "ShuttleDeedComponent.",
            "ShipOwnershipComponent.",

            // Station membership is stripped at store and a fresh station is built at retrieve.
            "StationMemberComponent.",

            // The repair baseline is derived state, regenerated on arrival by design.
            "ShipRepair",

            // Stamped on the grid at retrieve so roundstart variation does not re-litter a ship
            // every time it comes back.
            "StationVariationHasRunComponent.",

            // The loader puts this on any grid it reads. It is load bookkeeping, not ship state.
            "MapSaveTileMapComponent.",

            // Sounds and the despawn timers riding them used to be listed here. They are excluded
            // at the walk now, by the serializer's own save: false rule, because a whole entity is
            // reported as one line with no component name in it for a filter like this to match.

            // Generated networking bookkeeping present on every networked component, not ship
            // state. An entity that really went missing still says so through its other fields, so
            // dropping this loses no signal and removes about a third of the noise.
            "._netSync",

            // Both hold the grid's own NetEntity, written as a string. The retrieved hull is a new
            // entity, so these have to differ or the locks would be pointing at the ship that was
            // stored. Re-stamping them on arrival is the fix, not a fault.
            "ShipGridLockComponent.ShuttleId",
            "ShuttleConsoleLockComponent.ShuttleId",

            // Atmos devices leave their monitor at store and rejoin it on arrival, so the roster is
            // rebuilt by design. The engine's own rejoin guard is what makes that safe.
            "AtmosMonitorComponent.RegisteredDevices",

            // Slot contents are entity references, remapped by the loader like every other one. The
            // items themselves are visited in their own right by the walk, so nothing is unwatched.
            "ItemSlotsComponent.Slots",

            // Rolled fresh when the component starts. A stored drink coming back with the same
            // fizziness roll would mean the roll was not random, which is the opposite of the bug.
            "PressurizedSolutionComponent.SprayFizzinessThresholdRoll",

            // A transient counter for the shake played when a grid gains gravity, which is exactly
            // what a hull does on arriving at a berth.
            "GravityShakeComponent.ShakeTimes",

            // The hull comes back locked to whoever retrieved it, which is the same ownership stamp
            // the deed and the ownership component get. Observed as False -> True on the grid.
            "ShipGridLockComponent.Locked",
        };

        /// <summary>
        /// Values that advance with the clock rather than with the round trip, so two snapshots
        /// differ by however many ticks passed between them however well the drydock behaves.
        /// Whether a battery comes back at roughly the charge it left with is a question for a test
        /// that can assert a tolerance; the comparison only handles what should be identical.
        /// </summary>
        private static readonly string[] WithTheClock =
        {
            "BatteryComponent.CurrentCharge",
            "PowerNetworkBatteryComponent.SupplyRampPosition",
            "SpreaderGridComponent.UpdateAccumulator",

            // The powernet rebuild, which is the largest single source of difference in the fleet
            // sweep and is a property of the two snapshots rather than of the round trip. A hull
            // loaded from its map file starts at whatever charge the mapper left in it; a retrieved
            // one comes back holding the charge it was stored with, because preserving that is the
            // point. So at the same number of ticks the two are at different stages of the same
            // rebuild, and every device downstream of power reports it: the hum, the bulb, the bolt
            // lights, the thruster plume, the gravity the generator has or has not restored yet.
            //
            // This is an exemption, not a fix, and it is worth being plain about what it can hide: a
            // machine that genuinely comes back unpowered forever looks the same here as one that is
            // half a second behind. Settling both sides to steady state instead would be the real
            // answer, and it is not free, because the extra ticks give FirelockSystem more chances to
            // ask a torn-down map for its atmosphere, which is a known flake in this sweep.
            "GravityComponent.Enabled",
            "AmbientSoundComponent.Enabled",
            "PointLightComponent.",
            "PowerChargeComponent.Charge",
            "PowerNetworkBatteryComponent.CurrentSupply",
            "PowerNetworkBatteryComponent.CurrentReceiving",
            "PowerNetworkBatteryComponent.LoadingNetworkDemand",
            "PowerSupplierComponent.CurrentSupply",
            "PowerSupplierComponent.SupplyRampPosition",
            "PowerSupplierComponent.MaxSupply",
            "ApcPowerReceiverComponent.Load",
            "AirlockComponent.Powered",
            "DoorBoltComponent.Powered",
            "FirelockComponent.Powered",
            "DamageOnInteractComponent.IsDamageActive",

            // NOT in this list, on purpose: FuelGeneratorComponent.On and PowerSupplierComponent.Enabled.
            // Both sat here for one run and hid a real defect: True -> False on 73 generators, which is
            // a hull coming back dark, not a rebuild settling. Whether a generator is switched on is
            // state the round trip must preserve exactly; only what it produces advances with the clock.
            "Appearance.PowerDeviceVisuals.",
            "Appearance.PoweredLightVisuals.",
            "Appearance.PowerChargeVisuals.",
            "Appearance.ThrusterVisualState.",
            "Appearance.DoorVisuals.BoltLights",
            "Appearance.ApcVisuals.",
            "Appearance.ComputerVisuals.Powered",
            "Appearance.SmesVisuals.",
        };

        public static bool IsUnexpected(string diffLine)
        {
            return !ByDesign.Any(diffLine.Contains) && !WithTheClock.Any(diffLine.Contains);
        }
    }
}
