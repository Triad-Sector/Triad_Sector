using Content.Shared.Examine;
using Content.Shared.Popups;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Timing;
using Content.Shared._Triad.Weapons.Ranged.Components;
using Content.Shared.Item.ItemToggle.Components;
using Content.Shared.Verbs;
using Content.Shared.DoAfter;
using Content.Shared._Triad.Weapons.Ranged.Events;
using System.Dynamic;
using Content.Shared.Weapons.Ranged.Components;

namespace Content.Shared._Triad.Weapons.Ranged.Systems;

public sealed partial class GunToggleableSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedGunSystem _gun = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private ItemToggleSystem _itemToggle = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GunComponent, ItemToggledEvent>(OnGunItemToggled);

        SubscribeLocalEvent<GunToggleableBonusComponent, GunRefreshModifiersEvent>(OnGunRefreshModifiers);
        SubscribeLocalEvent<GunToggleableBonusComponent, ShotAttemptedEvent>(OnShootAttempt);
        SubscribeLocalEvent<GunToggleableBonusComponent, ExaminedEvent>(OnExamine);
        SubscribeLocalEvent<GunToggleableBonusComponent, ItemToggledEvent>(OnToggled);
        SubscribeLocalEvent<GunToggleableBonusComponent, GetVerbsEvent<AlternativeVerb>>(OnAlternateVerb);
        SubscribeLocalEvent<GunToggleableBonusComponent, GunToggleableToggleDoAfterEvent>(OnToggleDoAfterEvent);

        SubscribeLocalEvent<GunChangeShotSoundOnToggledComponent, GunRefreshModifiersEvent>(OnGetToggleSound);
    }

    private void OnGunItemToggled(Entity<GunComponent> ent, ref ItemToggledEvent args)
    {
        _gun.RefreshModifiers(ent.Owner, args.User);
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

    // Temporary shitcode for doafter toggle since item toggle doesn't support doafters yet
    private void OnAlternateVerb(Entity<GunToggleableBonusComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!TryComp<ItemToggleComponent>(ent.Owner, out var toggle))
            return;

        if (!args.CanAccess || !args.CanInteract)
            return;

        var user = args.User;

        args.Verbs.Add(new AlternativeVerb()
        {
            Text = !toggle.Activated ? Loc.GetString(toggle.VerbToggleOn) : Loc.GetString(toggle.VerbToggleOff),
            Priority = toggle.AltPriority,
            Act = () =>
            {
                var ev = new GunToggleableToggleDoAfterEvent();
                var doAfterEventArgs = new DoAfterArgs(EntityManager, user, ent.Comp.DoAfterTime, ev, ent.Owner, target: ent.Owner, used: ent.Owner)
                {
                    BreakOnMove = false,
                    BreakOnDamage = false,
                    NeedHand = true,
                };

                _doAfter.TryStartDoAfter(doAfterEventArgs);
            }
        });
    }

    private void OnToggleDoAfterEvent(Entity<GunToggleableBonusComponent> ent, ref GunToggleableToggleDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled)
            return;

        if (!TryComp<ItemToggleComponent>(ent.Owner, out var toggle))
            return;

        args.Handled = true;
        _itemToggle.Toggle((ent.Owner, toggle), args.User, predicted: toggle.Predictable);
    }

    private void OnGetToggleSound(Entity<GunChangeShotSoundOnToggledComponent> ent, ref GunRefreshModifiersEvent args)
    {
        if (!_itemToggle.IsActivated(ent.Owner))
            return;

        args.SoundGunshot = ent.Comp.Sound;
    }
}
