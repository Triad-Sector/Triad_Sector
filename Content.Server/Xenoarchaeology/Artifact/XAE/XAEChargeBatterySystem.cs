using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Xenoarchaeology.Artifact.XAE.Components;
using Content.Shared.Xenoarchaeology.Artifact;
using Content.Shared.Xenoarchaeology.Artifact.XAE;

namespace Content.Server.Xenoarchaeology.Artifact.XAE;

/// <summary>
/// System for xeno artifact activation effect that is fully charging batteries in certain range.
/// </summary>
public sealed class XAEChargeBatterySystem : BaseXAESystem<XAEChargeBatteryComponent>
{
    [Dependency] private readonly BatterySystem _battery = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;

    /// <summary> Pre-allocated and re-used collection.</summary>
    private readonly HashSet<Entity<BatteryComponent>> _batteryEntities = new();

    /// <inheritdoc />
    protected override void OnActivated(Entity<XAEChargeBatteryComponent> ent, ref XenoArtifactNodeActivatedEvent args)
    {
        var chargeBatteryComponent = ent.Comp;
        _batteryEntities.Clear();
        _lookup.GetEntitiesInRange(args.Coordinates, chargeBatteryComponent.Radius, _batteryEntities);

        var xform = Transform(ent.Owner); // Triad: the node inherits the artifact's grid
        foreach (var battery in _batteryEntities)
        {
            if (!XenoArtifact.IsGridLocal(xform, battery)) // Triad: no charging the ship next door
                continue;

            _battery.SetCharge(battery, battery.Comp.MaxCharge, battery);
        }
    }
}
