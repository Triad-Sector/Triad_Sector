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

public sealed class WeaponHarnessPowerSystem : EntitySystem
{
    private readonly HashSet<EntityUid> _suppressNextLinkFeedback = new();

    [Dependency] private readonly AlertsSystem _alerts = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly WeaponHarnessSystem _harnessSupport = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly PowerCellSystem _powerCell = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RequiresWeaponHarnessComponent, GunShotEvent>(OnGunShot);
        SubscribeLocalEvent<RequiresWeaponHarnessComponent, DroppedEvent>(OnSupportedWeaponDropped);
        SubscribeLocalEvent<WeaponHarnessGunEquippedHandEvent>(OnSupportedWeaponEquippedHand);
        SubscribeLocalEvent<WeaponHarnessGunUnequippedHandEvent>(OnSupportedWeaponUnequippedHand);
        SubscribeLocalEvent<WeaponHarnessGunUnequippedInventoryEvent>(OnSupportedWeaponUnequippedInventory);
        SubscribeLocalEvent<WeaponHarnessEquippedEvent>(OnHarnessEquipped);
        SubscribeLocalEvent<WeaponHarnessUnequippedEvent>(OnHarnessUnequipped);
        SubscribeLocalEvent<WeaponHarnessPowerCellChangedEvent>(OnHarnessPowerCellChanged);
        SubscribeLocalEvent<WeaponHarnessComponent, GetVerbsEvent<AlternativeVerb>>(OnHarnessGetAlternativeVerbs);
    }

    private static readonly TimeSpan ActiveDrainDelay = TimeSpan.FromSeconds(1);

    private void OnGunShot(Entity<RequiresWeaponHarnessComponent> ent, ref GunShotEvent args)
    {
        if (!_harnessSupport.HasActiveSupport(ent.Owner, args.User, ent.Comp) ||
            !_harnessSupport.TryGetPoweredHarness(args.User, ent.Comp.SupportKey, out var harnessUid) ||
            !TryComp<PowerCellDrawComponent>(harnessUid, out var draw))
            return;

        if (!_powerCell.TryUseCharge(harnessUid, draw.UseRate * args.Ammo.Count, user: args.User))
            return;

        _harnessSupport.RefreshHeldSupportedWeapons(args.User);
        UpdateHarnessAlerts((harnessUid, Comp<WeaponHarnessComponent>(harnessUid)), args.User, true);
    }

    private void OnSupportedWeaponDropped(Entity<RequiresWeaponHarnessComponent> ent, ref DroppedEvent args)
    {
        if (args.Handled ||
            !_harnessSupport.TryGetPoweredHarness(args.User, ent.Comp.SupportKey, out var harnessUid) ||
            !TryComp<WeaponHarnessComponent>(harnessUid, out var harness) ||
            !harness.MagneticRetrievalEnabled)
            return;

        var gun = ent.Owner;
        var user = args.User;
        var supportKey = ent.Comp.SupportKey;

        Timer.Spawn(0, () => TryRetrieveDroppedSupportedWeapon(gun, user, supportKey));
    }

    private void TryRetrieveDroppedSupportedWeapon(EntityUid gun, EntityUid user, string supportKey)
    {
        if (Deleted(gun) ||
            Deleted(user) ||
            !_harnessSupport.TryGetPoweredHarness(user, supportKey, out var harnessUid) ||
            !TryComp<WeaponHarnessComponent>(harnessUid, out var harness) ||
            !harness.MagneticRetrievalEnabled ||
            _inventory.TryGetSlotEntity(user, WeaponHarnessSystem.SuitStorageSlot, out _))
            return;

        if (!_inventory.TryEquip(user, user, gun, WeaponHarnessSystem.SuitStorageSlot, silent: true))
            return;

        _harnessSupport.RefreshHeldSupportedWeapons(user);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<WeaponHarnessComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var harness, out var xform))
        {
            if (_timing.CurTime < harness.NextActiveDrain)
                continue;

            harness.NextActiveDrain = _timing.CurTime + ActiveDrainDelay;

            var wearer = xform.ParentUid;
            if (!_harnessSupport.TryGetPoweredHarness(wearer, harness.SupportKey, out var harnessUid) ||
                harnessUid != uid ||
                !_harnessSupport.HasSupportedWeaponInHandOrSuitStorage(wearer, harness.SupportKey))
            {
                continue;
            }

            var charge = harness.ActiveChargePerSecond * (float) ActiveDrainDelay.TotalSeconds;
            if (!_powerCell.TryUseCharge(uid, charge))
                _harnessSupport.RefreshHeldSupportedWeapons(wearer);

            UpdateHarnessAlerts((uid, harness), wearer, true);
        }
    }

    private void OnSupportedWeaponEquippedHand(WeaponHarnessGunEquippedHandEvent args)
    {
        if (!TryComp<RequiresWeaponHarnessComponent>(args.Gun, out var support))
            return;

        var showFeedback = !_suppressNextLinkFeedback.Remove(args.Gun);
        TryLinkHarness(args.User, support.SupportKey, showFeedback, showFeedback);
    }

    private void OnSupportedWeaponUnequippedHand(WeaponHarnessGunUnequippedHandEvent args)
    {
        if (!TryGetHarness(args.User, out var harnessUid, out var harness) ||
            _harnessSupport.HasSupportedWeaponInHandOrSuitStorage(args.User, harness.SupportKey))
            return;

        harness.LinkSoundPlayed = false;
    }

    private void OnSupportedWeaponUnequippedInventory(WeaponHarnessGunUnequippedInventoryEvent args)
    {
        if (args.Slot == WeaponHarnessSystem.SuitStorageSlot)
            _suppressNextLinkFeedback.Add(args.Gun);
    }

    private void OnHarnessEquipped(WeaponHarnessEquippedEvent args)
    {
        if (args.Slot != WeaponHarnessSystem.BeltSlot ||
            !TryComp<WeaponHarnessComponent>(args.Harness, out var harness))
            return;

        TryLinkHarness(args.User, harness.SupportKey, true, false);
        UpdateHarnessAlerts((args.Harness, harness), args.User, true);
    }

    private void OnHarnessUnequipped(WeaponHarnessUnequippedEvent args)
    {
        if (args.Slot != WeaponHarnessSystem.BeltSlot ||
            !TryComp<WeaponHarnessComponent>(args.Harness, out var harness))
            return;

        ClearHarnessAlerts(args.User, harness);
        ResetHarnessWarnings(harness);
    }

    private void OnHarnessPowerCellChanged(WeaponHarnessPowerCellChangedEvent args)
    {
        if (!TryComp<WeaponHarnessComponent>(args.Harness, out var harness))
            return;

        if (!TryGetHarnessWearer(args.Harness, out var wearer))
        {
            ResetHarnessWarnings(harness);
            return;
        }

        UpdateHarnessAlerts((args.Harness, harness), wearer, true);
    }

    private void OnHarnessGetAlternativeVerbs(Entity<WeaponHarnessComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
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

    private void ToggleMagneticRetrieval(Entity<WeaponHarnessComponent> ent, EntityUid user)
    {
        ent.Comp.MagneticRetrievalEnabled = !ent.Comp.MagneticRetrievalEnabled;

        var message = ent.Comp.MagneticRetrievalEnabled
            ? ent.Comp.MagneticRetrievalEnabledPopup
            : ent.Comp.MagneticRetrievalDisabledPopup;

        _popup.PopupEntity(message, ent.Owner, user, PopupType.Medium);
    }

    private void TryLinkHarness(EntityUid user, string supportKey, bool showPopup, bool playSound)
    {
        if (!_harnessSupport.TryGetPoweredHarness(user, supportKey, out var harnessUid) ||
            !TryComp<WeaponHarnessComponent>(harnessUid, out var harness) ||
            !_harnessSupport.HasSupportedWeaponInHandOrSuitStorage(user, supportKey))
            return;

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

    private void UpdateHarnessAlerts(Entity<WeaponHarnessComponent> ent, EntityUid wearer, bool playSounds)
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

    private bool TryGetHarness(
        EntityUid user,
        out EntityUid harnessUid,
        out WeaponHarnessComponent harness)
    {
        harnessUid = default;
        harness = default!;

        if (!_inventory.TryGetSlotEntity(user, WeaponHarnessSystem.BeltSlot, out var belt) ||
            !TryComp<WeaponHarnessComponent>(belt.Value, out var harnessComp))
            return false;

        harnessUid = belt.Value;
        harness = harnessComp;
        return true;
    }

    private bool TryGetHarnessWearer(EntityUid harnessUid, out EntityUid wearer)
    {
        wearer = Transform(harnessUid).ParentUid;
        return TryGetHarness(wearer, out var beltHarness, out _) && beltHarness == harnessUid;
    }

    private void ClearHarnessAlerts(EntityUid wearer, WeaponHarnessComponent harness)
    {
        _alerts.ClearAlert(wearer, harness.LowPowerAlert);
        _alerts.ClearAlert(wearer, harness.DepletedAlert);
    }

    private static void ResetHarnessWarnings(WeaponHarnessComponent harness)
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
