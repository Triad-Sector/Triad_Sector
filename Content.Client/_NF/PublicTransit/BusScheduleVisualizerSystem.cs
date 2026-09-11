using Content.Shared._NF.PublicTransit;
using Robust.Client.GameObjects;

namespace Content.Client._NF.PublicTransit;

/// <summary>
/// Tints a bus schedule sign to the colour of the stop the bus is heading for.
/// </summary>
// Triad: the missing client half of the Frontier bus sign. Appearance carries a Color under
// PublicTransitVisuals.Livery and this paints it onto the sign's Livery layer.
public sealed class BusScheduleVisualizerSystem : VisualizerSystem<BusScheduleComponent>
{
    protected override void OnAppearanceChange(EntityUid uid, BusScheduleComponent component, ref AppearanceChangeEvent args)
    {
        if (args.Sprite == null)
            return;

        if (!SpriteSystem.LayerMapTryGet((uid, args.Sprite), PublicTransitVisualLayers.Livery, out var layer, false))
            return;

        var color = AppearanceSystem.TryGetData<Color>(uid, PublicTransitVisuals.Livery, out var livery, args.Component)
            ? livery
            : component.IdleColor;

        SpriteSystem.LayerSetColor((uid, args.Sprite), layer, color);
    }
}
