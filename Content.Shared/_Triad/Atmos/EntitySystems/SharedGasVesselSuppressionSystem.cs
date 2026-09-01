using Content.Shared.Examine;
using Content.Shared._Triad.Atmos.Components;

namespace Content.Shared._Triad.Atmos.EntitySystems;

/// <summary>
/// Neuters gas-vessel bombs. A tank or canister whose contents approach plasma ignition temperature while holding
/// flammables has those contents converted to room-temperature water vapour, its valves seized, and any docked tank
/// fused into the canister. Vessels that fail anyway (fragmentation, destruction while charged) foam over into a
/// solid metal-foam block via <see cref="FoamOver"/> instead of exploding or venting, consuming their contents.
/// </summary>
public abstract partial class SharedGasVesselSuppressionSystem : EntitySystem
{
    [SubscribeLocalEvent]
    private void OnExamined(Entity<SafeCanLabelComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        args.PushMarkup(Loc.GetString(ent.Comp.Label), ent.Comp.ExaminePriority);
    }
}
