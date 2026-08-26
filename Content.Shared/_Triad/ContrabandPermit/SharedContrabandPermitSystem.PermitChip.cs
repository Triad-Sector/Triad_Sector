using Content.Shared.Damage;
using Content.Shared.DoAfter;
using Content.Shared.Examine;
using Content.Shared.Forensics.Components;
using Content.Shared.Humanoid;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Utility;

namespace Content.Shared._Triad.ContrabandPermit;

public abstract partial class SharedContrabandPermitSystem : EntitySystem
{
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    private void InitializePermitChip()
    {
        SubscribeLocalEvent<ContrabandPermitChipComponent, ActivateInWorldEvent>(OnPermitChipActivate);
        SubscribeLocalEvent<ContrabandPermitChipComponent, ContrabandPermitChipScanIdentityDoAfterEvent>(OnPermitChipScanDoAfter);
        SubscribeLocalEvent<ContrabandPermitChipComponent, AfterInteractEvent>(OnPermitChipInteract);
        SubscribeLocalEvent<ContrabandPermitChipComponent, ExaminedEvent>(OnPermitChipExamine);
        SubscribeLocalEvent<ContrabandPermitChipComponent, GetVerbsEvent<Verb>>(OnPermitChipGetVerbs);
        SubscribeLocalEvent<ContrabandPermitChipComponent, GetVerbsEvent<UtilityVerb>>(OnPermitChipGetUtilityVerbs);
    }

    private void OnPermitChipActivate(Entity<ContrabandPermitChipComponent> ent, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        // Need to scan the item first
        if (ent.Comp.ScannedPermitCarrier != null || ent.Comp.ScannedItem == null)
            return;

        var user = args.User;

        // Only mobs that are humanoid!
        if (!HasComp<HumanoidAppearanceComponent>(user))
            return;

        if (!_whitelist.CheckBoth(user, ent.Comp.PermitCarrierBlacklist, ent.Comp.PermitCarrierWhitelist))
        {
            _popup.PopupClient(Loc.GetString("contraband-permit-chip-scan-error"), user, user);
            return;
        }

        var ev = new ContrabandPermitChipScanIdentityDoAfterEvent();
        var doAfter = new DoAfterArgs(EntityManager, user, ent.Comp.ScanIdDelay, ev, ent.Owner, user)
        {
            BreakOnMove = true,
            CancelDuplicate = true,
            DuplicateCondition = DuplicateConditions.SameEvent,
            NeedHand = true,
            BreakOnHandChange = true,
        };

        if (_doAfter.TryStartDoAfter(doAfter))
        {
            _popup.PopupClient(Loc.GetString("contraband-permit-chip-scan-id-start"), user, user);
            args.Handled = true;
        }
    }

    private void OnPermitChipScanDoAfter(Entity<ContrabandPermitChipComponent> ent, ref ContrabandPermitChipScanIdentityDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled)
            return;

        var user = args.User;

        // Your finger gets pricked if you're not a robot
        if (HasComp<DnaComponent>(user))
        {
            _damageable.TryChangeDamage(user, ent.Comp.PrickDamage, true, false);
            _popup.PopupClient(Loc.GetString("contraband-permit-chip-scan-id-dna-end"), user, user);
        }
        else
        {
            _popup.PopupClient(Loc.GetString("contraband-permit-chip-scan-id-no-dna-end"), user, user);
        }

        ent.Comp.ScannedPermitCarrier = GetNetEntity(user);
        Dirty(ent);

        _audio.PlayPredicted(ent.Comp.ScanSound, ent.Owner, user);
        args.Handled = true;
    }

    private void OnPermitChipInteract(Entity<ContrabandPermitChipComponent> ent, ref AfterInteractEvent args)
    {
        if (args.Handled)
            return;

        if (!args.CanReach || args.Target is not { Valid: true } target)
            return;

        if (TryScan(ent, target, args.User))
            args.Handled = true;
    }

    private bool TryScan(Entity<ContrabandPermitChipComponent> ent, EntityUid target, EntityUid actor)
    {
        if (HasComp<ContrabandPermitItemComponent>(target))
        {
            _popup.PopupClient(Loc.GetString("contraband-permit-chip-failure"), actor, actor, PopupType.SmallCaution);
            return false;
        }

        if (!TryComp<ContrabandPermittableComponent>(target, out var permittable) || !permittable.Permittable)
            return false;

        ent.Comp.ScannedItem = GetNetEntity(target);
        Dirty(ent);

        _audio.PlayPredicted(ent.Comp.ScanSound, target, actor);
        _popup.PopupClient(Loc.GetString("contraband-permit-chip-success", ("item", Identity.Entity(target, EntityManager))), actor, actor, PopupType.Medium);
        return true;
    }

    private void OnPermitChipExamine(Entity<ContrabandPermitChipComponent> ent, ref ExaminedEvent args)
    {
        if (ent.Comp.ScannedItem is { } item)
        {
            var fromNetEnt = GetEntity(item);
            args.PushMarkup(Loc.GetString("contraband-permit-chip-examine-signature", ("item", Identity.Entity(fromNetEnt, EntityManager))));
        }
        else
        {
            args.PushMarkup(Loc.GetString("contraband-permit-chip-examine-help"));
        }

        if (ent.Comp.ScannedPermitCarrier is { } carrier)
        {
            var fromNetEnt = GetEntity(carrier);
            args.PushMarkup(Loc.GetString("contraband-permit-chip-examine-owner", ("owner", Identity.Entity(fromNetEnt, EntityManager))), -1);
        }
        else if (ent.Comp.ScannedItem != null)
        {
            args.PushMarkup(Loc.GetString("contraband-permit-chip-examine-help-id"), -1);
        }
    }

    private void OnPermitChipGetVerbs(Entity<ContrabandPermitChipComponent> ent, ref GetVerbsEvent<Verb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;

        var user = args.User;

        if (ent.Comp.ScannedItem != null || ent.Comp.ScannedPermitCarrier != null)
        {
            Verb verb = new()
            {
                Act = () => ClearScannedItem(ent, user),
                Text = Loc.GetString("contraband-permit-chip-clear-verb"),
                Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/delete.svg.192dpi.png")),
                Priority = -3,
                DoContactInteraction = true
            };
            args.Verbs.Add(verb);
        }
    }

    private void OnPermitChipGetUtilityVerbs(Entity<ContrabandPermitChipComponent> ent, ref GetVerbsEvent<UtilityVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess)
            return;

        var user = args.User;
        var item = args.Target;

        if (!TryComp<ContrabandPermittableComponent>(item, out var permittable) || !permittable.Permittable)
            return;

        var verb = new UtilityVerb()
        {
            Act = () => TryScan(ent, item, user),
            IconEntity = GetNetEntity(ent.Owner),
            Text = Loc.GetString("contraband-permit-scan-verb-text"),
            Message = Loc.GetString("contraband-permit-scan-verb-message"),
            DoContactInteraction = true
        };

        args.Verbs.Add(verb);
    }

    private void ClearScannedItem(Entity<ContrabandPermitChipComponent> ent, EntityUid user)
    {
        ent.Comp.ScannedItem = null;
        ent.Comp.ScannedPermitCarrier = null;
        Dirty(ent);

        _audio.PlayPredicted(ent.Comp.ClearSound, ent.Owner, user);
        _popup.PopupClient(Loc.GetString("contraband-permit-chip-cleared"), user, user);
    }
}
