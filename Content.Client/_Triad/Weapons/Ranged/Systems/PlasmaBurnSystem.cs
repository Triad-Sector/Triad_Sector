using System.Numerics;
using Content.Shared._Triad.Weapons.Ranged.Components;
using Content.Shared._Triad.Weapons.Ranged.Systems;
using Robust.Client.GameObjects;

namespace Content.Client._Triad.Weapons.Ranged.Systems;

public sealed partial class PlasmaBurnSystem : SharedPlasmaBurnSystem
{
    [Dependency] private SpriteSystem _sprite = default!;

    private const string BurnLayer = "plasmaburn";

    [SubscribeLocalEvent]
    private void OnAppearanceChange(Entity<UserPlasmaBurnComponent> ent, ref AppearanceChangeEvent args)
    {
        if (args.Sprite != null)
            UpdateAppearance(ent);
    }

    protected override void UpdateAppearance(Entity<UserPlasmaBurnComponent> ent)
    {
        if (!_sprite.LayerMapTryGet(ent.Owner, BurnLayer, out var index, false))
            return;

        // Change color based on stack amount
        var stacks = ent.Comp.Stacks;
        var saturationMulti = ent.Comp.SaturationIncreasePerStack * stacks;
        var brightnessMulti = ent.Comp.BrightnessIncreasePerStack * stacks;

        var colorHsv = Color.ToHsv(ent.Comp.Color);

        var hue = colorHsv.X;
        var sat = Math.Clamp(colorHsv.Y + saturationMulti, 0f, 1f);
        var val = Math.Clamp(colorHsv.Z + brightnessMulti, 0f, 1f);
        var newVector = new Vector4(hue, sat, val, 1f);
        var newColor = Color.FromHsv(newVector);

        _sprite.LayerSetColor(ent.Owner, index, newColor);
    }
}
