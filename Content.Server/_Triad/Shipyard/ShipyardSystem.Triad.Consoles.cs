using System.Linq;
using Content.Server._NF.Shipyard.Components;
using Content.Server._NF.Station.Components;
using Content.Server._Triad.Shipyard;
using Content.Server.Database;
using Content.Server.Maps;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.StationEvents.Components;
using Content.Server.StationRecords;
using Content.Shared._Mono.Company;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared._NF.ShuttleRecords;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Shipyard.Save;
using Content.Shared.Access.Components;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.Forensics.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Preferences;
using Content.Shared.Radio;
using Content.Shared.Shuttles.Components;
using Content.Shared.Station.Components;
using Content.Shared.StationRecords;
using Robust.Shared.Player;

namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem : SharedShipyardSystem
{
    [Dependency] private readonly ShuttleConsoleSystem _shuttleConsole = default!;
    [Dependency] private readonly TriadTamperPolicyService _tamperPolicy = default!;

    public void OnSaveMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleSaveMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-idcard"));
            PlayDenySound(player, uid, component);
            return;
        }

        if (!TryComp<IdCardComponent>(targetId, out var idCard))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-invalid-idcard"));
            PlayDenySound(player, uid, component);
            return;
        }

        if (!TryComp<ShuttleDeedComponent>(targetId, out var deed))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-deed"));
            PlayDenySound(player, uid, component);
            return;
        }

        // Get player session from mind component
        if (!_mind.TryGetMind(player, out var mindUid, out var mindComp) || mindComp.UserId == null)
        {
            ConsolePopup(player, "Unable to save ship - player session not found");
            PlayDenySound(player, uid, component);
            return;
        }

        var playerSession = _player.GetSessionById(mindComp.UserId.Value);
        if (playerSession == null)
        {
            ConsolePopup(player, "Unable to save ship - player session not found");
            PlayDenySound(player, uid, component);
            return;
        }

        var shuttleUid = deed.ShuttleUid;
        var voucherUsed = deed.PurchasedWithVoucher;

        if (shuttleUid == null)
        {
            ConsolePopup(player, "Unable to save ship - grid not found");
            PlayDenySound(player, uid, component);
            return;
        }

        // Ensure the limits for limited entites doesn't exceed while saving
        if (!_shipyardGridSave.CheckGridEntityLimits(shuttleUid.Value, out var message))
        {
            ConsolePopup(player, message);
            PlayDenySound(player, uid, component);
            return;
        }

        if (voucherUsed)
        {
            ConsolePopup(player, $"Failed to store ship due to the usage of voucher.");
            PlayDenySound(player, uid, component);
            return;
        }

        var mobQuery = GetEntityQuery<MobStateComponent>();
        var xformQuery = GetEntityQuery<TransformComponent>();

        var foundOrganic = FoundOrganics(shuttleUid.Value, mobQuery, xformQuery);
        if (foundOrganic != null)
        {
            ConsolePopup(player, $"Failed to store ship; {foundOrganic} detected on board.");
            PlayDenySound(player, uid, component);
            return;
        }

        // Attempt to save the ship
        if (!_shipyardGridSave.TrySaveShip(shuttleUid.Value, targetId, playerSession))
        {
            ConsolePopup(player, $"Failed to store ship {deed.ShuttleName}.");
            PlayDenySound(player, uid, component);
            return;
        }

        ConsolePopup(player, $"Storing ship {deed.ShuttleName} at shipyard. Have a nice day!");
        PlayConfirmSound(player, uid, component);

        var name = GetFullName(deed);
        SendSaveMessage(uid, deed.ShuttleOwner!, name, component.ShipyardChannel, player, secret: false);
        if (component.SecretShipyardChannel is { } secretChannel)
            SendSaveMessage(uid, deed.ShuttleOwner!, name, secretChannel, player, secret: true);

        // Refresh UI with current deed info and player's balance
        int balance = 0;
        if (TryComp<BankAccountComponent>(player, out var bankAcc))
            balance = bankAcc.Balance;

        RefreshState(uid, balance, true, null, 0, targetId, (ShipyardConsoleUiKey)args.UiKey, voucherUsed);
    }

    public void OnLoadMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleLoadMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-idcard"));
            PlayDenySound(player, uid, component);
            return;
        }

        if (HasComp<ShipyardVoucherComponent>(targetId))
        {
            ConsolePopup(player, "Error: Stored ships cannot be called in with vouchers.");
            PlayDenySound(player, uid, component);
            return;
        }

        if (_station.GetOwningStation(uid) is not { Valid: true } station)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-invalid-station"));
            PlayDenySound(player, uid, component);
            return;
        }

        TryComp<IdCardComponent>(targetId, out var idCard);
        if (idCard is null)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-idcard"));
            PlayDenySound(player, uid, component);
            return;
        }

        if (HasComp<ShuttleDeedComponent>(targetId))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-already-deeded"));
            PlayDenySound(player, uid, component);
            return;
        }

        if (TryComp<AccessReaderComponent>(uid, out var accessReaderComponent) && !_access.IsAllowed(player, uid, accessReaderComponent))
        {
            ConsolePopup(player, Loc.GetString("comms-console-permission-denied"));
            PlayDenySound(player, uid, component);
            return;
        }

        if (!_mind.TryGetMind(player, out _, out var tamperMind) || tamperMind.UserId == null)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-load-failed"));
            PlayDenySound(player, uid, component);
            return;
        }

        if (!_player.TryGetSessionByEntity(player, out var playerSession))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-load-failed"));
            PlayDenySound(player, uid, component);
            return;
        }

        // F1 fix - empty YamlData is never a legitimate load. Legitimate clients
        // always send YAML bytes alongside SourceFilePath (see Content.Client/_NF/Shipyard/BUI/
        // ShipyardConsoleBoundUserInterface.cs). An empty payload means a modded client is
        // trying to skip tamper evaluation and reach the SourceFilePath disk-load fallback.
        // Reject hard and write a forensic audit row.
        if (string.IsNullOrWhiteSpace(args.YamlData))
        {
            _ = _tamperPolicy.RecordRejectedLoadAsync(
                tamperMind.UserId!.Value, playerSession.Name, args.SourceFilePath, _gameTicker.RoundId > 0 ? _gameTicker.RoundId : null);
            ConsolePopup(player, Loc.GetString("shipyard-tamper-blocked-empty-payload"));
            PlayDenySound(player, uid, component);
            return;
        }

        var authShip = AuthenticatedShipFile.FromShipFile(args.YamlData);
        // Resolve the ship name up front with the SAME tiers the load below uses (YAML name
        // -> source filename -> generated fallback), so the audit rows and EvaluateLoad record the
        // exact name the ship loads under rather than null. Re-signing in the migration branch leaves
        // the ship data unchanged, so this value holds for the migrated envelope too.
        var loadShipName = authShip.ShipYamlString() is { } loadYaml ? ExtractShipNameFromYaml(loadYaml) : null;
        if (string.IsNullOrWhiteSpace(loadShipName) && !string.IsNullOrWhiteSpace(args.SourceFilePath))
        {
            try { loadShipName = System.IO.Path.GetFileNameWithoutExtension(args.SourceFilePath); }
            catch { loadShipName = null; }
        }
        loadShipName ??= $"LoadedShip_{DateTime.Now:yyyyMMdd_HHmmss}";
        LoadDecision decision;
        try
        {
            decision = _tamperPolicy.EvaluateLoad(authShip, tamperMind.UserId!.Value, loadShipName);
        }
        catch (Exception ex)
        {
            // F6 fix (belt-and-braces): even with IsShipSigned guarded against malformed pubkeys,
            // an unexpected exception elsewhere in EvaluateLoad should not crash this network
            // handler. Treat as the existing 'hard reject for invalid signature' decision so the
            // standard rejection path (audit + popup + deny sound + return) handles it cleanly.
            _sawmill.Warning($"EvaluateLoad threw {ex.GetType().Name}; treating as invalid signature: {ex.Message}");
            decision = new LoadDecision(false, TriadShipyardEventType.LoadRejectedInvalidSignature, "shipyard-tamper-blocked-invalid-signature");
        }

        if (!decision.Allow)
        {
            _ = _tamperPolicy.RecordLoadAsync(
                authShip, tamperMind.UserId!.Value, playerSession.Name,
                shipName: loadShipName,
                decision.ResolvedEvent,
                loadTimeAppraisal: null,
                roundId: _gameTicker.RoundId > 0 ? _gameTicker.RoundId : null, serverName: null,
                vesselId: null, mapId: null,
                sourceFilePath: args.SourceFilePath,
                deedHolderEntity: null);

            ConsolePopup(player, Loc.GetString(decision.PopupReasonLocId ?? "shipyard-console-load-blocked-tamper"));
            PlayDenySound(player, uid, component);
            return;
        }

        // Migration branch: a permitted player loading a non-trusted ship. Re-sign with the
        // active key so the audit row + load path see the migrated envelope, but DEFER the
        // network push until TryPurchaseShuttleFromYamlData succeeds. If the load fails after
        // this point, the client keeps their original envelope on disk and can retry without
        // having lost their authoritative copy. Appraisal is an advisory display hint
        // (see AuthenticatedShipFile._appraisal comment); pass the original value through so
        // the migrated envelope's display matches the source rather than zeroing out.
        string? migratedEnvelopeToPush = null;
        if (decision.ResolvedEvent == TriadShipyardEventType.LoadMigrated
            && !string.IsNullOrWhiteSpace(args.SourceFilePath))
        {
            var migrated = _tamperPolicy.ReSignForMigration(authShip, authShip.Appraisal ?? 0);
            migratedEnvelopeToPush = migrated.ShipFileString();
            authShip = migrated;
        }

        _ = _tamperPolicy.RecordLoadAsync(
            authShip, tamperMind.UserId!.Value, playerSession.Name,
            shipName: loadShipName,
            decision.ResolvedEvent,
            loadTimeAppraisal: null,
            roundId: _gameTicker.RoundId > 0 ? _gameTicker.RoundId : null, serverName: null,
            vesselId: null, mapId: null,
            sourceFilePath: args.SourceFilePath,
            deedHolderEntity: null);

        var shipYaml = authShip.ShipYamlString();
        // End Triad



        // Triad: reuse the name resolved up front (above) so the audit row and the spawned ship
        // share one name instead of recomputing (which could diverge on the generated fallback).
        var name = loadShipName;

        // Attempt to load the shuttle from the in-message YAML only.
        // Triad: F1 fix - removed the SourceFilePath disk-load fallback, which bypassed
        // tamper protection by loading whatever path the client named under /UserData.
        // The YAML path above already runs through compatibility recovery; if it fails,
        // the load fails. SourceFilePath stays in scope only as audit-row / migration metadata.
        EntityUid? shuttleUidOut = null;
        bool loaded = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(shipYaml))
            {
                loaded = TryPurchaseShuttleFromYamlData(uid, shipYaml, out shuttleUidOut);
            }
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Error while attempting to load shuttle from YAML data: {ex}");
            loaded = false;
        }

        if (!loaded || shuttleUidOut is null)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-load-failed"));
            PlayDenySound(player, uid, component);
            return;
        }

        var shuttleUid = shuttleUidOut.Value;
        if (!TryComp<ShuttleComponent>(shuttleUid, out _))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-load-failed"));
            PlayDenySound(player, uid, component);
            return;
        }

        // Calculate appraisal cost for the loaded ship from cvar loadShipPrice
        var loadShipPrice = _configManager.GetCVar(TriadCCVars.LoadShipPrice);
        var fullAppraisal = _pricing.AppraiseGrid(shuttleUid, null);
        var appraisalCost = (int)MathF.Round((float)fullAppraisal * loadShipPrice);

        // Check if player has a bank account and session to charge them
        // Triad: playerSession is captured earlier (above tamper-protection block)
        if (!TryComp<BankAccountComponent>(player, out var bankAccount))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-bank"));
            PlayDenySound(player, uid, component);
            return;
        }

        var currentBalance = bankAccount.Balance;
        var newBalance = currentBalance - appraisalCost;

        // Force charge the player - allow going into debt
        if (!_bank.TryBankWithdrawAllowDebt(player, appraisalCost))
        {
            // This should rarely happen (only if no session/prefs/etc)
            ConsolePopup(player, Loc.GetString("shipyard-console-load-failed"));
            PlayDenySound(player, uid, component);
            return;
        }

        // Notify player of the charge and their new balance
        if (newBalance < 0)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-load-success-debt",
                ("ship", name), ("cost", appraisalCost), ("debt", -newBalance)));
        }
        else
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-load-success-charged",
                ("ship", name), ("cost", appraisalCost)));
        }

        // Add company information to the shuttle from the ID card or voucher
        AddCompanyInformation(targetId, shuttleUid); // Triad, generic method for adding company info

        var boughtEv = new ShipBoughtEvent();
        RaiseLocalEvent(shuttleUid, boughtEv);

        // Important: Treat loaded ships like independent shuttles, not part of the console's station.
        // The purchase-from-file path temporarily adds the grid to the console's station for IFF/ownership.
        // That causes station-wide events (alerts, etc.) to target the loaded ship. Remove that membership here.
        try
        {
            var consoleStation = _station.GetOwningStation(uid);
            if (consoleStation != null && TryComp<StationMemberComponent>(shuttleUid, out var member)
                && member.Station == consoleStation)
            {
                _station.RemoveGridFromStation(consoleStation.Value, shuttleUid);
                _sawmill.Info($"[ShipLoad(Console)] Removed station membership from loaded ship {ToPrettyString(shuttleUid)} (station {ToPrettyString(consoleStation.Value)})");
            }
        }
        catch (Exception rmEx)
        {
            _sawmill.Warning($"[ShipLoad(Console)] Failed to remove station membership from {ToPrettyString(shuttleUid)}: {rmEx.Message}");
        }

        // For loaded ships, we don't spawn a new station via a GameMap prototype unless we can infer the vessel ID.
        var vesselComp = EnsureComp<VesselComponent>(shuttleUid);
        var vessel = vesselComp.VesselId;

        EntityUid? shuttleStation = null;
        if (_prototypeManager.TryIndex<GameMapPrototype>(vessel, out var stationProto))
        {
            List<EntityUid> gridUids = new()
            {
                shuttleUid
            };
            name = Name(shuttleUid); // Name the station to the shuttle's name
            shuttleStation = _station.InitializeNewStation(stationProto.Stations[vessel], gridUids, name);

            var vesselInfo = EnsureComp<ExtraShuttleInformationComponent>(shuttleStation.Value);
            vesselInfo.Vessel = vessel;
        }

        SetFtlLockEnabled(shuttleUid);
        AddNewShuttleDeedAccessLevels(targetId, component);

        var deedID = EnsureComp<ShuttleDeedComponent>(targetId);

        var shuttleOwner = Name(player).Trim();
        const bool loadedFromSave = true; // mark as voucher-like to prevent resale

        AssignShuttleDeedProperties(deedID, shuttleUid, name, shuttleOwner, false, targetId.ToString(), loadedFromSave);
        deedID.DeedHolder = targetId;

        var deedShuttle = EnsureComp<ShuttleDeedComponent>(shuttleUid);
        AssignShuttleDeedProperties(deedShuttle, shuttleUid, name, shuttleOwner, false, targetId.ToString(), loadedFromSave);

        // Lock all shuttle consoles on the ship to this deed
        var shuttleConsoleQuery = EntityQueryEnumerator<ShuttleConsoleComponent, TransformComponent>();
        while (shuttleConsoleQuery.MoveNext(out var consoleUid, out _, out var transform))
        {
            // Only process consoles on the purchased ship
            if (transform.GridUid != shuttleUid)
                continue;

            // Add lock component and set the shuttle ID
            var lockComp = EnsureComp<ShuttleConsoleLockComponent>(consoleUid);
            _shuttleConsoleLock.SetShuttleId(consoleUid, shuttleUid.ToString(), lockComp);

            // Log for debugging
            Log.Debug("Locked shuttle console {0} to shuttle {1} for deed holder {2}", consoleUid, shuttleUid, targetId);
        }

        // Register ship ownership for auto-deletion when owner is offline too long
        // We need to get the player's session from their entity
        if (TryComp<ActorComponent>(player, out var actorComp) && actorComp.PlayerSession != null)
        {
            _shipOwnership.RegisterShipOwnership(shuttleUid, actorComp.PlayerSession);
        }

        var stationList = EntityQueryEnumerator<StationRecordsComponent>();

        if (TryComp<StationRecordKeyStorageComponent>(targetId, out var keyStorage)
            && shuttleStation != null
            && keyStorage.Key != null)
        {
            bool recSuccess = false;
            while (stationList.MoveNext(out var stationUid, out var stationRecComp))
            {
                if (!_records.TryGetRecord<GeneralStationRecord>(keyStorage.Key.Value, out var record))
                    continue;

                //_records.RemoveRecord(keyStorage.Key.Value);
                _records.AddRecordEntry(shuttleStation.Value, record);
                recSuccess = true;
                break;
            }

            if (!recSuccess
                && _mind.TryGetMind(player, out var mindUid, out var mindComp)
                && mindComp.UserId != null
                && _prefManager.GetPreferences(mindComp.UserId.Value).SelectedCharacter is HumanoidCharacterProfile playerProfile)
            {
                TryComp<FingerprintComponent>(player, out var fingerprintComponent);
                TryComp<DnaComponent>(player, out var dnaComponent);
                TryComp<StationRecordsComponent>(shuttleStation, out var stationRec);

                var fingerprint = fingerprintComponent?.Fingerprint ?? string.Empty;
                var dna = dnaComponent?.DNA ?? string.Empty;

                if (stationRec != null)
                {
                    _records.CreateGeneralRecord(
                        shuttleStation.Value,
                        targetId,
                        playerProfile.Name,
                        playerProfile.Age,
                        playerProfile.Species,
                        playerProfile.Gender,
                        $"Captain",
                        fingerprint,
                        dna,
                        playerProfile,
                        stationRec);
                }
            }
        }
        _records.Synchronize(shuttleStation!.Value);
        _records.Synchronize(station);

        // If we infer a vessel prototype, add any extra components it specifies.
        if (_prototypeManager.TryIndex(vessel, out var vesselProto))
            EntityManager.AddComponents(shuttleUid, vesselProto.AddComponents);

        AddShipAccessToEntities(shuttleUid);

        // Ensure cleanup on ship sale
        EnsureComp<LinkedLifecycleGridParentComponent>(shuttleUid);

        _shipyardDirection.SendShipDirectionMessage(player, shuttleUid);

        // Send radio messages and update UI
        SendPurchaseMessage(uid, player, name, component.ShipyardChannel, secret: false);
        if (component.SecretShipyardChannel is { } secretChannel)
            SendPurchaseMessage(uid, player, name, secretChannel, secret: true);

        PlayConfirmSound(player, uid, component);

        // Optional: show price/sell in UI; for loaded ships, resale is disabled so set 0
        var balance = 0;
        if (TryComp<BankAccountComponent>(player, out var bankAcc2))
            balance = bankAcc2.Balance;

        if (component.CanTransferDeed)
        {
            _shuttleRecordsSystem.AddRecord(
                new ShuttleRecord(
                    name: deedShuttle.ShuttleName ?? "",
                    suffix: deedShuttle.ShuttleNameSuffix ?? "",
                    ownerName: shuttleOwner,
                    entityUid: EntityManager.GetNetEntity(shuttleUid),
                    purchasedWithVoucher: false,
                    loadedFromSave: loadedFromSave,
                    purchasePrice: (uint)(vesselProto?.Price ?? 0)
                )
            );
        }

        var loadEv = new ShipyardShuttleLoadEvent(shuttleUid, player);
        RaiseLocalEvent(loadEv);
        RefreshState(uid, balance, true, name, 0, targetId, (ShipyardConsoleUiKey)args.UiKey, false);

        _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Low, $"{ToPrettyString(player):actor} loaded shuttle {ToPrettyString(shuttleUid)} from {(args.SourceFilePath ?? "YAML data")} via {ToPrettyString(uid)}");

        // After a successful server-side load, push deferred Migrate (if applicable) THEN Delete.
        // Order matters: Migrate overwrites the local file in place; Delete moves the (now-migrated)
        // file to /Exports/backup. Reversing the order would leave the original unsigned envelope
        // in backup and the signed one in /Exports - reasonable but inconsistent with the previous
        // end state, so we keep the existing Migrate-before-Delete ordering.
        if (!string.IsNullOrWhiteSpace(args.SourceFilePath) && _player.TryGetSessionByEntity(player, out var session))
        {
            if (migratedEnvelopeToPush != null)
            {
                RaiseNetworkEvent(
                    new MigrateShipFileMessage(args.SourceFilePath!, migratedEnvelopeToPush),
                    session);
            }
            var deleteEv = new DeleteLocalShipFileMessage(args.SourceFilePath!);
            RaiseNetworkEvent(deleteEv, session);
            _sawmill.Info($"Requested client to delete local ship file '{args.SourceFilePath}' after successful load");
        }
    }

    private void SendSaveMessage(EntityUid uid, string? player, string name, string shipyardChannel, EntityUid saver, bool secret)
    {
        var channel = _prototypeManager.Index<RadioChannelPrototype>(shipyardChannel);

        if (secret)
        {
            _chat.TrySendInGameICMessage(uid, Loc.GetString("shipyard-console-leaving-secret"), InGameICChatType.Speak, true);
        }
        else
        {
            _radio.SendRadioMessage(uid, Loc.GetString("shipyard-console-saved", ("owner", player!), ("vessel", name!), ("player", saver)), channel, uid);
            _chat.TrySendInGameICMessage(uid, Loc.GetString("shipyard-console-saved", ("owner", player!), ("vessel", name!), ("player", saver)), InGameICChatType.Speak, true);
        }
    }

    /// <summary>
    /// Triad - Adds company information from a given id card or voucher onto a shuttle grid entity.
    /// </summary>
    private void AddCompanyInformation(EntityUid idCard, EntityUid shuttleUid)
    {
        // Add company information to the shuttle from the ID card or voucher
        string? companyName = null;

        // First try to get company from ID card
        if (TryComp<IdCardComponent>(idCard, out var idCardCompany) &&
            !string.IsNullOrEmpty(idCardCompany.CompanyName))
        {
            companyName = idCardCompany.CompanyName;
        }
        // If no ID card company, try to get from voucher
        else if (TryComp<ShipyardVoucherComponent>(idCard, out var voucherCompany) &&
                !string.IsNullOrEmpty(voucherCompany.CompanyName))
        {
            companyName = voucherCompany.CompanyName;
        }

        // Apply company to ship if we found one
        if (!string.IsNullOrEmpty(companyName))
        {
            var shipCompany = EnsureComp<CompanyComponent>(shuttleUid);
            shipCompany.CompanyName = companyName;
            Dirty(shuttleUid, shipCompany);
        }
    }

    /// <summary>
    /// Triad - Adds the <see cref="FTLLockComponent"/> to a shuttle grid and sets it to enabled
    /// </summary>
    private void SetFtlLockEnabled(EntityUid shuttleUid)
    {
        // Add FTLLockComponent to the shuttle with Enabled set to true
        // We need to use the ShuttleConsoleSystem to properly set the Enabled property
        EnsureComp<FTLLockComponent>(shuttleUid);

        var dockedEntities = new List<NetEntity>();
        _shuttleConsole.ToggleFTLLock(shuttleUid, dockedEntities, true);
    }

    /// <summary>
    /// Triad - Adds new access levels to a shuttle deed from a <see cref="ShipyardConsoleComponent"/>
    /// </summary>
    private void AddNewShuttleDeedAccessLevels(EntityUid targetId, ShipyardConsoleComponent console)
    {
        if (!TryComp<AccessComponent>(targetId, out var newCap))
            return;

        var newAccess = newCap.Tags.ToList();
        newAccess.AddRange(console.NewAccessLevels);
        _accessSystem.TrySetTags(targetId, newAccess, newCap);
    }
}
