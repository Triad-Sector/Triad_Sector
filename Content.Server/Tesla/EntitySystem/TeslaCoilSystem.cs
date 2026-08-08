using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Tesla.Components;
using Content.Server.Lightning;
using Content.Shared.Power; // Triad

namespace Content.Server.Tesla.EntitySystems;

/// <summary>
/// Generates electricity from lightning bolts
/// </summary>
public sealed partial class TeslaCoilSystem : EntitySystem
{
    [Dependency] private BatterySystem _battery = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!; // Triad

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TeslaCoilComponent, HitByLightningEvent>(OnHitByLightning);
        SubscribeLocalEvent<TeslaCoilComponent, LightningStrikeAttemptEvent>(OnLightningStrikeAttempt); // Triad
        SubscribeLocalEvent<TeslaCoilComponent, ChargeChangedEvent>(OnChargeChanged); // Triad
    }

    //When struck by lightning, charge the internal battery
    private void OnHitByLightning(Entity<TeslaCoilComponent> coil, ref HitByLightningEvent args)
    {
        if (TryComp<BatteryComponent>(coil, out var batteryComponent))
        {
            _battery.SetCharge(coil, batteryComponent.CurrentCharge + coil.Comp.ChargeFromLightning);
        }
    }

    // Triad: bid for the strike by charge headroom. An empty coil is a guaranteed catch and outbids
    // every static target; a full coil floors at SaturatedHitProbability so bolts fall through to
    // the grounding rods. At half charge the effective chance is ~0.52, close to the old static 0.5.
    private void OnLightningStrikeAttempt(Entity<TeslaCoilComponent> coil, ref LightningStrikeAttemptEvent args)
    {
        if (!TryComp<BatteryComponent>(coil, out var battery) || battery.MaxCharge <= 0f)
            return;

        if (battery.CurrentCharge <= 0f)
        {
            args.Priority += coil.Comp.EmptyPriorityBonus;
            args.HitProbability = 1f;
            return;
        }

        var headroom = 1f - battery.CurrentCharge / battery.MaxCharge;
        args.HitProbability = MathHelper.Lerp(coil.Comp.SaturatedHitProbability, 1f, headroom);
    }

    // Triad: hold the arcing indicator while the coil can't bank a full strike, so engineers can see
    // saturation at a glance. Drains through the power net raise ChargeChangedEvent too, so the
    // indicator clears on its own as the coil pushes charge into the grid.
    private void OnChargeChanged(Entity<TeslaCoilComponent> coil, ref ChargeChangedEvent args)
    {
        _appearance.SetData(coil, TeslaCoilVisuals.Charged, args.Charge + coil.Comp.ChargeFromLightning > args.MaxCharge);
    }
}
