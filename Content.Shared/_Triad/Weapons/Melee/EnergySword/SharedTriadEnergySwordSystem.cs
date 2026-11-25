using Content.Shared.Weapons.Melee.EnergySword;
using Content.Shared.Emag.Systems;
using Robust.Shared.Random;
using Content.Shared.Examine;
using Content.Shared.Emag.Components;
using Content.Shared.Light.Components;

namespace Content.Shared._Triad.Weapons.Melee.EnergySword;

public abstract partial class SharedTriadEnergySwordSystem : EntitySystem
{
    [Dependency] private EnergySwordSystem _energySword = default!;
    [Dependency] private EmagSystem _emag = default!;
    [Dependency] private IRobustRandom _random = default!;

    [SubscribeLocalEvent]
    private void OnComponentStartup(Entity<EnergySwordComponent> entity, ref ComponentStartup args)
    {
        // PickedColor is so saved energy swords do not change colors across saves
        if (entity.Comp.ColorOptions.Count != 0 && !entity.Comp.PickedColor)
        {
            entity.Comp.PickedColor = true;
            entity.Comp.ActivatedColor = _random.Pick(entity.Comp.ColorOptions);
            Dirty(entity);
        }

        _energySword.SetActivatedColor(entity, entity.Comp.ActivatedColor); // Triad
    }

    [SubscribeLocalEvent]
    private void OnEmagged(Entity<EnergySwordComponent> ent, ref GotEmaggedEvent args)
    {
        if (ent.Comp.BlockHacking)
            return;

        if (!_emag.CompareFlag(args.Type, EmagType.Interaction))
            return;

        RemCompDeferred<RgbLightControllerComponent>(ent.Owner);
        args.Handled = true;
    }

    [SubscribeLocalEvent]
    private void OnExamined(Entity<EnergySwordComponent> ent, ref ExaminedEvent args)
    {
        if (!HasComp<EmaggedComponent>(ent) || ent.Comp.EmaggedExamineText is not { } locId)
            return;

        var examineText = Loc.GetString(locId, ("item", ent.Owner));
        args.PushMarkup(examineText);
    }

    public virtual bool TryOpenColorPicker(Entity<EnergySwordComponent> ent, EntityUid user)
    {
        return true;
    }
}
