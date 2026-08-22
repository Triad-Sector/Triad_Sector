using Content.Shared._Triad.Atmos;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;

namespace Content.Client._Triad.Atmos;

/// <summary>
/// Triad: cross-fades a radiator's incandescent glow and its spill light
/// between thermal buckets.
/// </summary>
/// <remarks>
/// The server only tells us which bucket a radiator is in, and only when that
/// changes; it also writes that bucket's resting values to the networked light.
/// This system supplies the transition between them, walking colour, energy and
/// radius toward the new target over
/// <see cref="RadiatorGlowComponent.FadeDuration"/>. Interpolating here rather
/// than server-side is what lets the fade be smooth without putting a per-tick
/// value on the wire for every fin in an array.
///
/// The division of labour matters: the light is server-authoritative, so the
/// server has to own where a fade *lands* or state application would revert it
/// to the prototype's disabled default as soon as this system stopped writing.
/// The sprite glow layer is client-local and has no such constraint, so this
/// system owns it outright.
/// </remarks>
public sealed class RadiatorGlowSystem : EntitySystem
{
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private SharedPointLightSystem _pointLight = default!;
    [Dependency] private SpriteSystem _sprite = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RadiatorGlowComponent, AppearanceChangeEvent>(OnAppearanceChange);
    }

    private void OnAppearanceChange(EntityUid uid, RadiatorGlowComponent comp, ref AppearanceChangeEvent args)
    {
        if (!_appearance.TryGetData<RadiatorThermalBucket>(uid, RadiatorVisuals.Bucket, out var bucket, args.Component))
            bucket = RadiatorThermalBucket.Neutral;

        var (color, energy, radius) = RadiatorGlowRamp.Get(bucket);

        // Begin the new fade from wherever the current one had reached, so a
        // bucket change mid-fade doesn't jump.
        var t = Progress(comp);
        comp.StartColor = Color.InterpolateBetween(comp.StartColor, comp.TargetColor, t);
        comp.StartEnergy = float.Lerp(comp.StartEnergy, comp.TargetEnergy, t);
        comp.StartRadius = float.Lerp(comp.StartRadius, comp.TargetRadius, t);

        comp.TargetColor = color;
        comp.TargetEnergy = energy;
        comp.TargetRadius = radius;
        comp.Elapsed = 0f;
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        var query = EntityQueryEnumerator<RadiatorGlowComponent, SpriteComponent>();
        while (query.MoveNext(out var uid, out var comp, out var sprite))
        {
            // Finished fades stop costing anything. Safe to stop writing because
            // the server holds the resting light values; this only ever supplies
            // the journey between them.
            if (comp.Elapsed > comp.FadeDuration)
                continue;

            comp.Elapsed += frameTime;
            var t = Progress(comp);

            var color = Color.InterpolateBetween(comp.StartColor, comp.TargetColor, t);
            var energy = float.Lerp(comp.StartEnergy, comp.TargetEnergy, t);
            var radius = float.Lerp(comp.StartRadius, comp.TargetRadius, t);

            if (_sprite.LayerMapTryGet((uid, sprite), RadiatorVisualLayers.Glow, out var index, false))
            {
                _sprite.LayerSetColor((uid, sprite), index, color);
                _sprite.LayerSetVisible((uid, sprite), index, color.A > 0.004f);
            }

            if (!_pointLight.TryGetLight(uid, out var light))
                continue;

            var lit = energy > 0.001f;
            _pointLight.SetEnabled(uid, lit, light);

            if (!lit)
                continue;

            _pointLight.SetColor(uid, RadiatorGlowRamp.ToLightColor(color), light);
            _pointLight.SetEnergy(uid, energy, light);
            _pointLight.SetRadius(uid, radius, light);
        }
    }

    private static float Progress(RadiatorGlowComponent comp)
    {
        if (comp.FadeDuration <= 0f)
            return 1f;

        return Math.Clamp(comp.Elapsed / comp.FadeDuration, 0f, 1f);
    }

}
