using Content.Shared.Mech;
using Content.Shared.Mech.Components;
using Content.Shared.Mech.EntitySystems;
using Robust.Client.GameObjects;
using DrawDepth = Content.Shared.DrawDepth.DrawDepth;

namespace Content.Client.Mech;

/// <inheritdoc/>
public sealed partial class MechSystem : SharedMechSystem
{
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private SpriteSystem _sprite = default!;

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MechComponent, AppearanceChangeEvent>(OnAppearanceChanged);
    }

    private void OnAppearanceChanged(EntityUid uid, MechComponent component, ref AppearanceChangeEvent args)
    {
        if (args.Sprite == null)
            return;

        if (!_sprite.TryGetLayer((uid, args.Sprite), (int) MechVisualLayers.Base, out var layer, false))
            return;

        var state = component.BaseState;
        var drawDepth = DrawDepth.Mobs;
        if (component.BrokenState != null && _appearance.TryGetData<bool>(uid, MechVisuals.Broken, out var broken, args.Component) && broken)
        {
            state = component.BrokenState;
            drawDepth = DrawDepth.SmallMobs;
        }
        else if (component.OpenState != null && _appearance.TryGetData<bool>(uid, MechVisuals.Open, out var open, args.Component) && open)
        {
            state = component.OpenState;
            drawDepth = DrawDepth.SmallMobs;
        }

        _sprite.LayerSetRsiState(layer, state);
        _sprite.SetDrawDepth((uid, args.Sprite), (int) drawDepth);
    }
}
