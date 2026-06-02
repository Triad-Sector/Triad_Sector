using Content.Server.PowerCell;
using Content.Shared.Alert;
using Content.Shared._Triad.Weapons.Ranged.Components;
using Content.Shared._Triad.Weapons.Ranged.Events;
using Content.Shared._Triad.Weapons.Ranged.Systems;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory;
using Content.Shared.PowerCell;
using Content.Shared.Popups;
using Content.Shared.Tag;
using Content.Shared.Verbs;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using System.Collections.Generic;

namespace Content.Server._Triad.Weapons.Ranged.Systems;

public sealed class WeaponHarnessPowerSystem : EntitySystem
{
    private static readonly ProtoId<TagPrototype> HeavyWeaponTag = "TriadHeavyWeapon";

    private readonly HashSet<EntityUid> _suppressNextLinkFeedback = new();

    [Dependency] private readonly AlertsSystem _alerts = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly WeaponHarnessSystem _harnessSupport = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly PowerCellSystem _powerCell = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly TagSystem _tag = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ReqWeapHarnComponent, GunShotEvent>(OnGunShot);
        SubscribeLocalEvent<ReqWeapHarnComponent, DroppedEvent>(OnSupportedWeaponDropped);
        SubscribeLocalEvent<WeapHarnGunEquipEvent>(OnSupportedWeaponEquippedHand);
        SubscribeLocalEvent<WeapHarnGunUnEquipEvent>(OnSupportedWeaponUnequippedHand);
        SubscribeLocalEvent<WeapHarnGunUnEquipInvEvent>(OnSupportedWeaponUnequippedInventory);
        SubscribeLocalEvent<WeapHarnEquipEvent>(OnHarnessEquipped);
        SubscribeLocalEvent<WeapHarnUnequipEvent>(OnHarnessUnequipped);
        SubscribeLocalEvent<WeapHarnPowerCellChangeEvent>(OnHarnessPowerCellChanged);
        SubscribeLocalEvent<WeapHarnComponent, GetVerbsEvent<AlternativeVerb>>(OnHarnessGetAlternativeVerbs);
    }

    private static readonly TimeSpan ActiveDrainDelay = TimeSpan.FromSeconds(1);

    private void OnGunShot(Entity<ReqWeapHarnComponent> ent, ref GunShotEvent args)
    {
        if (!_harnessSupport.TryGetActivePoweredHarness(ent.Owner, args.User, ent.Comp, out var harness) ||
            !TryComp<PowerCellDrawComponent>(harness.Owner, out var draw))
            return;

        if (!_powerCell.TryUseCharge(harness.Owner, draw.UseRate * args.Ammo.Count, user: args.User))
            return;

        _harnessSupport.RefreshHeldSupportedWeapons(args.User);
        UpdateHarnessAlerts(harness, args.User, true);
    }

    private void OnSupportedWeaponDropped(Entity<ReqWeapHarnComponent> ent, ref DroppedEvent args)
    {
        if (args.Handled ||
            !TryGetMagneticHarness(args.User, ent.Comp.SupportKey, out _) ||
            !CanMagneticallyRetrieve(ent.Owner))
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
            !TryGetMagneticHarness(user, supportKey, out _) ||
            !CanMagneticallyRetrieve(gun) ||
            _inventory.TryGetSlotEntity(user, WeaponHarnessSystem.SuitStorageSlot, out _))
            return;

        if (!_inventory.TryEquip(user, user, gun, WeaponHarnessSystem.SuitStorageSlot, silent: true))
            return;

        _harnessSupport.RefreshHeldSupportedWeapons(user);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<WeapHarnComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var harness, out var xform))
        {
            if (_timing.CurTime < harness.NextActiveDrain)
                continue;

            harness.NextActiveDrain = _timing.CurTime + ActiveDrainDelay;

            var wearer = xform.ParentUid;
            if (!_harnessSupport.TryGetPoweredHarnessEntity(wearer, harness.SupportKey, out var poweredHarness) ||
                poweredHarness.Owner != uid ||
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

    private void OnSupportedWeaponEquippedHand(WeapHarnGunEquipEvent args)
    {
        if (!TryComp<ReqWeapHarnComponent>(args.Gun, out var support))
            return;

        var showFeedback = !_suppressNextLinkFeedback.Remove(args.Gun);
        TryLinkHarness(args.User, support.SupportKey, showFeedback, showFeedback);
    }

    private void OnSupportedWeaponUnequippedHand(WeapHarnGunUnEquipEvent args)
    {
        if (!TryGetHarness(args.User, out var harnessUid, out var harness) ||
            _harnessSupport.HasSupportedWeaponInHandOrSuitStorage(args.User, harness.SupportKey))
            return;

        harness.LinkSoundPlayed = false;
    }

    private void OnSupportedWeaponUnequippedInventory(WeapHarnGunUnEquipInvEvent args)
    {
        if (args.Slot == WeaponHarnessSystem.SuitStorageSlot)
            _suppressNextLinkFeedback.Add(args.Gun);
    }

    private void OnHarnessEquipped(WeapHarnEquipEvent args)
    {
        if (args.Slot != WeaponHarnessSystem.BeltSlot ||
            !TryComp<WeapHarnComponent>(args.Harness, out var harness))
            return;

        TryLinkHarness(args.User, harness.SupportKey, true, false);
        UpdateHarnessAlerts((args.Harness, harness), args.User, true);
    }

    private void OnHarnessUnequipped(WeapHarnUnequipEvent args)
    {
        if (args.Slot != WeaponHarnessSystem.BeltSlot ||
            !TryComp<WeapHarnComponent>(args.Harness, out var harness))
            return;

        ClearHarnessAlerts(args.User, harness);
        ResetHarnessWarnings(harness);
    }

    private void OnHarnessPowerCellChanged(WeapHarnPowerCellChangeEvent args)
    {
        if (!TryComp<WeapHarnComponent>(args.Harness, out var harness))
            return;

        if (!TryGetHarnessWearer(args.Harness, out var wearer))
        {
            ResetHarnessWarnings(harness);
            return;
        }

        UpdateHarnessAlerts((args.Harness, harness), wearer, true);
    }

    private void OnHarnessGetAlternativeVerbs(Entity<WeapHarnComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
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

    private void ToggleMagneticRetrieval(Entity<WeapHarnComponent> ent, EntityUid user)
    {
        ent.Comp.MagneticRetrievalEnabled = !ent.Comp.MagneticRetrievalEnabled;

        var message = ent.Comp.MagneticRetrievalEnabled
            ? ent.Comp.MagneticRetrievalEnabledPopup
            : ent.Comp.MagneticRetrievalDisabledPopup;

        _popup.PopupEntity(message, ent.Owner, user, PopupType.Medium);
    }

    private void TryLinkHarness(EntityUid user, string supportKey, bool showPopup, bool playSound)
    {
        if (!_harnessSupport.TryGetPoweredHarnessEntity(user, supportKey, out var harness) ||
            !_harnessSupport.HasSupportedWeaponInHandOrSuitStorage(user, supportKey))
            return;

        if (!harness.Comp.LinkSoundPlayed)
        {
            if (playSound)
                PlayHarnessSound(harness.Comp.LinkSound, user);

            if (showPopup)
                _popup.PopupEntity(harness.Comp.LinkPopup, user, user, PopupType.Medium);

            harness.Comp.LinkSoundPlayed = true;
        }

        UpdateHarnessAlerts(harness, user, true);
    }

    private bool TryGetMagneticHarness(
        EntityUid user,
        string supportKey,
        out Entity<WeapHarnComponent> harness)
    {
        return _harnessSupport.TryGetPoweredHarnessEntity(user, supportKey, out harness) &&
               harness.Comp.MagneticRetrievalEnabled;
    }

    private bool CanMagneticallyRetrieve(EntityUid uid)
    {
        return _tag.HasTag(uid, HeavyWeaponTag);
    }

    private void UpdateHarnessAlerts(Entity<WeapHarnComponent> ent, EntityUid wearer, bool playSounds)
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
        out WeapHarnComponent harness)
    {
        harnessUid = default;
        harness = default!;

        if (!_inventory.TryGetSlotEntity(user, WeaponHarnessSystem.BeltSlot, out var belt) ||
            !TryComp<WeapHarnComponent>(belt.Value, out var harnessComp))
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

    private void ClearHarnessAlerts(EntityUid wearer, WeapHarnComponent harness)
    {
        _alerts.ClearAlert(wearer, harness.LowPowerAlert);
        _alerts.ClearAlert(wearer, harness.DepletedAlert);
    }

    private static void ResetHarnessWarnings(WeapHarnComponent harness)
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
