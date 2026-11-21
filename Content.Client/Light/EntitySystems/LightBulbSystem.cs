using Content.Shared.Light.Components;
using Robust.Client.GameObjects;

namespace Content.Client.Light.Visualizers;

public sealed class LightBulbSystem : VisualizerSystem<LightBulbComponent>
{
    protected override void OnAppearanceChange(EntityUid uid, LightBulbComponent comp, ref AppearanceChangeEvent args)
    {
        if (args.Sprite == null)
            return;

        // update sprite state
        if (args.TryGetData<LightBulbState>(LightBulbVisuals.State, out var state))
        {
            switch (state)
            {
                case LightBulbState.Normal:
                    SpriteSystem.LayerSetRsiState((uid, args.Sprite), LightBulbVisualLayers.Base, comp.NormalSpriteState);
                    break;
                case LightBulbState.Broken:
                    SpriteSystem.LayerSetRsiState((uid, args.Sprite), LightBulbVisualLayers.Base, comp.BrokenSpriteState);
                    break;
                case LightBulbState.Burned:
                    SpriteSystem.LayerSetRsiState((uid, args.Sprite), LightBulbVisualLayers.Base, comp.BurnedSpriteState);
                    break;
            }
        }

        // also update sprites color
        if (args.TryGetData<Color>(LightBulbVisuals.Color, out var color))
        {
            SpriteSystem.SetColor((uid, args.Sprite), color);
        }
    }
}
