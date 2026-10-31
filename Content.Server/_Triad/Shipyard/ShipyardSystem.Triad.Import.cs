// Triad: legacy import. Drains the old ship-save system into the drydock. Old saves are files on the
// player's own disk, so this is a conversation: the client offers a manifest of what it holds, the
// server says which of those it will accept, and the payload for the one ship being imported crosses
// the wire only when the player presses the button.
//
// Two authorities, and only one follows the tamper mode. The tamper policy decides whether a document
// is trusted enough to load: under enforce a file not signed by our key is refused unless the player
// holds a permit, notify lets it through with an audit row, off lets it through silently. The consume
// ledger decides whether a file has already bought a ship and whether the account has import
// allowance left, and it answers in every mode. The one step that reaches the player's disk, retiring
// the file to backup, is enforce-only, so a non-enforcing server can rehearse the real path without
// eating a save that still has to work somewhere else.

using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Shipyard;
using Content.Server.Database;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.BUI;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Shipyard.Save;
using Content.Shared._Triad.ShipSize;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem
{
    /// <summary>
    /// The client's inventory of local saves. Judged and cached; the answer reaches the player as the
    /// import list on the drydock tab, never as a per-file verdict, so a modified client cannot use
    /// this to ask which of a pile of forgeries would pass.
    /// </summary>
    private void OnImportManifestMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleImportManifestMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        component.CachedImportables = new();
        component.OfferedImports = new();
        component.ImportOfferAccount = null;

        if (!_configManager.GetCVar(TriadCCVars.DrydockEnabled)
            || !_configManager.GetCVar(TriadCCVars.DrydockImportEnabled))
        {
            RefreshDrydockUi(uid, component, player, (ShipyardConsoleUiKey)args.UiKey);
            return;
        }

        if (!TryGetOperatorAccount(player, out var operatorAccount)
            || DrydockBarsOperator(player, component)
            || !_mind.TryGetMind(player, out _, out var mind)
            || mind.UserId == null)
        {
            RefreshDrydockUi(uid, component, player, (ShipyardConsoleUiKey)args.UiKey);
            return;
        }

        _ = OfferImportsAsync(uid, component, player, operatorAccount, mind.UserId.Value, args.Candidates, (ShipyardConsoleUiKey)args.UiKey);
    }

    /// <summary>
    /// Filters the manifest and publishes what is left. The budget is read once for the whole
    /// manifest rather than per candidate: it bounds how many imports an account may spend, and a
    /// player at their limit is offered nothing rather than offered a list that refuses on click.
    /// </summary>
    private async Task OfferImportsAsync(
        EntityUid uid,
        ShipyardConsoleComponent component,
        EntityUid player,
        Guid operatorAccount,
        NetUserId userId,
        List<DrydockImportCandidate> candidates,
        ShipyardConsoleUiKey uiKey)
    {
        var enforcing = _tamperPolicy.IsEnforcing();
        var remaining = int.MaxValue;

        if (enforcing && !_configManager.GetCVar(TriadCCVars.DrydockImportUnlimited))
        {
            var budget = _configManager.GetCVar(TriadCCVars.DrydockImportBudget);
            var spent = await _consumedStore.CountForPlayerAsync(operatorAccount, default);
            remaining = Math.Max(0, budget - spent);
        }

        var offered = new List<DrydockImportShipInfo>();
        var accepted = new Dictionary<string, DrydockImportCandidate>();

        foreach (var candidate in candidates)
        {
            if (offered.Count >= remaining)
                break;

            if (string.IsNullOrWhiteSpace(candidate.FileId) || accepted.ContainsKey(candidate.FileId))
                continue;

            if (!_tamperPolicy.ShouldOfferForImport(candidate.PublicKey, userId))
                continue;

            // An already-spent file cannot be filtered out here, because that needs its hash and the
            // manifest carries none. It is refused at import instead. An enforcing server retires the
            // file to backup, which takes it off this list; a permissive one leaves it on the
            // player's disk, so there it stays listed and the refusal is what a second press meets.
            accepted[candidate.FileId] = candidate;
            offered.Add(new DrydockImportShipInfo(candidate.FileId, candidate.Name, candidate.Appraisal));
        }

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return;

        component.CachedImportables = offered;
        component.OfferedImports = accepted;
        component.ImportOfferAccount = operatorAccount;

        RefreshDrydockUi(uid, component, player, uiKey);
    }

    private async void OnImportMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleImportMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        // Taken before the first await and given back however this ends, so a second press while the
        // database is being read is refused rather than run alongside the first.
        if (component.ImportInProgress)
        {
            Refuse(uid, component, player, Loc.GetString("shipyard-console-import-error-in-progress"));
            return;
        }

        component.ImportInProgress = true;
        try
        {
            await TryImportLegacyShip(uid, component, player, args.FileId, args.YamlData, (ShipyardConsoleUiKey)args.UiKey);
        }
        catch (Exception e)
        {
            _sawmill.Error($"Legacy import threw {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            // Unconditional: a console left marked busy by a throw would refuse every later import
            // with no way back short of a restart.
            component.ImportInProgress = false;
        }
    }

    /// <summary>
    /// Imports one legacy save: stage it, measure it, give it a berth that fits, and file it.
    ///
    /// <para>Order matters and is not arbitrary. The hash is burned only after the store has
    /// succeeded, because a burn before a failure would cost the player the ship and the save both.
    /// The berth is granted before the store because a store needs somewhere to go, and released
    /// again if the store refuses, so a failed import leaves no empty berth behind.</para>
    /// </summary>
    internal async Task<bool> TryImportLegacyShip(
        EntityUid uid,
        ShipyardConsoleComponent component,
        EntityUid player,
        string fileId,
        string yamlData,
        ShipyardConsoleUiKey uiKey)
    {
        if (!_configManager.GetCVar(TriadCCVars.DrydockEnabled)
            || !_configManager.GetCVar(TriadCCVars.DrydockImportEnabled))
        {
            return Refuse(uid, component, player, Loc.GetString("shipyard-console-import-error-disabled"));
        }

        if (!TryGetOperatorAccount(player, out var operatorAccount))
            return Refuse(uid, component, player, Loc.GetString("shipyard-console-import-error-no-account"));

        if (DrydockBarsOperator(player, component))
            return Refuse(uid, component, player, Loc.GetString("shipyard-console-import-error-barred"));

        // The offer is the gate: a file the server did not just list cannot be imported, and a list
        // built for another captain is not this one's to spend.
        if (string.IsNullOrWhiteSpace(fileId)
            || component.ImportOfferAccount != operatorAccount
            || !component.OfferedImports.TryGetValue(fileId, out var candidate))
        {
            return Refuse(uid, component, player, Loc.GetString("shipyard-console-import-error-stale-offer"));
        }

        if (!_mind.TryGetMind(player, out _, out var mind) || mind.UserId == null)
            return Refuse(uid, component, player, Loc.GetString("shipyard-console-import-error-no-character"));

        if (!_player.TryGetSessionByEntity(player, out var session))
            return Refuse(uid, component, player, Loc.GetString("shipyard-console-import-error-no-session"));

        if (string.IsNullOrWhiteSpace(yamlData))
            return Refuse(uid, component, player, Loc.GetString("shipyard-console-import-error-empty-file"));

        var envelope = AuthenticatedShipFile.FromShipFile(yamlData);
        var hash = envelope.GetHash();

        // The grid document, unwrapped. The loader takes this and not the file: the envelope wraps
        // it alongside the signature and the appraisal, so handing over the whole file makes the
        // loader look for `meta` at the root and throw. This is what the old load path passed too.
        var shipYaml = envelope.ShipYamlString();

        var shipName = shipYaml is { } yaml ? ExtractShipNameFromYaml(yaml) : null;
        if (string.IsNullOrWhiteSpace(shipName))
            shipName = candidate.Name;
        if (string.IsNullOrWhiteSpace(shipName))
            shipName = $"ImportedShip_{DateTime.Now:yyyyMMdd_HHmmss}";

        // The authority, with the payload finally in hand. The manifest filter only decided what to
        // draw; this is the same call the old load path made.
        LoadDecision decision;
        try
        {
            decision = _tamperPolicy.EvaluateLoad(envelope, mind.UserId.Value, shipName);
        }
        catch (Exception ex)
        {
            _sawmill.Warning($"Import EvaluateLoad threw {ex.GetType().Name}; refusing: {ex.Message}");
            decision = new LoadDecision(false, TriadShipyardEventType.LoadRejectedInvalidSignature, "shipyard-tamper-blocked-invalid-signature");
        }

        if (!decision.Allow)
        {
            _ = _tamperPolicy.RecordLoadAsync(
                envelope, mind.UserId.Value, session.Name, shipName, decision.ResolvedEvent,
                loadTimeAppraisal: null,
                roundId: DrydockRoundId, serverName: null, vesselId: null, mapId: null,
                sourceFilePath: fileId, deedHolderEntity: null);
            // Triad: the tamper policy's own reason, the same key the ordinary load path reads.
            return Refuse(uid, component, player, Loc.GetString(decision.PopupReasonLocId ?? "shipyard-tamper-blocked-invalid-signature"));
        }

        var enforcing = _tamperPolicy.IsEnforcing();

        // Replay protection is not the tamper policy's business. The gate above is: it decides
        // whether a document is trusted enough to load at all. These two decide whether a file that
        // has already bought a ship can buy another, and whether this account has any import
        // allowance left, and both answers hold whatever mode the signature check is in.
        //
        // Gating them behind enforcement is what let a notify-mode server hand out the same hull
        // over and over: the console drops a spent file from its own cache, but the client re-sends
        // its whole manifest every time the UI opens, so the file came straight back and imported
        // again for free. Reported from the 2026-09-07 play test with the same cruiser listed four
        // times over.
        //
        // A test server can switch both off with DrydockImportUnlimited, which then also skips the
        // spend and the local-file retirement below, so a tester can import one save repeatedly.
        var unlimited = _configManager.GetCVar(TriadCCVars.DrydockImportUnlimited);

        if (!unlimited && await _consumedStore.IsConsumedAsync(hash, default))
            return await RefuseAsync(uid, component, player, uiKey, Loc.GetString("shipyard-console-import-error-already-imported"));

        var budget = _configManager.GetCVar(TriadCCVars.DrydockImportBudget);
        if (!unlimited && await _consumedStore.CountForPlayerAsync(operatorAccount, default) >= budget)
            return await RefuseAsync(uid, component, player, uiKey, Loc.GetString("shipyard-console-import-error-budget"));

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return false;

        // Stage it the way a purchase does. This is the only way to learn the hull's size: a class is
        // its built tile count, and nothing in the envelope carries one.
        if (string.IsNullOrWhiteSpace(shipYaml)
            || !TryPurchaseShuttleFromYamlData(uid, shipYaml, out var shuttleUid)
            || shuttleUid is not { } grid)
        {
            return Refuse(uid, component, player, Loc.GetString("shipyard-console-import-error-load-failed"));
        }

        if (!TryComp<MapGridComponent>(grid, out var mapGrid))
        {
            QueueDel(grid);
            return Refuse(uid, component, player, Loc.GetString("shipyard-console-import-error-invalid-grid"));
        }

        // A file that already carries a drydock identity describes a hull the drydock has filed.
        // Importing it would file a revision onto that row and, for a hull that is out, mark the
        // row stored while the original flies: the duplicate the design refuses outright.
        if (HasComp<DrydockIdentityComponent>(grid))
        {
            QueueDel(grid);
            return Refuse(uid, component, player, Loc.GetString("shipyard-console-import-error-already-filed"));
        }

        // The file loaded map-initialized, so its first map init happens here, before the store:
        // fills the old writer never saved (door electronics above all) are kept and filed with it.
        _drydock.InitializeImportedShip(grid);

        var sizeClass = _drydockSizes.GetSizeClass((grid, mapGrid));

        var berthId = await _drydockStore.AddBerth(
            operatorAccount, sizeClass, DrydockBerthKind.Granted, 0, operatorAccount, DrydockRoundId);

        // A loaded file's permits have no live owner: the owner uid and mind are session-local and
        // never written, so the store's purge would take every one of them. This claims the ones
        // issued to the importer's character and seizes the rest, since a permit follows its person
        // and never a ship file somebody else saved; the store then keeps exactly those.
        _contrabandPermit.InitializePermitItemsOnGrid(grid, player);
        EntityUid? permitHolderMind = _mind.TryGetMind(player, out var importerMind, out _) ? importerMind : null;

        var result = await _drydock.TryStoreShip(grid, operatorAccount, DrydockRoundId, berthId, permitHolderMind: permitHolderMind);

        if (result.Result != DrydockStoreResult.Success)
        {
            // The store refused, so the grid is still ours to clean up and the berth was never used.
            if (!TerminatingOrDeleted(grid))
                QueueDel(grid);
            await _drydockStore.TryRemoveBerth(berthId, operatorAccount, DrydockAuditAction.BerthDelete, operatorAccount, DrydockRoundId);

            if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
                return false;

            // Triad: chat failure feedback. The store's own reason, not a guess: the enum names why a
            // freshly staged hull could not be filed even though its berth had already been granted.
            var storeReason = result.Result switch
            {
                DrydockStoreResult.NoBerth => "no berth was available",
                DrydockStoreResult.BerthTooSmall => "no berth fit this hull",
                DrydockStoreResult.BerthOccupied => "that berth is occupied",
                DrydockStoreResult.OrganicsAboard => "a crew member was aboard",
                DrydockStoreResult.HazardAboard => "a hazard was aboard",
                DrydockStoreResult.Disabled => "drydock storage is off",
                DrydockStoreResult.InProgress => "that ship was already mid-operation",
                DrydockStoreResult.Cancelled => "the operation was cancelled",
                _ => "the ship would not serialize",
            };
            return await RefuseAsync(uid, component, player, uiKey, Loc.GetString("shipyard-console-import-error-store-failed", ("reason", storeReason)));
        }

        // Filed, so the save is spent, whatever the tamper mode: the ship this produced is real and
        // retrievable, so the file that bought it has to be marked gone. The one exception is a test
        // server running DrydockImportUnlimited, where nothing is spent.
        if (!unlimited
            && !await _consumedStore.TryConsumeAsync(hash, operatorAccount, result.ShipId, shipName, DrydockRoundId, default))
        {
            // Another import of the same file won the race. The ship is already filed under that
            // one, so this is a log line rather than a rollback: the unique index did its job.
            _sawmill.Warning($"Import of '{shipName}' lost the consume race; the hash was already spent.");
        }

        // Retire the local copy the way the old load path did, so the menu stops offering a file
        // that can no longer be imported. Enforcing only, unlike the ledger above: this is the one
        // step that reaches the player's disk, and the same file still has to load on a server that
        // is not draining saves yet. On a permissive server the file stays listed and a second press
        // is refused by the ledger, which is the authority either way. Never under
        // DrydockImportUnlimited, whose point is that the file stays importable.
        if (enforcing && !unlimited && !TerminatingOrDeleted(player))
            RaiseNetworkEvent(new DeleteLocalShipFileMessage(fileId), session);

        _ = _tamperPolicy.RecordLoadAsync(
            envelope, mind.UserId.Value, session.Name, shipName, decision.ResolvedEvent,
            loadTimeAppraisal: null,
            roundId: DrydockRoundId, serverName: null, vesselId: null, mapId: null,
            sourceFilePath: fileId, deedHolderEntity: null);

        // The drydock's own timeline records the import whatever the tamper mode, because the tamper
        // audit stops writing entirely when the check is off and an import must still leave a trace.
        await _drydockStore.WriteAudit(new DrydockAudit
        {
            CreatedAt = DateTime.UtcNow,
            Action = DrydockAuditAction.Imported,
            ShipGuid = result.ShipId,
            ShipName = shipName,
            ActorUserId = operatorAccount,
            BerthId = berthId,
            RoundId = DrydockRoundId,
            Reason = unlimited
                ? "unlimited imports: the save was not spent"
                : enforcing ? null : "tamper check not enforcing",
        });

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return true;

        // The file is spent, so drop it from the offer before the refresh redraws the list. Under
        // DrydockImportUnlimited it is not, and stays offered for the next press.
        if (!unlimited)
        {
            component.OfferedImports.Remove(fileId);
            component.CachedImportables = component.CachedImportables.Where(i => i.FileId != fileId).ToList();
        }

        if (result.ShipId is { } importedId)
            RecordCaptain(importedId, player);

        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        return true;
    }

    /// <summary>Refuses an import: the deny sound plus <paramref name="reason"/> in the pressing
    /// player's chat, via <see cref="DenyWithReason"/>.</summary>
    private bool Refuse(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, string reason)
    {
        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return false;

        DenyWithReason(player, uid, component, reason);
        return false;
    }

    /// <summary>A refusal that also redraws, for the paths that have already read the database.</summary>
    private async Task<bool> RefuseAsync(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey, string reason)
    {
        Refuse(uid, component, player, reason);
        await RefreshAfterRefusal(uid, component, player, uiKey);
        return false;
    }
}
