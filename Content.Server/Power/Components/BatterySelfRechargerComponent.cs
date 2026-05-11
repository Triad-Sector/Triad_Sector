using System;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom; // Triad - for TimeOffsetSerializer

namespace Content.Server.Power.Components
{
    /// <summary>
    ///     Self-recharging battery.
    /// </summary>
    [RegisterComponent]
    public sealed partial class BatterySelfRechargerComponent : Component
    {
        /// <summary>
        /// Does the entity auto recharge?
        /// </summary>
        [DataField] public bool AutoRecharge;

        /// <summary>
        /// At what rate does the entity automatically recharge?
        /// </summary>
        [DataField] public float AutoRechargeRate;

        /// <summary>
        /// Should this entity stop automatically recharging if a charge is used?
        /// </summary>
        [DataField] public bool AutoRechargePause = false;

        /// <summary>
        /// How long should the entity stop automatically recharging if a charge is used?
        /// </summary>
        [DataField] public float AutoRechargePauseTime = 0f;

        /// <summary>
        /// Do not auto recharge if this timestamp has yet to happen, set for the auto recharge pause system.
        /// </summary>
        [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))] public TimeSpan NextAutoRecharge = TimeSpan.FromSeconds(0f); // Triad - give it TimeOffsetSerializer so that saved maps don't have to worry about the timestamp being in the past when loaded
    }
}
