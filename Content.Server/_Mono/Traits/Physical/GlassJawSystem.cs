using Content.Shared._Mono.Traits.Physical;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;

namespace Content.Server._Mono.Traits.Physical;

/// <summary>
/// Applies the Glass Jaw trait effects by adjusting the critical health threshold.
/// </summary>
public sealed class GlassJawSystem : EntitySystem
{
    [Dependency] private readonly MobThresholdSystem _mobThresholds = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GlassJawComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<GlassJawComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnStartup(Entity<GlassJawComponent> ent, ref ComponentStartup args)
    {
        AdjustCritThreshold(ent.Owner, -ent.Comp.CritDecrease, ent.Comp.CritSetValueFallback);
    }

    private void OnShutdown(Entity<GlassJawComponent> ent, ref ComponentShutdown args)
    {
        AdjustCritThreshold(ent.Owner, ent.Comp.CritDecrease);
    }

    private void AdjustCritThreshold(EntityUid uid, int deltaPoints, int? setValue = null, MobThresholdsComponent? thresholdsComp = null)
    {
        var newValue = FixedPoint2.Zero;

        if (!_mobThresholds.TryGetThresholdForState(uid, MobState.Critical, out var current, thresholdsComp))
        {
            if (setValue == null)
                return;

            newValue = FixedPoint2.Max(0, (FixedPoint2)setValue);
        }
        else
        {
            newValue = FixedPoint2.Max(0, current.Value + (FixedPoint2)deltaPoints);
        }

        _mobThresholds.SetMobStateThreshold(uid, newValue, MobState.Critical, thresholdsComp);
    }
}
