using Content.Shared._DV.CCVars;
using Content.Shared.Access.Systems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Coordinates;
using Content.Shared.Humanoid;
using Content.Shared.Mind;
using Content.Shared.Popups;
using Content.Shared.Whitelist;
using Robust.Shared.ColorNaming;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Shared._Triad.ContrabandPermit;

public abstract partial class SharedContrabandPermitSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototype = default!;
    [Dependency] private ILocalizationManager _localization = default!;
    [Dependency] private SharedUserInterfaceSystem _userInterface = default!;
    [Dependency] private ItemSlotsSystem _itemSlot = default!;
    [Dependency] private INetManager _net = default!;
    [Dependency] private AccessReaderSystem _accessReader = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedMindSystem _mind = default!;
    [Dependency] private EntityWhitelistSystem _whitelist = default!;
    [Dependency] private IConfigurationManager _config = default!;

    private static readonly int MinimumPermitReasonLength = 5;
    private static readonly int MinimumRevokeReasonLength = 5;

    private static DateTime _serverDate;

    private void InitializeConsole()
    {
        SubscribeLocalEvent<ContrabandPermitConsoleComponent, MapInitEvent>(OnConsoleMapInit);
        SubscribeLocalEvent<ContrabandPermitConsoleComponent, EntInsertedIntoContainerMessage>(OnConsoleContainerUpdated);
        SubscribeLocalEvent<ContrabandPermitConsoleComponent, EntRemovedFromContainerMessage>(OnConsoleContainerUpdated);

        SubscribeLocalEvent<ContrabandPermitConsoleComponent, ContrabandPermitConsoleReasonUpdatedMessage>(OnConsoleReasonChanged);
        SubscribeLocalEvent<ContrabandPermitConsoleComponent, ContrabandPermitConsoleGrantButtonPressedMessage>(OnConsoleGrantPressed);
        SubscribeLocalEvent<ContrabandPermitConsoleComponent, ContrabandPermitConsoleRevokeButtonPressedMessage>(OnConsoleRevokePressed);
        SubscribeLocalEvent<ContrabandPermitConsoleComponent, ContrabandPermitConsolePrintButtonPressedMessage>(OnConsolePrintPressed);
        SubscribeLocalEvent<ContrabandPermitConsoleComponent, ContrabandPermitConsoleFocusChangeMessage>(OnConsoleFocusChanged);

        Subs.CVar(_config, DCCVars.YearOffset, value => _serverDate = DateTime.Today.AddYears(value), true);
    }

    private void OnConsoleMapInit(Entity<ContrabandPermitConsoleComponent> ent, ref MapInitEvent args)
    {
        UpdateUserInterface(ent.Owner, ent.Comp);
    }

    private void OnConsoleContainerUpdated(EntityUid uid, ContrabandPermitConsoleComponent component, EntityEventArgs args)
    {
        UpdateUserInterface(uid, component);
    }

    protected void UpdateUserInterface(EntityUid uid, ContrabandPermitConsoleComponent component)
    {
        if (!component.Initialized)
            return;

        if (!_itemSlot.TryGetSlot(uid, component.ChipSlotContainerId, out var itemSlot))
            return;

        ContrabandPermitConsoleBuiState? newState;

        var dateString = _serverDate.ToString("dd MMMM yyyy");

        if (itemSlot.Item is { } targetChip && TryComp<ContrabandPermitChipComponent>(targetChip, out var chipComp))
        {
            var permitOwner = GetEntity(chipComp.ScannedPermitCarrier);
            var permitItem = GetEntity(chipComp.ScannedItem);

            ContrabandPermitConsoleBuiStateOwnerInfo? ownerInfo = null;

            // Get permit owner information
            if (permitOwner != null && TryComp<HumanoidAppearanceComponent>(permitOwner, out var appearanceComp))
            {
                var ownerMeta = MetaData(permitOwner.Value);

                var species = _prototype.Index(appearanceComp.Species);
                var speciesName = Loc.GetString(species.Name);

                var colorName = ColorNaming.Describe(appearanceComp.EyeColor, _localization);

                if (ownerMeta != null)
                    ownerInfo = new(ownerMeta.EntityName, speciesName, appearanceComp.Age, appearanceComp.Gender, colorName);
            }

            EntProtoId? itemProtoId = null;

            // Get permit metadata so we know which prototype to show in the console UI
            if (permitItem != null)
            {
                var itemMeta = MetaData(permitItem.Value);

                if (itemMeta != null)
                    itemProtoId = itemMeta.EntityPrototype?.ID;
            }

            var chipNetEnt = GetNetEntity(targetChip);
            newState = new ContrabandPermitConsoleBuiState(chipNetEnt, dateString, itemProtoId, ownerInfo, component.Entries, component.FocusedEntry);
        }
        else
        {
            newState = new ContrabandPermitConsoleBuiState(null, null, null, null, component.Entries, component.FocusedEntry);
        }

        _userInterface.SetUiState(uid, ContrabandPermitConsoleUi.Key, newState);
    }

    private void OnConsoleReasonChanged(Entity<ContrabandPermitConsoleComponent> ent, ref ContrabandPermitConsoleReasonUpdatedMessage args)
    {
        var finalReason = args.Reason.Trim();
        SetConsolePermitReason(ent, finalReason);
    }

    private void OnConsoleGrantPressed(Entity<ContrabandPermitConsoleComponent> ent, ref ContrabandPermitConsoleGrantButtonPressedMessage args)
    {
        if (!_itemSlot.TryGetSlot(ent.Owner, ent.Comp.ChipSlotContainerId, out var itemSlot))
            return;

        if (itemSlot.Item is not { } insertedChip)
            return;

        if (!TryComp<ContrabandPermitChipComponent>(insertedChip, out var chipComp))
            return;

        var user = args.Actor;

        if (!_whitelist.CheckBoth(user, ent.Comp.GrantPermitBlacklist, ent.Comp.GrantPermitWhitelist))
        {
            PlayDenySound(ent, user);
            ConsolePopup(user, Loc.GetString("contraband-permit-console-popup-error-access-denied"), PopupType.SmallCaution);
            return;
        }

        var scannedItem = GetEntity(chipComp.ScannedItem);
        var scannedPermitCarrier = GetEntity(chipComp.ScannedPermitCarrier);

        if (scannedItem == null || scannedPermitCarrier == null
            || TerminatingOrDeleted(scannedItem.Value) || TerminatingOrDeleted(scannedPermitCarrier.Value))
        {
            PlayDenySound(ent, user);
            ConsolePopup(user, Loc.GetString("contraband-permit-console-popup-error-no-data"), PopupType.SmallCaution);
            return;
        }

        if (HasComp<ContrabandPermitItemComponent>(scannedItem))
        {
            PlayDenySound(ent, user);
            ConsolePopup(user, Loc.GetString("contraband-permit-console-popup-error-already-permit"), PopupType.SmallCaution);
            return;
        }

        if (!TryComp<ContrabandPermittableComponent>(scannedItem, out var permittable) || !permittable.Permittable)
            return;

        var permitReason = ent.Comp.CurrentPermitReason;

        if (permitReason == string.Empty || permitReason.Length < MinimumPermitReasonLength)
        {
            PlayDenySound(ent, user);
            ConsolePopup(user, Loc.GetString("contraband-permit-console-popup-reason-too-short"), PopupType.SmallCaution);
            return;
        }

        var dateString = _serverDate.ToString("dd MMMM yyyy");

        var permit = EnsureComp<ContrabandPermitItemComponent>(scannedItem.Value);
        permit.PermitReason = permitReason;
        permit.DateGranted = dateString;
        permit.PermitOwnerName = Name(scannedPermitCarrier.Value);
        permit.PermitOwner = scannedPermitCarrier;

        if (TryComp<ActorComponent>(scannedPermitCarrier, out var actor)
            && _mind.TryGetMind(actor.PlayerSession, out var mindId, out _))
        {
            permit.PermitOwnerMind = mindId;
        }

        Dirty(scannedItem.Value, permit);

        PlayConfirmSound(ent, user);
        ConsolePopup(user,
            Loc.GetString("contraband-permit-console-popup-success", ("item", scannedItem), ("owner", permit.PermitOwnerName)),
            PopupType.Medium);

        if (_itemSlot.TryEject(ent.Owner, ent.Comp.ChipSlotContainerId, user, out var ejected))
            PredictedQueueDel(ejected);

        var ev = new ContrabandPermitGrantedEvent(scannedItem.Value, scannedPermitCarrier.Value, ent.Owner, user, ent.Comp.CurrentPermitReason);
        RaiseLocalEvent(scannedItem.Value, ev, true);

        SetConsolePermitReason(ent, string.Empty);
    }

    private void OnConsoleRevokePressed(Entity<ContrabandPermitConsoleComponent> ent, ref ContrabandPermitConsoleRevokeButtonPressedMessage args)
    {
        var user = args.Actor;

        if (!_accessReader.IsAllowed(ent.Owner, user))
        {
            PlayDenySound(ent, args.Actor);
            ConsolePopup(args.Actor, Loc.GetString("contraband-permit-console-popup-error-access-denied"), PopupType.SmallCaution);
            return;
        }

        var reason = args.Reason.Trim();

        if (reason.Length < MinimumRevokeReasonLength)
        {
            PlayDenySound(ent, args.Actor);
            ConsolePopup(args.Actor, Loc.GetString("contraband-permit-console-popup-revoke-reason-too-short"), PopupType.SmallCaution);
            return;
        }

        if (ent.Comp.FocusedEntry == null || ent.Comp.FocusedEntry.Value.SelectedItem is not { } selectedItem)
        {
            PlayDenySound(ent, args.Actor);
            ConsolePopup(args.Actor, Loc.GetString("contraband-permit-console-popup-revoke-no-focus"), PopupType.SmallCaution);
            return;
        }

        var selectedEnt = GetEntity(selectedItem.Item);

        if (!TryComp<ContrabandPermitItemComponent>(selectedEnt, out var permitInfo) || permitInfo.PermitOwner == null)
            return;

        var permitOwner = permitInfo.PermitOwner.Value;
        var permitOwnerName = permitInfo.PermitOwnerName;

        PlayConfirmSound(ent, user);
        ConsolePopup(user,
            Loc.GetString("contraband-permit-console-popup-success-revoke", ("item", selectedEnt), ("owner", permitOwnerName)),
            PopupType.Medium);

        // Update the focus
        if (ent.Comp.FocusedEntry is { } focusedEntry)
        {
            var lastEntry = false;

            foreach (var entry in ent.Comp.Entries)
            {
                if (entry.Owner.Owner != focusedEntry.PermitOwner.Owner)
                    continue;

                if (entry.Items.Count <= 1)
                    lastEntry = true;

                break;
            }

            // If there's more than one entry left by this owner
            if (!lastEntry)
            {
                var focusData = new PermitEntryFocusData(focusedEntry.PermitOwner, null);
                UpdateFocusData(ent, focusData);
            }
            else
            {
                UpdateFocusData(ent, null);
            }
        }

        var ev = new ContrabandPermitRevokedEvent(selectedEnt, permitOwner, ent.Owner, user, reason);
        RaiseLocalEvent(selectedEnt, ev, true);
    }

    private void OnConsolePrintPressed(Entity<ContrabandPermitConsoleComponent> ent, ref ContrabandPermitConsolePrintButtonPressedMessage args)
    {
        var curTime = _timing.CurTime;
        var user = args.Actor;

        if (curTime < ent.Comp.PrintChipTimeoutEnd)
        {
            var timeRemaining = (ent.Comp.PrintChipTimeoutEnd - curTime).Seconds;
            _popup.PopupClient(Loc.GetString("contraband-permit-console-print-chip-cooldown", ("time", timeRemaining)), user, PopupType.MediumCaution);
            return;
        }

        PredictedSpawnAtPosition(ent.Comp.ChipPrototype, ent.Owner.ToCoordinates());
        _audio.PlayPredicted(ent.Comp.ChipPrintSound, ent.Owner, user);

        ent.Comp.PrintChipTimeoutEnd = curTime + ent.Comp.PrintChipTimeout;
        Dirty(ent);
    }

    private void OnConsoleFocusChanged(Entity<ContrabandPermitConsoleComponent> ent, ref ContrabandPermitConsoleFocusChangeMessage args)
    {
        if (args.FocusedOwner == null)
        {
            UpdateFocusData(ent, null);
        }
        else
        {
            var focusData = new PermitEntryFocusData(args.FocusedOwner.Value, args.FocusedItem);
            UpdateFocusData(ent, focusData);
        }
    }

    private void UpdateFocusData(Entity<ContrabandPermitConsoleComponent> ent, PermitEntryFocusData? entry)
    {
        ent.Comp.FocusedEntry = entry;
        Dirty(ent);
        UpdateUserInterface(ent.Owner, ent.Comp);
    }

    private void PlayConfirmSound(Entity<ContrabandPermitConsoleComponent> ent, EntityUid? user)
    {
        _audio.PlayPredicted(ent.Comp.ConfirmSound, ent.Owner, user);
    }

    private void PlayDenySound(Entity<ContrabandPermitConsoleComponent> ent, EntityUid? user)
    {
        _audio.PlayPredicted(ent.Comp.ErrorSound, ent.Owner, user);
    }

    private void ConsolePopup(EntityUid actor, string text, PopupType type = PopupType.Small)
    {
        if (_net.IsClient)
            return;

        if (actor is { Valid: true } player)
            _popup.PopupEntity(text, player, type);
    }

    private void SetConsolePermitReason(Entity<ContrabandPermitConsoleComponent> ent, string reason)
    {
        ent.Comp.CurrentPermitReason = reason;
        Dirty(ent);
    }

    public record struct ContrabandPermitGrantedEvent(EntityUid PermitEntity, EntityUid PermitOwner, EntityUid? Console, EntityUid? PermitGranter, string Reason);
    public record struct ContrabandPermitRevokedEvent(EntityUid PermitEntity, EntityUid PermitOwner, EntityUid? Console, EntityUid? PermitRevoker, string Reason);
}
