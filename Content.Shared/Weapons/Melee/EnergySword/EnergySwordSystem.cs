using Content.Shared.Light;
using Content.Shared.Light.Components;
using Content.Shared.Toggleable;
using Robust.Shared.Random;

namespace Content.Shared.Weapons.Melee.EnergySword;

public sealed partial class EnergySwordSystem : EntitySystem
{
    [Dependency] private SharedRgbLightControllerSystem _rgbSystem = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private IRobustRandom _random = default!;

    // Triad - Tool interaction handling moved to the server system to support QuickDialog.
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<EnergySwordComponent, MapInitEvent>(OnMapInit);
    }
    // Used to pick a random color for the blade on map init.
    private void OnMapInit(Entity<EnergySwordComponent> entity, ref MapInitEvent args)
    {
        if (entity.Comp.ColorOptions.Count != 0)
        {
            entity.Comp.ActivatedColor = _random.Pick(entity.Comp.ColorOptions);
            Dirty(entity);
        }

        if (!TryComp(entity, out AppearanceComponent? appearanceComponent))
            return;

        _appearance.SetData(entity, ToggleableVisuals.Color, entity.Comp.ActivatedColor, appearanceComponent);
    }

    // Triad - Apply custom blade colors through the shared appearance system.
    /// <summary>
    /// Updates the blade color and its appearance data.
    /// </summary>
    public void SetActivatedColor(EntityUid uid, Color color)
    {
        if (!TryComp(uid, out EnergySwordComponent? component))
            return;

        component.ActivatedColor = color;
        Dirty(uid, component);

        if (TryComp(uid, out AppearanceComponent? appearance))
            _appearance.SetData(uid, ToggleableVisuals.Color, color, appearance);
    }

    // Triad - Keep RGB-cycle state changes in the component's owning system.
    /// <summary>
    /// Toggles RGB cycling on an energy sword.
    /// </summary>
    public void ToggleHacked(Entity<EnergySwordComponent> entity)
    {
        entity.Comp.Hacked = !entity.Comp.Hacked;

        if (entity.Comp.Hacked)
        {
            var rgb = EnsureComp<RgbLightControllerComponent>(entity);
            _rgbSystem.SetCycleRate(entity, entity.Comp.CycleRate, rgb);
        }
        else
            RemComp<RgbLightControllerComponent>(entity);

        Dirty(entity);
    }
}
