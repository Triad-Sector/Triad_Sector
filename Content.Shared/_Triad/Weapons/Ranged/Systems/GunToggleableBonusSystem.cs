using Content.Shared.Examine;
using Content.Shared.Popups;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Timing;
using Content.Shared._Triad.Weapons.Ranged.Components;
using Content.Shared.Item.ItemToggle.Components;

namespace Content.Shared._Triad.Weapons.Ranged.Systems;

public sealed class GunToggleableBonusSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly ItemToggleSystem _itemToggle = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GunToggleableBonusComponent, GunRefreshModifiersEvent>(OnGunRefreshModifiers);
        SubscribeLocalEvent<GunToggleableBonusComponent, ShotAttemptedEvent>(OnShootAttempt);
        SubscribeLocalEvent<GunToggleableBonusComponent, ExaminedEvent>(OnExamine);
        SubscribeLocalEvent<GunToggleableBonusComponent, ItemToggledEvent>(OnToggled);
    }

    private void OnGunRefreshModifiers(Entity<GunToggleableBonusComponent> bonus, ref GunRefreshModifiersEvent args)
    {
        if (!_itemToggle.IsActivated(bonus.Owner))
            return;

        args.MinAngle += bonus.Comp.MinAngle;
        args.MaxAngle += bonus.Comp.MaxAngle;
        args.AngleDecay += bonus.Comp.AngleDecay;
        args.AngleIncrease += bonus.Comp.AngleIncrease;
        args.FireRate += bonus.Comp.BonusFireRate;
    }

    private void OnShootAttempt(Entity<GunToggleableBonusComponent> ent, ref ShotAttemptedEvent args)
    {
        if (ent.Comp.RequiresToggle && _itemToggle.IsActivated(ent.Owner))
        {
            args.Cancel();

            var time = _timing.CurTime;
            if (time > ent.Comp.LastPopup + ent.Comp.PopupCooldown)
            {
                ent.Comp.LastPopup = time;
                var message = Loc.GetString(ent.Comp.RequiresToggledMessage, ("item", ent.Owner));
                _popup.PopupClient(message, args.Used, args.User);
            }
        }
    }

    private void OnExamine(Entity<GunToggleableBonusComponent> ent, ref ExaminedEvent args)
    {
        if (ent.Comp.ExamineMessage != null)
            args.PushText(Loc.GetString(ent.Comp.ExamineMessage));
    }

    private void OnToggled(Entity<GunToggleableBonusComponent> ent, ref ItemToggledEvent args)
    {
        _gun.RefreshModifiers(ent.Owner);
    }
}
