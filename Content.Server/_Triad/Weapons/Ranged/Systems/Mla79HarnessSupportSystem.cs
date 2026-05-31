using Content.Server.PowerCell;
using Content.Shared.Alert;
using Content.Shared._Triad.Weapons.Ranged.Components;
using Content.Shared._Triad.Weapons.Ranged.Events;
using Content.Shared._Triad.Weapons.Ranged.Systems;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory;
using Content.Shared.PowerCell;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using System.Collections.Generic;

namespace Content.Server._Triad.Weapons.Ranged.Systems;

public sealed class Mla79HarnessPowerDrainSystem : EntitySystem
{
    private readonly HashSet<EntityUid> _suppressNextLinkFeedback = new();

    [Dependency] private readonly AlertsSystem _alerts = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly Mla79HarnessSupportSystem _harnessSupport = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly PowerCellSystem _powerCell = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RequiresMla79HarnessSupportComponent, GunShotEvent>(OnGunShot);
        SubscribeLocalEvent<RequiresMla79HarnessSupportComponent, DroppedEvent>(OnMla79Dropped);
        SubscribeLocalEvent<Mla79HarnessGunEquippedHandEvent>(OnMla79EquippedHand);
        SubscribeLocalEvent<Mla79HarnessGunUnequippedHandEvent>(OnMla79UnequippedHand);
        SubscribeLocalEvent<Mla79HarnessGunUnequippedInventoryEvent>(OnMla79UnequippedInventory);
        SubscribeLocalEvent<Mla79HarnessEquippedEvent>(OnHarnessEquipped);
        SubscribeLocalEvent<Mla79HarnessUnequippedEvent>(OnHarnessUnequipped);
        SubscribeLocalEvent<Mla79HarnessPowerCellChangedEvent>(OnHarnessPowerCellChanged);
        SubscribeLocalEvent<Mla79HarnessComponent, GetVerbsEvent<AlternativeVerb>>(OnHarnessGetAlternativeVerbs);
    }

    private static readonly TimeSpan ActiveDrainDelay = TimeSpan.FromSeconds(1);

    private void OnGunShot(Entity<RequiresMla79HarnessSupportComponent> ent, ref GunShotEvent args)
    {
        if (!_harnessSupport.HasActiveSupport(ent.Owner, args.User, ent.Comp) ||
            !_harnessSupport.TryGetPoweredHarness(args.User, out var harnessUid) ||
            !TryComp<PowerCellDrawComponent>(harnessUid, out var draw))
        {
            return;
        }

        if (_powerCell.TryUseCharge(harnessUid, draw.UseRate * args.Ammo.Count, user: args.User))
        {
            _harnessSupport.RefreshHeldMla79(args.User);
            UpdateHarnessAlerts((harnessUid, Comp<Mla79HarnessComponent>(harnessUid)), args.User, true);
        }
    }

    private void OnMla79Dropped(Entity<RequiresMla79HarnessSupportComponent> ent, ref DroppedEvent args)
    {
        if (args.Handled ||
            !_harnessSupport.TryGetPoweredHarness(args.User, out var harnessUid) ||
            !TryComp<Mla79HarnessComponent>(harnessUid, out var harness) ||
            !harness.MagneticRetrievalEnabled)
        {
            return;
        }

        var gun = ent.Owner;
        var user = args.User;

        Timer.Spawn(0, () => TryRetrieveDroppedMla79(gun, user));
    }

    private void TryRetrieveDroppedMla79(EntityUid gun, EntityUid user)
    {
        if (Deleted(gun) ||
            Deleted(user) ||
            !_harnessSupport.TryGetPoweredHarness(user, out var harnessUid) ||
            !TryComp<Mla79HarnessComponent>(harnessUid, out var harness) ||
            !harness.MagneticRetrievalEnabled ||
            _inventory.TryGetSlotEntity(user, "suitstorage", out _))
        {
            return;
        }

        if (!_inventory.TryEquip(user, user, gun, "suitstorage", silent: true))
            return;

        _harnessSupport.RefreshHeldMla79(user);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<Mla79HarnessComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var harness, out var xform))
        {
            if (_timing.CurTime < harness.NextActiveDrain)
                continue;

            harness.NextActiveDrain = _timing.CurTime + ActiveDrainDelay;

            var wearer = xform.ParentUid;
            if (!_harnessSupport.TryGetPoweredHarness(wearer, out var harnessUid) ||
                harnessUid != uid ||
                !_harnessSupport.HasMla79InHandOrSuitStorage(wearer))
            {
                continue;
            }

            var charge = harness.ActiveChargePerSecond * (float) ActiveDrainDelay.TotalSeconds;
            if (!_powerCell.TryUseCharge(uid, charge))
                _harnessSupport.RefreshHeldMla79(wearer);

            UpdateHarnessAlerts((uid, harness), wearer, true);
        }
    }

    private void OnMla79EquippedHand(Mla79HarnessGunEquippedHandEvent args)
    {
        var showFeedback = !_suppressNextLinkFeedback.Remove(args.Gun);
        TryLinkHarness(args.User, showFeedback, showFeedback);
    }

    private void OnMla79UnequippedHand(Mla79HarnessGunUnequippedHandEvent args)
    {
        if (_harnessSupport.HasMla79InHandOrSuitStorage(args.User) ||
            !TryGetHarness(args.User, out var harnessUid) ||
            !TryComp<Mla79HarnessComponent>(harnessUid, out var harness))
        {
            return;
        }

        harness.LinkSoundPlayed = false;
    }

    private void OnMla79UnequippedInventory(Mla79HarnessGunUnequippedInventoryEvent args)
    {
        if (args.Slot == "suitstorage")
            _suppressNextLinkFeedback.Add(args.Gun);
    }

    private void OnHarnessEquipped(Mla79HarnessEquippedEvent args)
    {
        if (args.Slot != "belt")
            return;

        if (!TryComp<Mla79HarnessComponent>(args.Harness, out var harness))
            return;

        TryLinkHarness(args.User, true, false);
        UpdateHarnessAlerts((args.Harness, harness), args.User, true);
    }

    private void OnHarnessUnequipped(Mla79HarnessUnequippedEvent args)
    {
        if (args.Slot != "belt")
            return;

        if (!TryComp<Mla79HarnessComponent>(args.Harness, out var harness))
            return;

        ClearHarnessAlerts(args.User, harness);
        ResetHarnessWarnings(harness);
    }

    private void OnHarnessPowerCellChanged(Mla79HarnessPowerCellChangedEvent args)
    {
        if (!TryComp<Mla79HarnessComponent>(args.Harness, out var harness))
            return;

        if (!TryGetHarnessWearer(args.Harness, out var wearer))
        {
            ResetHarnessWarnings(harness);
            return;
        }

        UpdateHarnessAlerts((args.Harness, harness), wearer, true);
    }

    private void OnHarnessGetAlternativeVerbs(Entity<Mla79HarnessComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;

        if (TryGetHarnessWearer(ent.Owner, out var wearer) && wearer != args.User)
            return;

        var user = args.User;

        args.Verbs.Add(new AlternativeVerb
        {
            Text = ent.Comp.MagneticRetrievalEnabled
                ? ent.Comp.DisableMagneticRetrievalVerb
                : ent.Comp.EnableMagneticRetrievalVerb,
            Priority = 2,
            Act = () => ToggleMagneticRetrieval(ent, user),
        });
    }

    private void ToggleMagneticRetrieval(Entity<Mla79HarnessComponent> ent, EntityUid user)
    {
        ent.Comp.MagneticRetrievalEnabled = !ent.Comp.MagneticRetrievalEnabled;

        var message = ent.Comp.MagneticRetrievalEnabled
            ? ent.Comp.MagneticRetrievalEnabledPopup
            : ent.Comp.MagneticRetrievalDisabledPopup;

        _popup.PopupEntity(message, ent.Owner, user, PopupType.Medium);
    }

    private void TryLinkHarness(EntityUid user, bool showPopup, bool playSound)
    {
        if (!_harnessSupport.TryGetPoweredHarness(user, out var harnessUid) ||
            !TryComp<Mla79HarnessComponent>(harnessUid, out var harness) ||
            !_harnessSupport.HasMla79InHandOrSuitStorage(user))
        {
            return;
        }

        if (!harness.LinkSoundPlayed)
        {
            if (playSound)
                PlayHarnessSound(harness.LinkSound, user);

            if (showPopup)
                _popup.PopupEntity(harness.LinkPopup, user, user, PopupType.Medium);

            harness.LinkSoundPlayed = true;
        }

        UpdateHarnessAlerts((harnessUid, harness), user, true);
    }

    private void UpdateHarnessAlerts(Entity<Mla79HarnessComponent> ent, EntityUid wearer, bool playSounds)
    {
        if (!_powerCell.TryGetBatteryFromSlot(ent.Owner, out var battery))
        {
            ClearHarnessAlerts(wearer, ent.Comp);
            ResetHarnessWarnings(ent.Comp);
            return;
        }

        var fraction = battery.MaxCharge > 0f
            ? battery.CurrentCharge / battery.MaxCharge
            : 0f;

        var depleted = battery.CurrentCharge <= 0f;
        if (TryComp<PowerCellDrawComponent>(ent.Owner, out var draw))
            depleted |= battery.CurrentCharge < draw.UseRate;

        var low = !depleted && fraction <= ent.Comp.HalfChargeThreshold;

        if (depleted)
        {
            _alerts.ClearAlert(wearer, ent.Comp.LowPowerAlert);
            _alerts.ShowAlert(wearer, ent.Comp.DepletedAlert);

            if (playSounds && !ent.Comp.DepletedWarned)
                PlayHarnessSound(ent.Comp.DepletedSound, wearer);

            ent.Comp.HalfChargeWarned = true;
            ent.Comp.DepletedWarned = true;
            return;
        }

        if (low)
        {
            _alerts.ClearAlert(wearer, ent.Comp.DepletedAlert);
            _alerts.ShowAlert(wearer, ent.Comp.LowPowerAlert);

            if (playSounds && !ent.Comp.HalfChargeWarned)
                PlayHarnessSound(ent.Comp.HalfChargeSound, wearer);

            ent.Comp.HalfChargeWarned = true;
            ent.Comp.DepletedWarned = false;
            return;
        }

        ClearHarnessAlerts(wearer, ent.Comp);
        ent.Comp.HalfChargeWarned = false;
        ent.Comp.DepletedWarned = false;
    }

    private bool TryGetHarness(EntityUid user, out EntityUid harnessUid)
    {
        harnessUid = default;

        if (!_inventory.TryGetSlotEntity(user, "belt", out var belt) ||
            !HasComp<Mla79HarnessComponent>(belt.Value))
        {
            return false;
        }

        harnessUid = belt.Value;
        return true;
    }

    private bool TryGetHarnessWearer(EntityUid harnessUid, out EntityUid wearer)
    {
        wearer = Transform(harnessUid).ParentUid;
        return TryGetHarness(wearer, out var beltHarness) && beltHarness == harnessUid;
    }

    private void ClearHarnessAlerts(EntityUid wearer, Mla79HarnessComponent harness)
    {
        _alerts.ClearAlert(wearer, harness.LowPowerAlert);
        _alerts.ClearAlert(wearer, harness.DepletedAlert);
    }

    private static void ResetHarnessWarnings(Mla79HarnessComponent harness)
    {
        harness.HalfChargeWarned = false;
        harness.DepletedWarned = false;
        harness.LinkSoundPlayed = false;
    }

    private void PlayHarnessSound(SoundSpecifier? sound, EntityUid user)
    {
        if (sound == null)
            return;

        _audio.PlayEntity(sound, Filter.Empty().FromEntities(user), user, false);
    }
}
