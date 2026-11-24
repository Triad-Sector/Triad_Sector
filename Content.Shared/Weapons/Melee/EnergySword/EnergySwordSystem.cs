using Content.Shared._Triad.Weapons.Melee.EnergySword;
using Content.Shared.Emag.Components;
using Content.Shared.Interaction;
using Content.Shared.Light;
using Content.Shared.Light.Components;
using Content.Shared.Toggleable;
using Content.Shared.Tools.Systems;

namespace Content.Shared.Weapons.Melee.EnergySword;

public sealed partial class EnergySwordSystem : EntitySystem
{
    [Dependency] private SharedRgbLightControllerSystem _rgbSystem = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    // [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedToolSystem _toolSystem = default!;
    [Dependency] private SharedTriadEnergySwordSystem _triadEnergySword = default!;

    public override void Initialize()
    {
        base.Initialize();

        // SubscribeLocalEvent<EnergySwordComponent, MapInitEvent>(OnMapInit); - Triad: moved this event to SharedTriadEnergySwordSystem.
        SubscribeLocalEvent<EnergySwordComponent, InteractUsingEvent>(OnInteractUsing);
    }

    // Triad - moved this event to SharedTriadEnergySwordSystem.
    // Used to pick a random color for the blade on map init.
    //private void OnMapInit(Entity<EnergySwordComponent> entity, ref MapInitEvent args)
    //{
    //    if (entity.Comp.PickedColor)
    //        return;

    //    entity.Comp.PickedColor = true;

    //   if (entity.Comp.ColorOptions.Count != 0)
    //    {
    //        entity.Comp.ActivatedColor = _random.Pick(entity.Comp.ColorOptions);
    //        Dirty(entity);
    //   }

    //    SetActivatedColor(entity, entity.Comp.ActivatedColor); // Triad
    //}

    // Triad - Apply custom blade colors through the shared appearance system.
    /// <summary>
    /// Updates the blade color and its appearance data.
    /// </summary>
    public void SetActivatedColor(Entity<EnergySwordComponent> ent, Color color)
    {
        ent.Comp.ActivatedColor = color;
        Dirty(ent);

        if (!TryComp(ent, out AppearanceComponent? appearanceComponent))
            return;

        _appearance.SetData(ent, ToggleableVisuals.Color, color, appearanceComponent);
    }

    // Used to make the blade multicolored when using a multitool on it.
    private void OnInteractUsing(Entity<EnergySwordComponent> entity, ref InteractUsingEvent args)
    {
        if (args.Handled)
            return;

        if (entity.Comp.BlockHacking || !_toolSystem.HasQuality(args.Used, SharedToolSystem.PulseQuality)) // Frontier: add entity.Comp.BlocksHacking
            return;

        args.Handled = true;

        // Triad - activate a color picker for emagged eswords
        if (HasComp<EmaggedComponent>(entity.Owner))
        {
            _triadEnergySword.TryOpenColorPicker(entity, args.User);
            return;
        }

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
