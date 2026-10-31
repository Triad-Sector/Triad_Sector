using Content.Server.Store.Systems;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom; // Triad - for TimeOffsetSerializer

namespace Content.Server.Store.Components;

// TODO: Refund on a per-item/action level.
//   Requires a refund button next to each purchase (disabled/invis by default)
//   Interactions with ActionUpgrades would need to be modified to reset all upgrade progress and return the original action purchase to the store.

/// <summary>
///     Keeps track of entities bought from stores for refunds, especially useful if entities get deleted before they can be refunded.
/// </summary>
[RegisterComponent, Access(typeof(StoreSystem))]
public sealed partial class StoreRefundComponent : Component
{
    /// <summary>
    ///     The store this entity was bought from
    /// </summary>
    [DataField]
    public EntityUid? StoreEntity;

    /// <summary>
    ///     The time this entity was bought
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan? BoughtTime; // Triad - TimeOffsetSerializer: absolute game time, re-based on load so a saved ship does not carry the previous server clock

    /// <summary>
    ///     How long until this entity disables refund purchase?
    /// </summary>
    [DataField]
    public TimeSpan DisableTime = TimeSpan.FromSeconds(300);
}
