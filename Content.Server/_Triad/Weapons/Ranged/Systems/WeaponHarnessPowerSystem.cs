using Content.Server.PowerCell;
using Content.Shared.Alert;
using Content.Shared._Triad.Weapons.Ranged.Components;
using Content.Shared._Triad.Weapons.Ranged.Events;
using Content.Shared._Triad.Weapons.Ranged.Systems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory;
using Content.Shared.Mobs;
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

/// Handles powered harness charge use, alerts, link feedback, magnetic retrieval, and retrieval toggle verbs.
/// Magnetic retrieval stores supported weapons in the harness-configured <see cref="WeapHarnComponent.RetrievalSlot"/>
/// when dropped or when the wearer becomes critical or dead.
public sealed class WeaponHarnessPowerSystem : EntitySystem
{
    private static readonly ProtoId<TagPrototype> HeavyWeaponTag = "TriadHeavyWeapon";

    private readonly HashSet<EntityUid> _suppressNextLinkFeedback = new();

    [Dependency] private readonly AlertsSystem _alerts = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly WeaponHarnessSystem _harnessSupport = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly PowerCellSystem _powerCell = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly TagSystem _tag = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ReqWeapHarnComponent, GunShotEvent>(OnGunShot);
        SubscribeLocalEvent<ReqWeapHarnComponent, DroppedEvent>(OnSupWeapDrop);
        SubscribeLocalEvent<WeapHarnGunEquipEvent>(OnSupWeapEquipHand);
        SubscribeLocalEvent<WeapHarnGunUnEquipEvent>(OnSupWeapUnequipHand);
        SubscribeLocalEvent<WeapHarnGunUnEquipInvEvent>(OnSupWeapUnequipInv);
        SubscribeLocalEvent<WeapHarnEquipEvent>(OnHarnEquip);
        SubscribeLocalEvent<WeapHarnUnequipEvent>(OnHarnUnequip);
        SubscribeLocalEvent<WeapHarnPowerCellChangeEvent>(OnHarnPowerCellChange);
        SubscribeLocalEvent<WeapHarnComponent, GetVerbsEvent<Verb>>(OnHarnGetVerbs);
        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
    }

    private static readonly TimeSpan ActiveDrainDelay = TimeSpan.FromSeconds(1);

    private void OnGunShot(Entity<ReqWeapHarnComponent> ent, ref GunShotEvent args)
    {
        if (!_harnessSupport.TryGetActivePowHarn(ent.Owner, args.User, ent.Comp, out var harness) ||
            !TryComp<PowerCellDrawComponent>(harness.Owner, out var draw))
            return;

        if (!_powerCell.TryUseCharge(harness.Owner, draw.UseRate * args.Ammo.Count, user: args.User))
            return;

        _harnessSupport.RefreshHeldSupWeap(args.User);
        UpdateHarnAlert(harness, args.User, true);
    }

    private void OnSupWeapDrop(Entity<ReqWeapHarnComponent> ent, ref DroppedEvent args)
    {
        if (args.Handled ||
            !TryGetMagnetHarn(args.User, ent.Comp.SupportKey, out _) ||
            !CanMagnetRetrieve(ent.Owner))
            return;

        var gun = ent.Owner;
        var user = args.User;
        var supportKey = ent.Comp.SupportKey;

        Timer.Spawn(0, () => TryRetrieveDropSupWeap(gun, user, supportKey));
    }

    private void TryRetrieveDropSupWeap(EntityUid gun, EntityUid user, string supportKey)
    {
        if (Deleted(gun) ||
            Deleted(user) ||
            !TryGetMagnetHarn(user, supportKey, out var harness) ||
            !CanMagnetRetrieve(gun) ||
            !_harnessSupport.TryGetSlotName(user, harness.Comp.RetrievalSlot, out var retrievalSlot) ||
            _inventory.TryGetSlotEntity(user, retrievalSlot, out _))
            return;

        if (!_inventory.TryEquip(user, user, gun, retrievalSlot, silent: true, force: true))
            return;

        _harnessSupport.RefreshHeldSupWeap(user);
    }

    private void OnMobStateChanged(MobStateChangedEvent args)
    {
        if (args.NewMobState is not (MobState.Critical or MobState.Dead))
            return;

        TryRetrieveHeldSuppWeap(args.Target);
    }

    private void TryRetrieveHeldSuppWeap(EntityUid user)
    {
        if (!TryGetHarness(user, out _, out var harness) ||
            !TryGetMagnetHarn(user, harness.SupportKey, out _) ||
            !_harnessSupport.TryGetSlotName(user, harness.RetrievalSlot, out var retrievalSlot) ||
            _inventory.TryGetSlotEntity(user, retrievalSlot, out _))
            return;

        foreach (var held in _hands.EnumerateHeld(user))
        {
            if (!TryComp<ReqWeapHarnComponent>(held, out var support) ||
                support.SupportKey != harness.SupportKey ||
                !CanMagnetRetrieve(held))
            {
                continue;
            }

            if (!_inventory.TryEquip(user, user, held, retrievalSlot, silent: true, force: true))
                return;

            _harnessSupport.RefreshHeldSupWeap(user);
            return;
        }
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
            if (!_harnessSupport.TryGetPowHarnEntity(wearer, harness.SupportKey, out var poweredHarness) ||
                poweredHarness.Owner != uid ||
                !_harnessSupport.HasSupWeapInHandOrRetrievalSlot(wearer, harness.SupportKey))
            {
                continue;
            }

            var charge = harness.ActiveChargePerSecond * (float) ActiveDrainDelay.TotalSeconds;
            if (!_powerCell.TryUseCharge(uid, charge))
                _harnessSupport.RefreshHeldSupWeap(wearer);

            UpdateHarnAlert((uid, harness), wearer, true);
        }
    }

    private void OnSupWeapEquipHand(WeapHarnGunEquipEvent args)
    {
        if (!TryComp<ReqWeapHarnComponent>(args.Gun, out var support))
            return;

        var showFeedback = !_suppressNextLinkFeedback.Remove(args.Gun);
        TryLinkHarness(args.User, support.SupportKey, showFeedback, showFeedback);
    }

    private void OnSupWeapUnequipHand(WeapHarnGunUnEquipEvent args)
    {
        if (!TryGetHarness(args.User, out var harnessUid, out var harness) ||
            _harnessSupport.HasSupWeapInHandOrRetrievalSlot(args.User, harness.SupportKey))
            return;

        harness.LinkSoundPlayed = false;
    }

    private void OnSupWeapUnequipInv(WeapHarnGunUnEquipInvEvent args)
    {
        if (TryGetHarness(args.User, out _, out var harness) &&
            _harnessSupport.TryGetSlotName(args.User, harness.RetrievalSlot, out var retrievalSlot) &&
            args.Slot == retrievalSlot)
            _suppressNextLinkFeedback.Add(args.Gun);
    }

    private void OnHarnEquip(WeapHarnEquipEvent args)
    {
        if (!TryComp<WeapHarnComponent>(args.Harness, out var harness) ||
            !_inventory.TryGetSlot(args.User, args.Slot, out var slot) ||
            (slot.SlotFlags & harness.HarnessSlot) == 0)
            return;

        TryLinkHarness(args.User, harness.SupportKey, true, false);
        UpdateHarnAlert((args.Harness, harness), args.User, true);
    }

    private void OnHarnUnequip(WeapHarnUnequipEvent args)
    {
        if (!TryComp<WeapHarnComponent>(args.Harness, out var harness) ||
            !_inventory.TryGetSlot(args.User, args.Slot, out var slot) ||
            (slot.SlotFlags & harness.HarnessSlot) == 0)
            return;

        ClearHarnAlert(args.User, harness);
        ResetHarnWarn(harness);
    }

    private void OnHarnPowerCellChange(WeapHarnPowerCellChangeEvent args)
    {
        if (!TryComp<WeapHarnComponent>(args.Harness, out var harness))
            return;

        if (!TryGetHarnWearer(args.Harness, out var wearer))
        {
            ResetHarnWarn(harness);
            return;
        }

        UpdateHarnAlert((args.Harness, harness), wearer, true);
    }

    private void OnHarnGetVerbs(Entity<WeapHarnComponent> ent, ref GetVerbsEvent<Verb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;

        if (TryGetHarnWearer(ent.Owner, out var wearer) && wearer != args.User)
            return;

        var user = args.User;

        args.Verbs.Add(new Verb
        {
            Text = ent.Comp.MagneticRetrievalEnabled
                ? ent.Comp.DisableMagneticRetrievalVerb
                : ent.Comp.EnableMagneticRetrievalVerb,
            Priority = 2,
            Act = () => ToggleMagnetRetrieve(ent, user),
        });
    }

    private void ToggleMagnetRetrieve(Entity<WeapHarnComponent> ent, EntityUid user)
    {
        ent.Comp.MagneticRetrievalEnabled = !ent.Comp.MagneticRetrievalEnabled;

        var message = ent.Comp.MagneticRetrievalEnabled
            ? ent.Comp.MagneticRetrievalEnabledPopup
            : ent.Comp.MagneticRetrievalDisabledPopup;

        _popup.PopupEntity(message, ent.Owner, user, PopupType.Medium);
    }

    private void TryLinkHarness(EntityUid user, string supportKey, bool showPopup, bool playSound)
    {
        if (!_harnessSupport.TryGetPowHarnEntity(user, supportKey, out var harness) ||
            !_harnessSupport.HasSupWeapInHandOrRetrievalSlot(user, supportKey))
            return;

        if (!harness.Comp.LinkSoundPlayed)
        {
            if (playSound)
                PlayHarnSound(harness.Comp.LinkSound, user);

            if (showPopup && !string.IsNullOrEmpty(harness.Comp.LinkPopup))
                _popup.PopupEntity(harness.Comp.LinkPopup, user, user, PopupType.Medium);

            harness.Comp.LinkSoundPlayed = true;
        }

        UpdateHarnAlert(harness, user, true);
    }

    private bool TryGetMagnetHarn(
        EntityUid user,
        string supportKey,
        out Entity<WeapHarnComponent> harness)
    {
        return _harnessSupport.TryGetPowHarnEntity(user, supportKey, out harness) &&
               harness.Comp.MagneticRetrievalEnabled;
    }

    private bool CanMagnetRetrieve(EntityUid uid)
    {
        return _tag.HasTag(uid, HeavyWeaponTag);
    }

    private void UpdateHarnAlert(Entity<WeapHarnComponent> ent, EntityUid wearer, bool playSounds)
    {
        if (!_powerCell.TryGetBatteryFromSlot(ent.Owner, out var battery))
        {
            ClearHarnAlert(wearer, ent.Comp);
            ResetHarnWarn(ent.Comp);
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
                PlayHarnSound(ent.Comp.DepletedSound, wearer);

            ent.Comp.HalfChargeWarned = true;
            ent.Comp.DepletedWarned = true;
            return;
        }

        if (low)
        {
            _alerts.ClearAlert(wearer, ent.Comp.DepletedAlert);
            _alerts.ShowAlert(wearer, ent.Comp.LowPowerAlert);

            if (playSounds && !ent.Comp.HalfChargeWarned)
                PlayHarnSound(ent.Comp.HalfChargeSound, wearer);

            ent.Comp.HalfChargeWarned = true;
            ent.Comp.DepletedWarned = false;
            return;
        }

        ClearHarnAlert(wearer, ent.Comp);
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

        var enumerator = _inventory.GetSlotEnumerator(user);
        while (enumerator.NextItem(out var item, out var slot))
        {
            if (!TryComp<WeapHarnComponent>(item, out var harnessComp) ||
                (slot.SlotFlags & harnessComp.HarnessSlot) == 0)
                continue;

            harnessUid = item;
            harness = harnessComp;
            return true;
        }

        return false;
    }

    private bool TryGetHarnWearer(EntityUid harnessUid, out EntityUid wearer)
    {
        wearer = Transform(harnessUid).ParentUid;
        return TryComp<WeapHarnComponent>(harnessUid, out var harness) &&
               _inventory.TryGetContainingSlot(harnessUid, out var slot) &&
               (slot.SlotFlags & harness.HarnessSlot) != 0;
    }

    private void ClearHarnAlert(EntityUid wearer, WeapHarnComponent harness)
    {
        _alerts.ClearAlert(wearer, harness.LowPowerAlert);
        _alerts.ClearAlert(wearer, harness.DepletedAlert);
    }

    private static void ResetHarnWarn(WeapHarnComponent harness)
    {
        harness.HalfChargeWarned = false;
        harness.DepletedWarned = false;
        harness.LinkSoundPlayed = false;
    }

    private void PlayHarnSound(SoundSpecifier? sound, EntityUid user)
    {
        if (sound == null)
            return;

        _audio.PlayEntity(sound, Filter.Empty().FromEntities(user), user, false);
    }
}
