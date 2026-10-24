// Triad: drydock tab. A partial of the NF ShipyardSystem, kept in the Triad tree beside the other
// Triad console work rather than in _NF/, so an upstream merge touching the shipyard console only
// ever conflicts on the subscriptions in ShipyardSystem.Initialize and the two marked lines in the
// purchase handler. The pipeline this sits in front of lives in Content.Server._Triad.Drydock.

using System.Linq;
using System.Threading.Tasks;
using Content.Server._NF.Shipyard.Components;
using Content.Server._Triad.Drydock;
using Content.Server._Triad.Market;
using Content.Server.Database;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._Mono.Shipyard;
using Content.Shared._NF.Bank;
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.BUI;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared._NF.ShuttleRecords;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Drydock;
using Content.Shared._Triad.Shipyard.Save;
using Content.Shared._Triad.ShipSize;
using Content.Shared.Access.Components;
using Content.Shared.Database;
using Content.Shared.Forensics.Components;
using Content.Shared.Preferences;
using Content.Shared.Shuttles.Components;
using Content.Shared.StationRecords;
using Content.Server.StationEvents.Components;
using Content.Server.StationRecords;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem
{
    [Dependency] private DrydockSystem _drydock = default!;
    [Dependency] private DrydockStore _drydockStore = default!;
    [Dependency] private ShipSizeSystem _drydockSizes = default!;

    /// <summary>
    /// The round to stamp an audit row with, or null when there is no round yet. See
    /// <see cref="GameTicker.RoundIdOrNull"/> for why nullable is what the schema means by it.
    /// </summary>
    private int? DrydockRoundId => _gameTicker.RoundIdOrNull;

    // ---------------------------------------------------------------- Pricing

    /// <summary>The berth price for a hull class, from the prototype ladder. Zero when the ladder has no entry, which disables the charge rather than refusing the purchase.</summary>
    public int DrydockBerthPrice(ShipSizeClass sizeClass)
    {
        return DrydockVesselBerths.BerthPrice(_prototypeManager, sizeClass);
    }

    /// <summary>
    /// The berth class a newly bought hull is sold with: the vessel's row in the
    /// <c>drydockVesselClass</c> table, so the listing's quote and the charge agree, or the live
    /// grid's measured class when the vessel has no row. False when neither is available.
    /// </summary>
    public bool TryGetPurchaseBerthClass(string? vesselId, EntityUid grid, out ShipSizeClass sizeClass)
    {
        if (!string.IsNullOrEmpty(vesselId) && DrydockVesselBerths.TryGetClass(_prototypeManager, vesselId, out sizeClass))
            return true;

        if (TryComp<MapGridComponent>(grid, out var map))
        {
            sizeClass = _drydockSizes.GetSizeClass((grid, map));
            return true;
        }

        sizeClass = default;
        return false;
    }

    /// <summary>
    /// The berth price the purchase handler requires on top of the vessel price before it charges
    /// anything. Zero with the drydock off, and zero for a hull that will carry
    /// <see cref="ShipSavingBlacklistComponent"/>: that component arrives through the vessel's
    /// <c>addComponents</c> after this check, and <see cref="OnShuttlePurchased"/> charges no berth
    /// for such a hull.
    /// </summary>
    internal int DrydockPurchaseBerthPrice(VesselPrototype vessel, EntityUid grid)
    {
        if (!_configManager.GetCVar(TriadCCVars.DrydockEnabled))
            return 0;

        if (vessel.AddComponents.ContainsKey(Factory.GetComponentName<ShipSavingBlacklistComponent>()))
            return 0;

        return TryGetPurchaseBerthClass(vessel.ID, grid, out var sizeClass) ? DrydockBerthPrice(sizeClass) : 0;
    }

    /// <summary>
    /// Drydock fees are owed to the Triad Frontier Administration account, which does not exist yet;
    /// the economy update fills this in. Called after every successful drydock withdrawal and before
    /// every drydock refund deposit. A negative amount is money the TFA pays back (refunds).
    /// </summary>
    private void RouteDrydockFeeToTfa(int credits, MarketTransactionKind kind)
    {
    }

    private Dictionary<string, int> DrydockBerthPrices()
    {
        var prices = new Dictionary<string, int>();
        foreach (var sizeClass in Enum.GetValues<ShipSizeClass>())
            prices[sizeClass.ToString()] = DrydockBerthPrice(sizeClass);
        return prices;
    }

    // ---------------------------------------------------------------- Bundled berth on purchase

    /// <summary>
    /// Every ship bought at a shipyard comes with a berth of its hull class, charged on top of the
    /// vessel price. The purchase handler already required the whole amount before charging the
    /// vessel, so a failure to pay here is a race and not the ordinary case; a new owner is never
    /// left without a berth over it, they get one granted and the shortfall is logged.
    /// </summary>
    private void OnShuttlePurchased(ShipyardShuttlePurchaseEvent ev)
    {
        if (!_configManager.GetCVar(TriadCCVars.DrydockEnabled))
            return;

        if (!TryComp<ShipOwnershipComponent>(ev.Shuttle, out var ownership))
            return;

        // A vessel issued on a voucher, or one its faction has blacklisted from saving, can never
        // be stored, so it brings no berth with it. Faction crews are not drydock customers.
        if (TryComp<ShuttleDeedComponent>(ev.Shuttle, out var deed) && deed.PurchasedWithVoucher
            || HasComp<ShipSavingBlacklistComponent>(ev.Shuttle))
        {
            return;
        }

        // The class the listing quoted and the purchase handler required: the vessel's table row,
        // falling back to the grid only for a vessel with none. The purchase stamps the vessel id
        // before raising this event.
        var vesselId = TryComp<VesselComponent>(ev.Shuttle, out var vessel) ? vessel.VesselId.Id : null;
        if (!TryGetPurchaseBerthClass(vesselId, ev.Shuttle, out var sizeClass))
            return;

        var owner = ownership.OwnerUserId.UserId;
        var price = DrydockBerthPrice(sizeClass);

        var paid = 0;
        var kind = DrydockBerthKind.Granted;
        if (price > 0)
        {
            if (_bank.TryBankWithdraw(ev.Purchaser, price, new MarketRecord { Kind = MarketTransactionKind.DrydockBerth }))
            {
                RouteDrydockFeeToTfa(price, MarketTransactionKind.DrydockBerth);
                paid = price;
                kind = DrydockBerthKind.Purchased;
            }
            else
            {
                Log.Warning($"Drydock: {ToPrettyString(ev.Purchaser)} bought {ToPrettyString(ev.Shuttle)} but could not pay the {price} berth fee after the vessel; berth granted free.");
            }
        }

        _ = GrantPurchasedBerthAsync(owner, sizeClass, kind, paid, ev.Purchaser);
    }

    private async Task GrantPurchasedBerthAsync(Guid owner, ShipSizeClass sizeClass, DrydockBerthKind kind, int paid, EntityUid purchaser)
    {
        try
        {
            await _drydockStore.AddBerth(owner, sizeClass, kind, paid, owner, DrydockRoundId);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: berth for a purchased {sizeClass} could not be created for {owner}: {e.Message}");
            if (!TerminatingOrDeleted(purchaser))
            {
                if (paid > 0)
                {
                    RouteDrydockFeeToTfa(-paid, MarketTransactionKind.DrydockBerth);
                    _bank.TryBankDeposit(purchaser, paid, new MarketRecord { Kind = MarketTransactionKind.DrydockBerth });
                }

                ReportConsoleError(purchaser, DrydockError(DrydockConsoleVerb.BerthGrant, "drydock-error-berth-grant-failed"));
            }
        }

        // The purchase handler refreshes the tab in the same tick it raises the purchase, which is
        // before this write lands, so that read never sees the new berth. Refresh again now it has.
        KickDrydockRefreshForAccount(owner);
    }

    // ---------------------------------------------------------------- Message handlers

    // Every handler fires its verb through RunDrydockVerb: fire-and-forget, which is what a BUI
    // message subscription needs, with the exception logged there rather than escaping to the
    // synchronization context. A database fault is a logged refusal, never an unhandled throw, and
    // the pressing player reads a one-line failure in chat.
    private void OnStoreMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleStoreMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        var uiKey = (ShipyardConsoleUiKey)args.UiKey;
        RunDrydockVerb(uid, player, DrydockConsoleVerb.Store, "store from console", () => TryDrydockStore(uid, component, player, uiKey, args.BerthId), onFailure: () =>
        {
            // Before the refresh, not after: the refresh is what publishes the cached percentage,
            // and a throw from anywhere the pipeline's own finally does not cover would leave the
            // console reporting a store that is no longer running.
            ClearDrydockProgress(uid, component);
            return RefreshAfterRefusal(uid, component, player, uiKey);
        });
    }

    private void OnRetrieveMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleRetrieveMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        var uiKey = (ShipyardConsoleUiKey)args.UiKey;
        RunDrydockVerb(uid, player, DrydockConsoleVerb.Retrieve, $"retrieve of {args.ShipId} from console", () => TryDrydockRetrieve(uid, component, player, args.ShipId, uiKey), onFailure: () =>
        {
            ClearDrydockProgress(uid, component); // Same reason as the store handler's.
            return RefreshAfterRefusal(uid, component, player, uiKey);
        });
    }

    private void OnBuyBerthMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleBuyBerthMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        RunDrydockVerb(uid, player, DrydockConsoleVerb.BuyBerth, "berth purchase at", () => TryBuyBerth(uid, component, player, args.SizeClass, (ShipyardConsoleUiKey)args.UiKey));
    }

    private void OnSellBerthMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleSellBerthMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        RunDrydockVerb(uid, player, DrydockConsoleVerb.SellBerth, "berth sale at", () => TrySellBerth(uid, component, player, args.BerthId, (ShipyardConsoleUiKey)args.UiKey));
    }

    private void OnUpgradeBerthMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleUpgradeBerthMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        RunDrydockVerb(uid, player, DrydockConsoleVerb.UpgradeBerth, "berth upgrade at", () => TryUpgradeBerth(uid, component, player, args.BerthId, (ShipyardConsoleUiKey)args.UiKey));
    }

    private void OnOfferTransferMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleOfferTransferMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        RunDrydockVerb(uid, player, DrydockConsoleVerb.OfferTransfer, "transfer offer at", () => TryOfferTransfer(uid, component, player, args.ShipId, args.RecipientUserId, (ShipyardConsoleUiKey)args.UiKey));
    }

    private void OnCancelTransferMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleCancelTransferMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        RunDrydockVerb(uid, player, DrydockConsoleVerb.CancelTransfer, "transfer cancel at", () => TryCancelTransfer(uid, component, player, args.TransferId, (ShipyardConsoleUiKey)args.UiKey));
    }

    private void OnDeclineTransferMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleDeclineTransferMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        RunDrydockVerb(uid, player, DrydockConsoleVerb.DeclineTransfer, "transfer decline at", () => TryDeclineTransfer(uid, component, player, args.TransferId, (ShipyardConsoleUiKey)args.UiKey));
    }

    private void OnSellStoredShipMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleSellStoredShipMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        RunDrydockVerb(uid, player, DrydockConsoleVerb.Sell, $"sale of {args.ShipId} at", () => TrySellStoredShip(uid, component, player, args.ShipId, args.TypedName, (ShipyardConsoleUiKey)args.UiKey));
    }

    private void OnRenameStoredShipMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleRenameStoredShipMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        RunDrydockVerb(uid, player, DrydockConsoleVerb.Rename, $"rename of {args.ShipId} at", () => TryRenameStoredShip(uid, component, player, args.ShipId, args.NewName, (ShipyardConsoleUiKey)args.UiKey));
    }

    private void OnMoveStoredShipMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleMoveStoredShipMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        RunDrydockVerb(uid, player, DrydockConsoleVerb.Move, $"move of {args.ShipId} at", () => TryMoveStoredShip(uid, component, player, args.ShipId, args.BerthId, (ShipyardConsoleUiKey)args.UiKey));
    }

    private void OnAcceptTransferMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleAcceptTransferMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        RunDrydockVerb(uid, player, DrydockConsoleVerb.AcceptTransfer, "transfer accept at", () => TryAcceptTransfer(uid, component, player, args.TransferId, (ShipyardConsoleUiKey)args.UiKey));
    }

    private void OnRedeemImpoundMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleRedeemImpoundMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        RunDrydockVerb(uid, player, DrydockConsoleVerb.Reclaim, $"reclaim of {args.ShipId} at", () => TryRedeemImpound(uid, component, player, args.ShipId, args.BerthId, (ShipyardConsoleUiKey)args.UiKey));
    }

    private void OnAbandonShipMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleAbandonShipMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        RunDrydockVerb(uid, player, DrydockConsoleVerb.Abandon, $"abandon of {args.ShipId} at", () => TryAbandonShip(uid, component, player, args.ShipId, args.TypedName, (ShipyardConsoleUiKey)args.UiKey));
    }

    private void OnReissueDeedMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleReissueDeedMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        RunDrydockVerb(uid, player, DrydockConsoleVerb.ReissueDeed, $"deed reissue of {args.Ship} at", () => TryReissueDeed(uid, component, player, args.Ship, (ShipyardConsoleUiKey)args.UiKey));
    }

    /// <summary>
    /// Runs one drydock verb the way every message handler needs it run: fired without blocking
    /// the BUI dispatch, with a throw logged rather than escaping to the synchronization context,
    /// then a generic failure line for <paramref name="chatVerb"/> in the pressing player's chat
    /// (the exception itself stays in the server log), and, for the two handlers that need one, a
    /// cleanup step run after both. <paramref name="label"/> is the text between "Drydock: " and
    /// the console/actor pair in the log line, so each caller keeps its own wording exactly.
    /// </summary>
    private async void RunDrydockVerb(EntityUid uid, EntityUid player, DrydockConsoleVerb chatVerb, string label, Func<Task> verb, Func<Task>? onFailure = null)
    {
        try
        {
            await verb();
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: {label} {ToPrettyString(uid)} by {ToPrettyString(player)} threw: {e}");
            if (!TerminatingOrDeleted(player))
                ReportConsoleError(player, DrydockError(chatVerb, "drydock-error-exception"));

            if (onFailure != null)
                await onFailure();
        }
    }

    // ---------------------------------------------------------------- State

    /// <summary>
    /// Fills the console's drydock caches for whoever is operating it, then re-publishes the
    /// interface state so the drydock tab shows the fresh lists. The caches exist because the
    /// state builder is synchronous and these come from the database.
    /// </summary>
    internal async Task RefreshDrydockState(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey)
    {
        // Triad: the caches are NOT cleared here, deliberately. Clearing at the top and refilling
        // after five awaited reads leaves several ticks where the cached lists are empty while the
        // tab shows them, so anything publishing in that window - an upstream RefreshState, a card
        // going in or out, a second refresh - sends a state with no berths and no ships. Lists are
        // built into locals and swapped in at the end, so the cache is only ever replaced by a
        // complete set and an early return leaves the last good one standing.
        //
        // No card, no account to list against: the drydock tab is per-operator, and an empty list
        // is the honest answer rather than everything the console has ever seen.
        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId
            || !TryComp<ActorComponent>(player, out var actor))
        {
            component.CachedDrydock = component.CachedDrydock with
            {
                StoredShips = new(),
                Berths = new(),
                DeedShip = null,
                TransferOffers = new(),
                Captains = new(),
                ImpoundedShips = new(),
            };
            CacheDrydockAccess(component, player, null);
            RefreshDrydockUi(uid, component, player, uiKey);
            return;
        }

        var owner = actor.PlayerSession.UserId.UserId;
        // The five reads below take only the owner and do not depend on one another, and each opens
        // its own database context (every DrydockStore read is its own RunTriadDbCommand), so they
        // run concurrently rather than one main-thread hop at a time. Results still land in locals
        // and swap into the cache together at the end, same as a sequential read would.
        var rowsTask = _drydockStore.GetShipsByOwner(owner);
        var slotsTask = _drydockStore.GetBerths(owner);
        var offersOutTask = _drydockStore.GetPendingOffersFrom(owner);
        var offersInTask = _drydockStore.GetPendingOffersFor(owner);
        var appraisalsTask = _drydockStore.GetCurrentAppraisals(owner);
        await Task.WhenAll(rowsTask, slotsTask, offersOutTask, offersInTask, appraisalsTask);
        var rows = await rowsTask;
        var slots = await slotsTask;
        var offersOut = await offersOutTask;
        var offersIn = await offersInTask;
        var appraisals = await appraisalsTask;

        // Everyone else online, for the transfer picker, with the classes of their free berths so
        // the picker can grey the captains with nowhere to put the ship. Read in one query, and only
        // when the operator has a stored ship to offer: every open tab refreshes on every expiry
        // sweep and every admin action, and a list nobody can pick from is a query per tab for nothing.
        var canOffer = rows.Any(r => r.State == DrydockShipState.Stored);
        var online = canOffer ? _player.Sessions.Where(s => s.UserId.UserId != owner).ToList() : new List<ICommonSession>();

        // Independent of each other too: one draws on the online list above, the other on the two
        // offer sets, neither on both.
        var freeClassesTask = _drydockStore.GetFreeBerthClasses(online.Select(s => s.UserId.UserId));
        var namesTask = _drydockStore.GetPlayerNames(offersOut.Values.Select(t => t.ToUserId).Concat(offersIn.Select(o => o.Transfer.FromUserId)));
        await Task.WhenAll(freeClassesTask, namesTask);
        var freeClasses = await freeClassesTask;
        var names = await namesTask;

        // The console or the operator may have gone during the reads.
        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return;

        // Every hull the account has, including the ones that are out: the tab warns when an
        // action would leave a ship with nowhere to dock.
        var storedShips = rows
            .Select(r => new StoredShipInfo(r.ShipGuid, r.ShipName, r.SizeClass, r.State.ToString(), r.BerthId))
            .ToList();

        var berthInfos = new List<DrydockBerthInfo>();
        var offerInfos = new List<DrydockTransferOfferInfo>();
        var captainInfos = new List<DrydockCaptainInfo>();

        var now = DateTime.UtcNow;
        var refund = _configManager.GetCVar(TriadCCVars.DrydockBerthRefund);
        foreach (var slot in slots)
        {
            int? upgradePrice = null;
            string? upgradeClass = null;
            if (ShipSizeRules.TryParseClass(slot.Berth.MaxSizeClass, out var current) && ShipSizeRules.NextSizeClass(current) is { } next)
            {
                upgradePrice = Math.Max(0, DrydockBerthPrice(next) - DrydockBerthPrice(current));
                upgradeClass = next.ToString();
            }

            DrydockTransfer? escrow = null;
            int? sellPrice = null;
            int? sellBasis = null;
            if (slot.Occupant != null)
            {
                offersOut.TryGetValue(slot.Occupant.ShipGuid, out escrow);
                if (appraisals.TryGetValue(slot.Occupant.ShipGuid, out var appraisal) && appraisal is { } value)
                {
                    sellPrice = DrydockSalePrice((uid, component), value).Net;
                    sellBasis = value;
                }
            }

            berthInfos.Add(new DrydockBerthInfo(
                slot.Berth.BerthId,
                slot.Berth.MaxSizeClass,
                (int)(slot.Berth.PricePaid * refund),
                upgradePrice,
                upgradeClass,
                slot.Occupant?.ShipGuid,
                slot.Occupant?.ShipName,
                slot.Occupant?.SizeClass,
                slot.Occupant?.State.ToString(),
                sellPrice,
                escrow?.Id,
                escrow != null ? CaptainName(escrow.ToUserId, names) : null,
                escrow != null ? SecondsLeft(escrow.ExpiresAt, now) : null,
                sellBasis));
        }

        // The alerts: every offer addressed to this account, with where the ship would land if
        // accepted right now. The berth is chosen again at accept, so this is a preview.
        foreach (var (transfer, ship) in offersIn)
        {
            int? lands = ShipSizeRules.PreferredBerth(FittingFreeBerths(slots, ship.SizeClass), null);

            offerInfos.Add(new DrydockTransferOfferInfo(
                transfer.Id,
                ship.ShipGuid,
                ship.ShipName,
                ship.SizeClass,
                CaptainName(transfer.FromUserId, names),
                transfer.FromUserId,
                lands,
                SecondsLeft(transfer.ExpiresAt, now)));
        }

        foreach (var session in online)
        {
            var id = session.UserId.UserId;
            captainInfos.Add(new DrydockCaptainInfo(id, SessionDisplayName(session), freeClasses.GetValueOrDefault(id) ?? new List<string>()));
        }

        // The impound lot: every hull of the account's the lot holds, with what it costs to get
        // back and which berths it could go into, read off the same slots the rows draw with the
        // store's own preference. The fee is the row's, frozen at impound; the appraisal beside it
        // is what that fee was cut from, so the card can say what share of the hull it is.
        var impoundedInfos = new List<DrydockImpoundedShipInfo>();
        foreach (var row in rows)
        {
            if (row.State != DrydockShipState.Impounded)
                continue;

            var fitting = FittingFreeBerths(slots, row.SizeClass);
            appraisals.TryGetValue(row.ShipGuid, out var basis);

            impoundedInfos.Add(new DrydockImpoundedShipInfo(
                row.ShipGuid,
                row.ShipName,
                row.SizeClass,
                row.ImpoundFee,
                basis,
                row.ImpoundRedeemable,
                row.ImpoundReason,
                ShipSizeRules.PreferredBerth(fitting, row.LastBerthId),
                fitting));
        }

        // Everything is read; swap the whole set in at once. Nothing above this line has touched
        // what the console is currently showing.
        component.CachedDrydock = component.CachedDrydock with
        {
            StoredShips = storedShips,
            Berths = berthInfos,
            TransferOffers = offerInfos,
            Captains = captainInfos,
            ImpoundedShips = impoundedInfos,
            DeedShip = BuildDeedShip(uid, targetId, rows, slots),
        };
        CacheDrydockAccess(component, player, targetId);
        RefreshDrydockUi(uid, component, player, uiKey);
    }

    // ---------------------------------------------------------------- Access, ships out, deed reissue

    /// <summary>
    /// Reads who the operator is into the console's caches: barred or not, the civilian ships their
    /// account has out, and whether the inserted card can take a deed. World reads, so they are
    /// taken at the swap and never across an await.
    /// </summary>
    private void CacheDrydockAccess(ShipyardConsoleComponent component, EntityUid player, EntityUid? targetId)
    {
        component.CachedDrydock = component.CachedDrydock with
        {
            DrydockOperatorBarred = DrydockBarsOperator(player, component),
            ShipsOut = TryGetOperatorAccount(player, out var account)
                ? CivilianShipsOut(account)
                    .Select(grid => new DrydockReissueShipInfo(
                        GetNetEntity(grid),
                        TryComp<ShuttleDeedComponent>(grid, out var deed) ? GetFullName(deed) : Name(grid),
                        TryComp<MapGridComponent>(grid, out var map) ? _drydockSizes.GetSizeClass((grid, map)).ToString() : null))
                    .ToList()
                : new(),
            CanReissueToCard = CanReissueDeedTo(targetId),
        };
    }

    /// <summary>Whether a card could take a reissued deed: present, an ID card, not a voucher, not already deeded.</summary>
    private bool CanReissueDeedTo(EntityUid? card)
    {
        return card is { } id
            && HasComp<IdCardComponent>(id)
            && !HasComp<ShipyardVoucherComponent>(id)
            && !HasComp<ShuttleDeedComponent>(id);
    }

    /// <summary>
    /// Every civilian hull the account has out in the world: owned by it, not issued on a voucher
    /// and not a faction hull. Those two are provisioned equipment, never counted and never stored.
    ///
    /// <para>Read from the world rather than the drydock rows, because a hull bought this round has
    /// no row until its first store. <c>AllEntityQuery</c> and not the enumerator: a hull mid-store
    /// sits paused on a private staging map for several seconds and is still out for that whole
    /// window, and the paused-skipping enumerator would drop it.</para>
    /// </summary>
    internal List<EntityUid> CivilianShipsOut(Guid account)
    {
        var ships = new List<EntityUid>();
        var query = AllEntityQuery<ShipOwnershipComponent>();
        while (query.MoveNext(out var grid, out var ownership))
        {
            if (ownership.OwnerUserId.UserId != account || TerminatingOrDeleted(grid))
                continue;

            if (HasComp<ShipSavingBlacklistComponent>(grid)
                || TryComp<ShuttleDeedComponent>(grid, out var deed) && deed.PurchasedWithVoucher)
            {
                continue;
            }

            ships.Add(grid);
        }

        return ships;
    }

    /// <summary>
    /// Accounts with a retrieve between its gate and its hull landing. The hull carries no ownership
    /// until the pipeline hands it back, so without this two presses at two consoles would both see
    /// nothing out and both land a ship.
    /// </summary>
    private readonly HashSet<Guid> _retrievesInFlight = new();

    /// <summary>
    /// Whether the account may bring another civilian ship out: nothing out and no retrieve in flight.
    /// One ship out per account is the rule the purchase and the retrieve both enforce.
    /// </summary>
    internal bool HasShipOut(Guid account)
    {
        return _retrievesInFlight.Contains(account) || CivilianShipsOut(account).Count > 0;
    }

    /// <summary>
    /// The purchase half of one ship out, called from the upstream purchase handler through one
    /// marked line. A voucher purchase never reaches it: provisioned equipment is not counted. Off
    /// with the drydock, whose tab is what tells the player which ship they have out.
    /// </summary>
    internal bool RefuseShipAlreadyOut(EntityUid uid, ShipyardConsoleComponent component, EntityUid player)
    {
        if (!_configManager.GetCVar(TriadCCVars.DrydockEnabled))
            return false;

        if (!TryGetOperatorAccount(player, out var account) || !HasShipOut(account))
            return false;

        DenyWithReason(player, uid, component, DrydockError(DrydockConsoleVerb.Purchase, "drydock-error-purchase-ship-out"));
        return true;
    }

    /// <summary>
    /// The server half of the lockout for the upstream deed verb (sell). A deed is a holder
    /// claim and cards get lent, lost and stolen, so a card is never proof of ownership: the account
    /// behind the press has to own the hull. A hull issued on a voucher, or one no account owns, is
    /// left to upstream's rules. Both refusals write their reason to the pressing player's chat, so a
    /// caller adds nothing of its own.
    /// </summary>
    internal bool RefuseDeedNotOwned(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShuttleDeedComponent deed, string verb)
    {
        if (deed.PurchasedWithVoucher
            || deed.ShuttleUid is not { Valid: true } shuttle
            || !TryComp<ShipOwnershipComponent>(shuttle, out var ownership))
        {
            return false;
        }

        var chatVerb = verb switch
        {
            "sell" => DrydockConsoleVerb.Sell,
            // A new upstream caller: still refused and still reported, under a neutral label.
            _ => DrydockConsoleVerb.DeedAction,
        };

        if (!TryGetOperatorAccount(player, out var account))
        {
            DenyWithReason(player, uid, component, DrydockError(chatVerb, "drydock-error-no-account"));
            return true;
        }

        if (ownership.OwnerUserId.UserId == account)
            return false;

        RefuseAccess(uid, component, player, account, TryGetDrydockShipId(shuttle), Name(shuttle), ownership.OwnerUserId.UserId, null, verb,
            DrydockError(chatVerb, "drydock-error-not-owner"));
        return true;
    }

    /// <summary>
    /// Writes the character at the console onto the hull's row as its captain, for the registry. Only
    /// ever called after a verb the owning account's character just completed (store, retrieve,
    /// import, accept), so the name is always one of the owner's characters. Fire and forget: it is
    /// display only, and a failure is a log line, never a refusal.
    /// </summary>
    private void RecordCaptain(Guid shipId, EntityUid player)
    {
        var name = Name(player).Trim();
        if (name.Length == 0)
            return;

        _ = RecordCaptainAsync(shipId, name);
    }

    private async Task RecordCaptainAsync(Guid shipId, string name)
    {
        try
        {
            await _drydockStore.SetCaptainName(shipId, name);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: recording {name} as captain of {shipId} threw: {e.Message}");
        }
    }

    /// <summary>
    /// Refuses a drydock verb from an operator the drydock bars. Faction crews are issued their
    /// vessels and may not store, retrieve or keep a garage at all; the tab draws the access-denied
    /// screen over every one of these, and this is what stops a press that gets past it.
    /// </summary>
    private bool RefuseBarredOperator(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, DrydockConsoleVerb chatVerb)
    {
        if (!DrydockBarsOperator(player, component))
            return false;

        DenyWithReason(player, uid, component, DrydockError(chatVerb, "drydock-error-barred"));
        return true;
    }

    /// <summary>
    /// Moves the deed to one of the operator's ships that is out onto the card in the slot, wherever
    /// the ship is, and strips it from every other card. A deed proves a claim; the hull does not
    /// need to be alongside for the claim to be written down again.
    ///
    /// <para>Every other card loses it, not only the last holder: a lost or stolen card, and any copy
    /// made from it, goes dead in the same press. Crew who need in again get guest access at the
    /// helm, which the owner controls.</para>
    /// </summary>
    internal async Task<bool> TryReissueDeed(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, NetEntity shipNet, ShipyardConsoleUiKey uiKey)
    {
        const DrydockConsoleVerb verb = DrydockConsoleVerb.ReissueDeed;

        if (RefuseBarredOperator(uid, component, player, verb))
            return false;

        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId)
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-no-card"));
            return false;
        }

        // The same three conditions CanReissueDeedTo reads for the tab, split so each names itself.
        if (!HasComp<IdCardComponent>(targetId))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-card-not-id"));
            return false;
        }

        if (HasComp<ShipyardVoucherComponent>(targetId))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-card-voucher"));
            return false;
        }

        if (HasComp<ShuttleDeedComponent>(targetId))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-card-has-deed"));
            return false;
        }

        if (!TryGetOperatorAccount(player, out var account))
            return false;

        if (!TryGetEntity(shipNet, out var maybeGrid)
            || maybeGrid is not { } grid
            || TerminatingOrDeleted(grid)
            || !TryComp<ShipOwnershipComponent>(grid, out var ownership))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-reissue-ship-gone"));
            return false;
        }

        if (ownership.OwnerUserId.UserId != account)
        {
            RefuseAccess(uid, component, player, account, TryGetDrydockShipId(grid), Name(grid), ownership.OwnerUserId.UserId, null, "reissue deed",
                DrydockError(verb, "drydock-error-not-owner"));
            return false;
        }

        // Provisioned hulls are not the drydock's to re-key, and a hull mid-store is about to have
        // its deeds settled by the store itself. Owner and existence are already settled above, so
        // a hull missing from the civilian list is a faction or voucher one.
        if (HasComp<DrydockInProgressComponent>(grid))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-reissue-storing"));
            return false;
        }

        if (!CivilianShipsOut(account).Contains(grid))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-reissue-provisioned"));
            return false;
        }

        var loadedFromSave = TryComp<ShuttleDeedComponent>(grid, out var gridDeed) && gridDeed.LoadedFromSave;

        MintDeeds(targetId, grid, player);
        if (TryComp<ShuttleDeedComponent>(targetId, out var cardDeed))
        {
            // The sale rule rides the card; a reissue is not a way to make an unsellable hull sellable.
            cardDeed.LoadedFromSave = loadedFromSave;
            Dirty(targetId, cardDeed);
        }

        AddNewShuttleDeedAccessLevels(targetId, component);

        // Collected before acting, because removing inside the walk mutates what is being walked.
        // AllEntityQuery for the same reason as the count: a card can sit on a paused map.
        var stale = new List<EntityUid>();
        var deeds = AllEntityQuery<ShuttleDeedComponent>();
        while (deeds.MoveNext(out var holder, out var deed))
        {
            if (deed.ShuttleUid == grid && holder != grid && holder != targetId)
                stale.Add(holder);
        }

        foreach (var holder in stale)
            RemComp<ShuttleDeedComponent>(holder);

        _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Medium,
            $"{ToPrettyString(player):actor} reissued the deed to {ToPrettyString(grid)} onto {ToPrettyString(targetId)} via {ToPrettyString(uid)}, stripping {stale.Count} other card(s)");

        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        return true;
    }

    /// <summary>
    /// Whether the ship is docked to one of the station's grids. A store is a hand-over at a berth,
    /// not a remote command: the hull has to be alongside the station whose console is filing it.
    /// Any dock on the ship whose partner sits on a grid the station owns counts.
    /// </summary>
    private bool IsDockedToStation(EntityUid shuttle, EntityUid station)
    {
        foreach (var dock in _docking.GetDocks(shuttle))
        {
            if (dock.Comp.DockedWith is not { } partner || !Exists(partner))
                continue;

            if (Transform(partner).GridUid is { } grid && _station.GetOwningStation(grid) == station)
                return true;
        }

        return false;
    }

    private static int SecondsLeft(DateTime expiresAt, DateTime now)
    {
        return (int)Math.Max(0, Math.Ceiling((expiresAt - now).TotalSeconds));
    }

    /// <summary>The hull's drydock identity, if it has ever been filed. Null for a hull with no history yet.</summary>
    internal Guid? TryGetDrydockShipId(EntityUid grid)
    {
        return TryComp<DrydockIdentityComponent>(grid, out var identity) && identity.ShipId != Guid.Empty
            ? identity.ShipId
            : null;
    }

    /// <summary>The character's name while they are online, else the account's last seen name, else a placeholder.</summary>
    private string CaptainName(Guid userId, Dictionary<Guid, string> lastSeen)
    {
        if (_player.TryGetSessionById(new NetUserId(userId), out var session))
            return SessionDisplayName(session);

        return lastSeen.TryGetValue(userId, out var name) ? name : Loc.GetString("shipyard-console-transfer-someone");
    }

    private string SessionDisplayName(ICommonSession session)
    {
        if (session.AttachedEntity is { } ent && !TerminatingOrDeleted(ent))
        {
            var name = Name(ent).Trim();
            if (name.Length > 0)
                return name;
        }

        return session.Name;
    }

    /// <summary>
    /// The card at the top of the tab: the ship on the inserted deed, how long it has been out,
    /// whether it is docked here, and which of the operator's free berths it fits. Read from the
    /// live grid, never from the cached class text, for the same reason the store itself does.
    /// </summary>
    private DrydockDeedShipInfo? BuildDeedShip(EntityUid console, EntityUid targetId, List<DrydockShip> rows, List<DrydockBerthSlot> slots)
    {
        if (!TryComp<ShuttleDeedComponent>(targetId, out var deed)
            || deed.ShuttleUid is not { Valid: true } shuttle
            || !TryComp<MapGridComponent>(shuttle, out var map))
        {
            return null;
        }

        var sizeClass = _drydockSizes.GetSizeClass((shuttle, map));
        var hullClass = sizeClass.ToString();

        DrydockShip? row = null;
        if (TryGetDrydockShipId(shuttle) is { } shipId)
            row = rows.FirstOrDefault(r => r.ShipGuid == shipId);

        int? minutesOut = row is { State: DrydockShipState.CheckedOut }
            ? (int)Math.Max(0, (DateTime.UtcNow - row.StateChangedAt).TotalMinutes)
            : null;

        // The same preference the store applies: the ship's own last berth if it is free and
        // fits, else the smallest free berth that fits. The dropdown lists the rest.
        var fitting = FittingFreeBerths(slots, hullClass);
        var preferred = ShipSizeRules.PreferredBerth(fitting, row?.LastBerthId);

        var docked = _station.GetOwningStation(console) is { Valid: true } station && IsDockedToStation(shuttle, station);

        return new DrydockDeedShipInfo(GetFullName(deed), hullClass, minutesOut, preferred, fitting, docked);
    }

    /// <summary>The operator's free berths the hull fits, smallest class first, in the order the store's own pick walks them.</summary>
    private static List<int> FittingFreeBerths(List<DrydockBerthSlot> slots, string? hullClass)
    {
        return ShipSizeRules.OrderByFitPreference(
                slots.Where(s => s.Occupant == null && ShipSizeRules.Fits(hullClass, s.Berth.MaxSizeClass)),
                s => s.Berth.MaxSizeClass,
                s => s.Berth.BerthId)
            .Select(s => s.Berth.BerthId)
            .ToList();
    }

    /// <summary>
    /// Fills the tab without waiting: the upstream console handlers are synchronous and the drydock
    /// lists come from the database. Called from one marked line in each.
    ///
    /// <para>Every handler that publishes a console state has to call this, not only the ones that
    /// obviously touch the drydock. <c>RefreshState</c> builds the drydock half out of the caches
    /// this fills, so publishing without kicking a read sends whatever the tab last knew - buying a
    /// ship leaves the deed card blank, because the purchase mints the deed and nothing re-reads
    /// it.</para>
    /// </summary>
    internal void KickDrydockRefresh(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey)
    {
        if (!_configManager.GetCVar(TriadCCVars.DrydockEnabled))
            return;

        _ = RefreshDrydockStateSafe(uid, component, player, uiKey);
    }

    private async Task RefreshDrydockStateSafe(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey)
    {
        try
        {
            await RefreshDrydockState(uid, component, player, uiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: refreshing the tab on {ToPrettyString(uid)} for {ToPrettyString(player)} threw: {e}");
        }
    }

    /// <summary>
    /// Re-fills the drydock tab on every console this account has open, wherever it is. Called
    /// when the other side of an offer acts, so the alert or the escrow row changes under them
    /// without a reopen.
    /// </summary>
    internal void KickDrydockRefreshForAccount(Guid userId)
    {
        KickDrydockRefreshWhere(actorUserId => actorUserId == userId);
    }

    /// <summary>Re-fills every open drydock tab. The expiry sweep calls this, since it does not know who was watching.</summary>
    internal void KickDrydockRefreshAll()
    {
        KickDrydockRefreshWhere(_ => true);
    }

    private void KickDrydockRefreshWhere(Func<Guid, bool> accountMatches)
    {
        if (!_configManager.GetCVar(TriadCCVars.DrydockEnabled))
            return;

        var query = EntityQueryEnumerator<ShipyardConsoleComponent>();
        while (query.MoveNext(out var uid, out var console))
        {
            foreach (var key in Enum.GetValues<ShipyardConsoleUiKey>())
            {
                foreach (var viewer in _ui.GetActors(uid, key))
                {
                    if (TryComp<ActorComponent>(viewer, out var actor) && accountMatches(actor.PlayerSession.UserId.UserId))
                        KickDrydockRefresh(uid, console, viewer, key);
                }
            }
        }
    }

    /// <summary>
    /// The drydock half of the console state, read from the caches. Called by the upstream state
    /// builder through one marked line.
    ///
    /// <para><see cref="ShipyardConsoleComponent.CachedImportables"/> stays its own cache rather
    /// than folding into <see cref="ShipyardConsoleComponent.CachedDrydock"/>: it is written from
    /// the import message handlers in ShipyardSystem.Triad.Import.cs, so merged in here rather
    /// than at every one of that file's write sites.</para>
    /// </summary>
    internal DrydockTabState BuildDrydockState(EntityUid uid)
    {
        // The same floor the offer itself applies, so the prompt never promises less than an offer gets.
        var offerMinutes = (int)Math.Ceiling(Math.Max(60, _configManager.GetCVar(TriadCCVars.DrydockTransferOfferSeconds)) / 60.0);
        var enabled = _configManager.GetCVar(TriadCCVars.DrydockEnabled);
        var prices = DrydockBerthPrices();

        if (!TryComp<ShipyardConsoleComponent>(uid, out var console))
            return DrydockTabState.Empty with { DrydockEnabled = enabled, BerthPrices = prices, TransferOfferMinutes = offerMinutes };

        return console.CachedDrydock with
        {
            DrydockEnabled = enabled,
            BerthPrices = prices,
            DeedOwnerUserId = DeedOwnerAccount(console),
            TransferOfferMinutes = offerMinutes,
            ImportableShips = console.CachedImportables,
            DeedSale = DeedSaleFor(console.TargetIdSlot.ContainerSlot?.ContainedEntity),
        };
    }

    /// <summary>
    /// What the footer's sale button does with the deed on <paramref name="card"/>. A hull the
    /// drydock can hold sells from the drydock tab once stored, so the row stays the one record of
    /// the sale; the footer keeps Sell only for hulls the drydock refuses, and turns into Return for
    /// a hull issued on a voucher.
    /// </summary>
    internal ShipyardDeedSale DeedSaleFor(EntityUid? card)
    {
        if (card is not { Valid: true } id
            || !TryComp<ShuttleDeedComponent>(id, out var deed)
            || deed.ShuttleUid is not { Valid: true } shuttle)
        {
            return ShipyardDeedSale.None;
        }

        if (deed.PurchasedWithVoucher)
            return ShipyardDeedSale.Return;

        if (_configManager.GetCVar(TriadCCVars.DrydockEnabled) && !HasComp<ShipSavingBlacklistComponent>(shuttle))
            return ShipyardDeedSale.StoreFirst;

        return ShipyardDeedSale.Sell;
    }

    /// <summary>
    /// The server half of <see cref="DeedSaleFor"/>: refuses a footer sale of a hull the drydock can
    /// hold, which the console never offers. Writes the reason to the pressing player's chat.
    /// </summary>
    internal bool RefuseFooterSale(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, EntityUid card)
    {
        if (DeedSaleFor(card) != ShipyardDeedSale.StoreFirst)
            return false;

        DenyWithReason(player, uid, component, DrydockError(DrydockConsoleVerb.Sell, "drydock-error-sell-store-first"));
        return true;
    }

    /// <summary>
    /// The account that owns the ship on the inserted card's deed, or null when the card carries no
    /// deed to a live ship. The client compares it with its own account to draw the lockout; every
    /// message the lockout hides is refused server-side regardless, so this is presentation, and
    /// the id it exposes is already networked on the ship's ownership component.
    /// </summary>
    private Guid? DeedOwnerAccount(ShipyardConsoleComponent console)
    {
        if (console.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId
            || !TryComp<ShuttleDeedComponent>(targetId, out var deed)
            || deed.ShuttleUid is not { Valid: true } shuttle
            || !TryComp<ShipOwnershipComponent>(shuttle, out var ownership))
        {
            return null;
        }

        return ownership.OwnerUserId.UserId;
    }

    /// <summary>
    /// The account behind the click, which is the only identity any drydock verb is checked
    /// against. Deliberately not the character's mind: a mind is what goes missing when a dead
    /// player is reprinted into a body without its components, and that has stranded ships before.
    /// A session's account survives every body.
    /// </summary>
    private bool TryGetOperatorAccount(EntityUid player, out Guid userId)
    {
        userId = default;
        if (!TryComp<ActorComponent>(player, out var actor))
            return false;

        userId = actor.PlayerSession.UserId.UserId;
        return true;
    }

    /// <summary>
    /// Whether the operator is barred from the drydock. Faction crews (TDF, TFA, the station roles)
    /// are issued their vessels on voucher and the drydock is the civilian garage, so two signals
    /// decide it: the blacklist component the job stamps on the character, and the console's own
    /// whitelist and blacklist (named for the ship saving they once gated), which read that
    /// component too. The direct check is here so a console whose blacklist a mapper forgot still
    /// refuses.
    /// </summary>
    private bool DrydockBarsOperator(EntityUid player, ShipyardConsoleComponent component)
    {
        return HasComp<ShipSavingBlacklistComponent>(player) || !IsShipSaveWhitelistValid(player, component);
    }

    /// <summary>
    /// Refuses a message whose sender does not own what it names, and writes the refusal to the
    /// timeline. The console never offers such a click, so a row here means a modified client or a
    /// forged message, which is exactly what an admin wants to see beside a stolen-card report.
    /// <paramref name="verb"/> is the audit row's wording; <paramref name="reason"/> is the line the
    /// pressing player reads in chat beside the deny sound.
    /// </summary>
    private void RefuseAccess(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid actor, Guid? shipGuid, string? shipName, Guid? ownerUserId, int? berthId, string verb, string reason)
    {
        Log.Info($"Drydock: {verb} by {ToPrettyString(player)} ({actor}) refused, not the owner of {shipName ?? shipGuid?.ToString() ?? $"berth {berthId}"}.");

        _ = WriteRefusalAsync(new DrydockAudit
        {
            ShipGuid = shipGuid,
            ShipName = shipName,
            BerthId = berthId,
            Action = DrydockAuditAction.AccessRefused,
            ActorUserId = actor,
            SubjectUserId = ownerUserId,
            RoundId = DrydockRoundId,
            Reason = verb,
        });

        DenyWithReason(player, uid, component, reason);
    }

    private async Task WriteRefusalAsync(DrydockAudit entry)
    {
        try
        {
            await _drydockStore.WriteAudit(entry);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: refused-access audit row could not be written: {e.Message}");
        }
    }

    /// <summary>
    /// Republishes the console's whole interface state, recomputed the way opening the console
    /// computes it. Lives here rather than in the upstream console file so the drydock tab needs no
    /// refactor of <c>RefreshState</c>'s caller-supplied arguments.
    /// </summary>
    private void RefreshDrydockUi(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey)
    {
        if (!TryComp<BankAccountComponent>(player, out var bank))
            return;

        var targetId = component.TargetIdSlot.ContainerSlot?.ContainedEntity;
        TryComp<ShuttleDeedComponent>(targetId, out var deed);

        var sellValue = 0;
        if (deed?.ShuttleUid is { } deedShuttle && Exists(deedShuttle))
        {
            sellValue = AppraiseHull(deedShuttle);
            sellValue = CalculateShipResaleValue((uid, component), sellValue);
        }

        RefreshState(
            uid,
            bank.Balance,
            true,
            deed != null ? GetFullName(deed) : null,
            sellValue,
            targetId,
            uiKey,
            HasComp<ShipyardVoucherComponent>(targetId));
    }

    // ---------------------------------------------------------------- Store and retrieve

    /// <summary>
    /// The console half of a store: resolve the ship from the inserted card's deed, check the
    /// operator may put it away, hand off to the pipeline, then clean up the card.
    ///
    /// <para>Ownership here is the ship's stamped account, never the deed on the card. A deed is a
    /// holder claim and cards get lent, so gating on the card would let a borrowed one file
    /// somebody else's ship into a garage they do not own.</para>
    ///
    /// <para>Returns null when this console refused before the pipeline was entered, so a caller
    /// can tell "we did not try" from "we tried and it said no". The in-progress refusal is the one
    /// exception: it is named rather than swallowed, because the pipeline that would have named it
    /// is the one holding the hull.</para>
    /// </summary>
    internal async Task<(DrydockStoreResult Result, Guid? ShipId)?> TryDrydockStore(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey, int? berthId = null)
    {
        const DrydockConsoleVerb verb = DrydockConsoleVerb.Store;

        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId)
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-store-no-card"));
            return null;
        }

        if (!TryComp<ShuttleDeedComponent>(targetId, out var deed) || deed.ShuttleUid is not { Valid: true } shuttleUid)
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-store-no-deed"));
            return null;
        }

        if (RefuseBarredOperator(uid, component, player, verb))
            return null;

        if (!TryGetOperatorAccount(player, out var operatorAccount))
            return null;

        // No account owns it, so nobody can put it away. Not a forged message and not somebody
        // else's ship, so it is a refusal and not a timeline row.
        if (!TryComp<ShipOwnershipComponent>(shuttleUid, out var ownership))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-store-unowned"));
            return null;
        }

        if (ownership.OwnerUserId.UserId != operatorAccount)
        {
            // A ship that has been stored before carries its id; a new hull has none yet, and the
            // refusal is filed against the actor alone.
            RefuseAccess(uid, component, player, operatorAccount, TryGetDrydockShipId(shuttleUid), Name(shuttleUid), ownership.OwnerUserId.UserId, null, "store",
                DrydockError(verb, "drydock-error-not-owner"));
            return null;
        }

        // The hull itself: a faction vessel carries the blacklist on its grid, and a deed bought on
        // a voucher says so. Both are what the ship-save path refuses, for the same reason.
        if (HasComp<ShipSavingBlacklistComponent>(shuttleUid))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-store-faction-ship"));
            return null;
        }

        if (deed.PurchasedWithVoucher)
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-store-voucher-ship"));
            return null;
        }

        // A store is a hand-over at a berth. The ship has to be docked to the station this console
        // belongs to, not parked somewhere in the sector while its captain files it remotely.
        if (_station.GetOwningStation(uid) is not { Valid: true } station)
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-no-station"));
            return null;
        }

        // Ahead of the docked gate, because a store already in flight has undocked the hull and moved
        // it to a private map: every gate below would then refuse it for the wrong reason and tell
        // the captain their ship is not docked for the length of the store. The pipeline's own
        // re-entrancy sentinel is the marker, read here because the pipeline is only reached past
        // these gates.
        if (HasComp<DrydockInProgressComponent>(shuttleUid))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, DrydockStoreErrorKey(DrydockStoreResult.InProgress)));
            return (DrydockStoreResult.InProgress, null);
        }

        if (!IsDockedToStation(shuttleUid, station))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-store-not-docked"));
            return null;
        }

        // This is the only layer that holds the console, the operator and the interface key at
        // once, so the pipeline is handed a delegate rather than being told about any of them. The
        // station goes with it because the store now moves the hull onto a private map before it
        // does any work, and the unwind needs somewhere to put it back that it cannot re-derive
        // from a grid sitting on that map.
        DrydockProgressCallback onProgress = (percent, _) =>
            PushDrydockProgress(uid, component, player, uiKey, DrydockProgressKind.Store, percent);

        // The operator's own permits are the only ones that go with the ship, the ship-save path's
        // ClearPermitItemsOnGrid rule: anyone else's permitted kit left aboard is purged at store
        // rather than filed into the document with the hull.
        EntityUid? permitHolderMind = _mind.TryGetMind(player, out var operatorMind, out _) ? operatorMind : null;

        (DrydockStoreResult Result, Guid? ShipId) result;
        try
        {
            result = await _drydock.TryStoreShip(shuttleUid, ownership.OwnerUserId.UserId, DrydockRoundId, berthId, station, onProgress, permitHolderMind: permitHolderMind);
        }
        finally
        {
            // Every path out of here runs a refresh, and a refresh publishes the cached percentage
            // to whoever opens this console next. A cancellation throws past every one of those
            // return sites, so the clear is a finally: a stale figure would otherwise sit in the
            // cache for the rest of the round and draw a busy button at an idle console.
            ClearDrydockProgress(uid, component);
        }

        // The write yielded. The store itself has already succeeded or refused; everything below is
        // the console epilogue.
        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return result;

        if (result.Result != DrydockStoreResult.Success)
        {
            DenyWithReason(player, uid, component, DrydockError(verb, DrydockStoreErrorKey(result.Result)));
            await RefreshAfterRefusal(uid, component, player, uiKey);
            return result;
        }

        // The grid is gone, so the card deed now points at nothing. Strip it as the sell path does;
        // the ship's durable identity is the database row, not this card.
        if (!TerminatingOrDeleted(targetId))
            RemComp<ShuttleDeedComponent>(targetId);

        if (result.ShipId is { } storedId)
            RecordCaptain(storedId, player);

        PlayConfirmSound(player, uid, component);

        await RefreshDrydockState(uid, component, player, uiKey);
        return result;
    }

    /// <summary>
    /// The console half of a retrieve: check the card can take a deed, hand off to the pipeline,
    /// then mint the deed that makes the returned ship flyable.
    ///
    /// <para>Authorization is the pipeline's, not this method's. It re-reads the row's owner and
    /// moves the row out of <see cref="DrydockShipState.Stored"/> in one conditional update, which
    /// is what makes a forged ship id and two simultaneous retrieves both safe.</para>
    /// </summary>
    internal async Task<EntityUid?> TryDrydockRetrieve(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid shipId, ShipyardConsoleUiKey uiKey)
    {
        const DrydockConsoleVerb verb = DrydockConsoleVerb.Retrieve;

        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId)
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-no-card"));
            return null;
        }

        // One ship per card is card capacity, not duplicate prevention: the row state is what stops
        // a ship existing twice. Refusing here keeps a card from carrying two claims at once.
        if (HasComp<ShuttleDeedComponent>(targetId))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-card-has-deed"));
            return null;
        }

        if (RefuseBarredOperator(uid, component, player, verb))
            return null;

        // A voucher is a claim on a new hull from the faction's list, not a card a stored ship can
        // be called in on. The ship-load path refuses it for the same reason.
        if (HasComp<ShipyardVoucherComponent>(targetId))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-card-voucher"));
            return null;
        }

        if (!TryGetOperatorAccount(player, out var operatorAccount))
            return null;

        if (_station.GetOwningStation(uid) is not { Valid: true } station)
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-no-station"));
            return null;
        }

        // One civilian ship out per account, the rule the purchase enforces too. The claim is taken
        // here and held until the hull has landed carrying its ownership, so a second press at
        // another console in between still sees a ship out.
        if (HasShipOut(operatorAccount))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-retrieve-ship-out"));
            return null;
        }

        _retrievesInFlight.Add(operatorAccount);
        try
        {
            return await RetrieveAfterGates(uid, component, player, shipId, uiKey, targetId, operatorAccount, station);
        }
        finally
        {
            _retrievesInFlight.Remove(operatorAccount);
        }
    }

    /// <summary>The console half of a retrieve past its gates, under the account's in-flight claim.</summary>
    private async Task<EntityUid?> RetrieveAfterGates(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid shipId, ShipyardConsoleUiKey uiKey, EntityUid targetId, Guid operatorAccount, EntityUid station)
    {
        // The pipeline re-reads the owner and refuses on its own; this earlier read is what turns
        // a forged retrieve into a timeline row rather than a silent null.
        var header = await _drydockStore.GetShipHeader(shipId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return null;

        if (header != null && header.OwnerUserId != operatorAccount)
        {
            RefuseAccess(uid, component, player, operatorAccount, shipId, header.ShipName, header.OwnerUserId, header.BerthId, "retrieve",
                DrydockError(DrydockConsoleVerb.Retrieve, "drydock-error-not-owner"));
            return null;
        }

        DrydockProgressCallback onProgress = (percent, _) =>
            PushDrydockProgress(uid, component, player, uiKey, DrydockProgressKind.Retrieve, percent);

        DrydockRetrieve retrieve;
        try
        {
            retrieve = await _drydock.TryRetrieveShip(shipId, operatorAccount, station, DrydockRoundId, onProgress);
        }
        finally
        {
            // Same reason as the store's: the cached figure outlives a cancellation otherwise, and
            // every refresh below would publish it to the next person at this console.
            ClearDrydockProgress(uid, component);
        }

        if (!retrieve.Succeeded)
        {
            if (!TerminatingOrDeleted(player))
            {
                // A success with no grid is not a refusal the pipeline names, so it reads as the
                // generic error rather than as a mapped reason.
                var reason = retrieve.Result == DrydockRetrieveResult.Success
                    ? DrydockError(DrydockConsoleVerb.Retrieve, "drydock-error-exception")
                    : DrydockError(DrydockConsoleVerb.Retrieve, DrydockRetrieveErrorKey(retrieve.Result));
                DenyWithReason(player, uid, component, reason);
            }

            await RefreshAfterRefusal(uid, component, player, uiKey);
            return null;
        }

        var grid = retrieve.Grid!.Value;

        // The read yielded and the card may be gone. The ship is already docked and its row is
        // checked out, so skipping the mint is recoverable through an admin: the console cannot
        // store a hull no card carries a deed to, but an impound takes it and a release puts it
        // back in a berth for nothing. Throwing out of an async void handler is not recoverable.
        if (TerminatingOrDeleted(targetId) || TerminatingOrDeleted(player))
            return grid;

        MintDeeds(targetId, grid, player);
        AddNewShuttleDeedAccessLevels(targetId, component);
        AddCompanyInformation(targetId, grid);
        AddRetrievedShuttleRecord(component, grid, player);

        // The rest of what a purchase and a ship load do for their captain, in their order (the
        // list is the ship-load path's, walked with its author): a station record on
        // the ship's own station, ship access on every door and locker, the grid-split lifecycle
        // marker, the permits claimed by whoever is retrieving and anyone else's seized, and the
        // shipyard channel hearing about it. Not the direction message: a success writes nothing to
        // the captain's chat, and the tab already shows the ship out. Ownership is the drydock's own step, since
        // the row, not the card, says who owns a retrieved ship. Console locks are the drydock's
        // too, because they hold the grid uid, which only the retrieve knows.
        if (_station.GetOwningStation(grid) is { Valid: true } shipStation)
            EnsureCaptainStationRecord(shipStation, targetId, player);

        // AI cores respawn: a stored core comes back empty and does not re-offer its ghost role.
        // Called here rather than only from import, which is meant to be switched off once the
        // legacy pool drains and would have taken this with it.
        _shipLoadRespawn.RespawnMarkedEntities(grid);

        AddShipAccessToEntities(grid);
        EnsureComp<LinkedLifecycleGridParentComponent>(grid);
        _contrabandPermit.InitializePermitItemsOnGrid(grid, player);

        var gridName = Name(grid);
        SendPurchaseMessage(uid, player, gridName, component.ShipyardChannel, secret: false);
        if (component.SecretShipyardChannel is { } secretChannel)
            SendPurchaseMessage(uid, player, gridName, secretChannel, secret: true);

        RecordCaptain(shipId, player);
        PlayConfirmSound(player, uid, component);

        await RefreshDrydockState(uid, component, player, uiKey);
        return grid;
    }

    // ---------------------------------------------------------------- Berths

    /// <summary>Buys a berth for the operator's own account. Money first, then the row; a row that fails after the money moved refunds it.</summary>
    internal async Task<bool> TryBuyBerth(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, string sizeClassText, ShipyardConsoleUiKey uiKey)
    {
        const DrydockConsoleVerb verb = DrydockConsoleVerb.BuyBerth;

        if (RefuseBarredOperator(uid, component, player, verb))
            return false;

        if (!TryComp<ActorComponent>(player, out var actor))
            return false;

        if (!ShipSizeRules.TryParseClass(sizeClassText, out var sizeClass))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-berth-class-unknown"));
            return false;
        }

        var price = DrydockBerthPrice(sizeClass);
        if (price <= 0)
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-buy-berth-not-for-sale"));
            return false;
        }

        if (!_bank.TryBankWithdraw(player, price, new MarketRecord { Kind = MarketTransactionKind.DrydockBerth }))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-funds", ("price", BankSystemExtensions.ToSpesoString(price))));
            return false;
        }

        RouteDrydockFeeToTfa(price, MarketTransactionKind.DrydockBerth);

        var owner = actor.PlayerSession.UserId.UserId;
        try
        {
            await _drydockStore.AddBerth(owner, sizeClass, DrydockBerthKind.Purchased, price, owner, DrydockRoundId);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: berth purchase for {owner} failed after payment: {e.Message}");
            if (!TerminatingOrDeleted(player))
            {
                RouteDrydockFeeToTfa(-price, MarketTransactionKind.DrydockBerth);
                _bank.TryBankDeposit(player, price, new MarketRecord { Kind = MarketTransactionKind.DrydockBerth });
                ReportConsoleError(player, DrydockError(verb, "drydock-error-exception"));
            }

            return false;
        }

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return true;

        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        return true;
    }

    /// <summary>Sells one of the operator's empty berths for the configured fraction of what was paid.</summary>
    internal async Task<bool> TrySellBerth(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, int berthId, ShipyardConsoleUiKey uiKey)
    {
        const DrydockConsoleVerb verb = DrydockConsoleVerb.SellBerth;

        if (RefuseBarredOperator(uid, component, player, verb))
            return false;

        if (!TryComp<ActorComponent>(player, out var actor))
            return false;

        var owner = actor.PlayerSession.UserId.UserId;
        var (outcome, berth) = await _drydockStore.TryRemoveBerth(berthId, owner, DrydockAuditAction.BerthSale, owner, DrydockRoundId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return outcome == DrydockBerthResult.Success;

        if (outcome != DrydockBerthResult.Success || berth == null)
        {
            DenyWithReason(player, uid, component, BerthRefusalText(verb, outcome));
            return false;
        }

        var refund = (int)(berth.PricePaid * _configManager.GetCVar(TriadCCVars.DrydockBerthRefund));
        if (refund > 0)
        {
            RouteDrydockFeeToTfa(-refund, MarketTransactionKind.DrydockBerth);
            _bank.TryBankDeposit(player, refund, new MarketRecord { Kind = MarketTransactionKind.DrydockBerth });
        }

        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        return true;
    }

    /// <summary>Raises one of the operator's berths one class, charging the price difference.</summary>
    internal async Task<bool> TryUpgradeBerth(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, int berthId, ShipyardConsoleUiKey uiKey)
    {
        const DrydockConsoleVerb verb = DrydockConsoleVerb.UpgradeBerth;

        if (RefuseBarredOperator(uid, component, player, verb))
            return false;

        if (!TryComp<ActorComponent>(player, out var actor))
            return false;

        var owner = actor.PlayerSession.UserId.UserId;
        var slots = await _drydockStore.GetBerths(owner);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return false;

        var slot = slots.FirstOrDefault(s => s.Berth.BerthId == berthId);
        if (slot == null)
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-berth-not-yours"));
            return false;
        }

        if (!ShipSizeRules.TryParseClass(slot.Berth.MaxSizeClass, out var current))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-berth-class-unknown"));
            return false;
        }

        if (ShipSizeRules.NextSizeClass(current) is not { } next)
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-upgrade-berth-largest"));
            return false;
        }

        var delta = Math.Max(0, DrydockBerthPrice(next) - DrydockBerthPrice(current));
        if (delta > 0 && !_bank.TryBankWithdraw(player, delta, new MarketRecord { Kind = MarketTransactionKind.DrydockBerth }))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-funds", ("price", BankSystemExtensions.ToSpesoString(delta))));
            return false;
        }

        if (delta > 0)
            RouteDrydockFeeToTfa(delta, MarketTransactionKind.DrydockBerth);

        var outcome = await _drydockStore.TryUpgradeBerth(berthId, owner, next, delta, owner, DrydockRoundId);
        return await FinishVerb(uid, component, player, uiKey, outcome == DrydockBerthResult.Success, () => BerthRefusalText(verb, outcome), onDeny: () =>
        {
            if (delta > 0)
            {
                RouteDrydockFeeToTfa(-delta, MarketTransactionKind.DrydockBerth);
                _bank.TryBankDeposit(player, delta, new MarketRecord { Kind = MarketTransactionKind.DrydockBerth });
            }
        });
    }

    // ---------------------------------------------------------------- Transfer

    /// <summary>
    /// Opens an offer of one of the operator's stored ships to another account. The recipient has
    /// to be online right now, which is the one social gate: an offer is a conversation, not a
    /// parcel left on a doorstep. From here the offer is a persisted row with a deadline, the
    /// ship waits in escrow in its own berth, and the recipient answers from any console.
    /// </summary>
    internal async Task<bool> TryOfferTransfer(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid shipId, Guid recipient, ShipyardConsoleUiKey uiKey)
    {
        const DrydockConsoleVerb verb = DrydockConsoleVerb.OfferTransfer;

        if (RefuseBarredOperator(uid, component, player, verb))
            return false;

        if (!TryGetOperatorAccount(player, out var owner))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-no-account"));
            return false;
        }

        if (recipient == owner)
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-offer-self"));
            return false;
        }

        // The account behind the click must own the row. The card in the slot says nothing here,
        // and this is checked before anything about the recipient so a forged offer of someone
        // else's ship lands on the timeline whoever it was addressed to.
        if (await GateOwnedShip(uid, component, player, shipId, owner, "transfer", verb, DrydockShipState.Stored) is null)
            return false;

        if (!_player.TryGetSessionById(new NetUserId(recipient), out _))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-offer-recipient-offline"));
            return false;
        }

        var seconds = Math.Max(60, _configManager.GetCVar(TriadCCVars.DrydockTransferOfferSeconds));
        var (outcome, transfer) = await _drydockStore.TryOfferTransfer(shipId, owner, recipient, TimeSpan.FromSeconds(seconds), DrydockRoundId);
        return await FinishVerb(uid, component, player, uiKey, outcome == DrydockBerthResult.Success && transfer != null,
            () => BerthRefusalText(verb, outcome),
            onSuccess: () => KickDrydockRefreshForAccount(recipient));
    }

    /// <summary>The owner withdraws a standing offer. The ship leaves escrow; the recipient's alert goes.</summary>
    internal async Task<bool> TryCancelTransfer(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, long transferId, ShipyardConsoleUiKey uiKey)
    {
        return await TryEndTransfer(uid, component, player, transferId, DrydockTransferResolution.Cancelled, uiKey);
    }

    /// <summary>The recipient turns an offer down. The ship leaves escrow; the owner's row goes back to Stored.</summary>
    internal async Task<bool> TryDeclineTransfer(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, long transferId, ShipyardConsoleUiKey uiKey)
    {
        return await TryEndTransfer(uid, component, player, transferId, DrydockTransferResolution.Declined, uiKey);
    }

    private async Task<bool> TryEndTransfer(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, long transferId, DrydockTransferResolution resolution, ShipyardConsoleUiKey uiKey)
    {
        // Cancel is the owner's verb and decline the recipient's.
        var chatVerb = resolution == DrydockTransferResolution.Cancelled
            ? DrydockConsoleVerb.CancelTransfer
            : DrydockConsoleVerb.DeclineTransfer;

        if (RefuseBarredOperator(uid, component, player, chatVerb))
            return false;

        if (!TryGetOperatorAccount(player, out var operatorAccount))
            return false;

        var pending = await _drydockStore.GetPendingTransfer(transferId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return false;

        if (pending is not var (transfer, ship))
        {
            DenyWithReason(player, uid, component, DrydockError(chatVerb, "drydock-error-offer-not-open"));
            return false;
        }

        // The console never offers the other party's verb, so the wrong party here is a forged
        // message and goes on the timeline.
        var (rightParty, verb, notYoursKey) = resolution == DrydockTransferResolution.Cancelled
            ? (transfer.FromUserId, "cancel offer", "drydock-error-offer-not-made-by-you")
            : (transfer.ToUserId, "decline offer", "drydock-error-offer-not-addressed");
        if (rightParty != operatorAccount)
        {
            RefuseAccess(uid, component, player, operatorAccount, ship.ShipGuid, ship.ShipName, ship.OwnerUserId, ship.BerthId, verb,
                DrydockError(chatVerb, notYoursKey));
            return false;
        }

        // Null is the store's only refusal here, and past the party check above it means the offer
        // stopped being pending between the read and the write.
        var resolved = await _drydockStore.TryResolveTransfer(transferId, resolution, operatorAccount, DrydockRoundId);
        return await FinishVerb(uid, component, player, uiKey, resolved != null,
            () => DrydockError(chatVerb, "drydock-error-offer-not-open"),
            onSuccess: () => KickDrydockRefreshForAccount(resolution == DrydockTransferResolution.Cancelled ? resolved!.ToUserId : resolved!.FromUserId));
    }

    /// <summary>
    /// The recipient takes the ship. The store re-checks the deadline and picks the berth now,
    /// so an alert that outlived its offer, or a garage that filled up meanwhile, is a refusal
    /// rather than a ship in two places.
    /// </summary>
    internal async Task<bool> TryAcceptTransfer(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, long transferId, ShipyardConsoleUiKey uiKey)
    {
        const DrydockConsoleVerb verb = DrydockConsoleVerb.AcceptTransfer;

        if (RefuseBarredOperator(uid, component, player, verb))
            return false;

        if (!TryGetOperatorAccount(player, out var recipient))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-no-account"));
            return false;
        }

        // A card has to be in the slot. The tab lists against the account behind the click, not the
        // card, so this is not who the card belongs to; it is that a retrieve mints the deed onto
        // a card, and a recipient with none in cannot follow the accept with the retrieve.
        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true })
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-no-card"));
            return false;
        }

        var pending = await _drydockStore.GetPendingTransfer(transferId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return false;

        if (pending is not var (transfer, ship))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-offer-not-open"));
            return false;
        }

        if (transfer.ToUserId != recipient)
        {
            RefuseAccess(uid, component, player, recipient, ship.ShipGuid, ship.ShipName, ship.OwnerUserId, ship.BerthId, "accept offer",
                DrydockError(verb, "drydock-error-offer-not-addressed"));
            return false;
        }

        var (outcome, _, acceptedName) = await _drydockStore.TryAcceptTransfer(transferId, recipient, DrydockRoundId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return outcome == DrydockBerthResult.Success;

        if (outcome != DrydockBerthResult.Success || acceptedName == null)
        {
            DenyWithReason(player, uid, component, BerthRefusalText(verb, outcome));
            return false;
        }

        RecordCaptain(ship.ShipGuid, player);
        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        KickDrydockRefreshForAccount(transfer.FromUserId);
        return true;
    }

    // ---------------------------------------------------------------- Sell, rename, move

    /// <summary>The shipyard's appraisal of a live hull, as the sale path prices it. Captured at store as the scrap quote.</summary>
    public int AppraiseHull(EntityUid grid)
    {
        return (int)_pricing.AppraiseGrid(grid, LacksPreserveOnSaleComp);
    }

    /// <summary>
    /// What scrapping a stored ship pays and what each tax account takes, from the appraisal
    /// captured at store. The same arithmetic as the live sale's, so the figure on the menu is
    /// the figure that lands.
    /// </summary>
    public (int Gross, int Net, List<(SectorBankAccount Account, int Tax)> Taxes) DrydockSalePrice(Entity<ShipyardConsoleComponent> console, int appraisal)
    {
        var gross = console.Comp.IgnoreBaseSaleRate ? appraisal : (int)(appraisal * _baseSaleRate);
        gross = Math.Max(0, gross);

        var taxes = new List<(SectorBankAccount, int)>();
        var net = gross;
        foreach (var (account, coeff) in console.Comp.TaxAccounts)
        {
            var tax = CalculateSalesTax(gross, coeff);
            taxes.Add((account, tax));
            net -= tax;
        }

        return (gross, Math.Max(0, net), taxes);
    }

    /// <summary>
    /// Writes the row's name onto a retrieved hull and its grid-side deed, split the way the
    /// shipyard splits a typed name into name and suffix. Called by the retrieve pipeline before
    /// the station is recreated, since the station takes the grid's name.
    /// </summary>
    public void StampStoredName(EntityUid grid, string fullName)
    {
        fullName = fullName.Trim();
        if (fullName.Length == 0)
            return;

        if (TryComp<ShuttleDeedComponent>(grid, out var deed))
        {
            var (name, suffix) = DrydockNameRules.SplitShuttleName(fullName);
            deed.ShuttleName = name;
            deed.ShuttleNameSuffix = suffix;
            Dirty(grid, deed);
        }

        _metaData.SetEntityName(grid, fullName);
    }

    /// <summary>
    /// The owner scraps a stored ship. The typed name is compared with the row's name here,
    /// exactly, which is the safety the modal exists for: the client's locked button is a
    /// convenience and this comparison is the rule. Money moves after the row is Sold, the
    /// same order as the live sale.
    /// </summary>
    internal async Task<(bool Sold, int Price, bool Paid)> TrySellStoredShip(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid shipId, string typedName, ShipyardConsoleUiKey uiKey)
    {
        const DrydockConsoleVerb verb = DrydockConsoleVerb.Sell;

        if (RefuseBarredOperator(uid, component, player, verb))
            return (false, 0, false);

        if (!TryGetOperatorAccount(player, out var owner))
            return (false, 0, false);

        if (!HasComp<BankAccountComponent>(player))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-no-bank"));
            return (false, 0, false);
        }

        var header = await _drydockStore.GetShipHeader(shipId);
        var appraisals = await _drydockStore.GetCurrentAppraisals(owner);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return (false, 0, false);

        if (header != null && header.OwnerUserId != owner)
        {
            RefuseAccess(uid, component, player, owner, shipId, header.ShipName, header.OwnerUserId, header.BerthId, "sell",
                DrydockError(verb, "drydock-error-not-owner"));
            return (false, 0, false);
        }

        if (header == null)
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-ship-not-found"));
            return (false, 0, false);
        }

        if (header.State != DrydockShipState.Stored)
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-ship-not-stored"));
            return (false, 0, false);
        }

        if (!TypedNameMatches(typedName, header.ShipName))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-name-mismatch"));
            return (false, 0, false);
        }

        if (!appraisals.TryGetValue(shipId, out var appraisal) || appraisal is not { } value)
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-sell-no-appraisal"));
            return (false, 0, false);
        }

        var price = DrydockSalePrice((uid, component), value);
        var (outcome, soldName) = await _drydockStore.TrySellShip(shipId, owner, price.Net, value, DrydockRoundId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return (outcome == DrydockBerthResult.Success, price.Net, false);

        if (outcome != DrydockBerthResult.Success || soldName == null)
        {
            DenyWithReason(player, uid, component, BerthRefusalText(verb, outcome));
            return (false, 0, false);
        }

        foreach (var (account, tax) in price.Taxes)
            _bank.TrySectorDeposit(account, tax, LedgerEntryType.ShipyardTax);

        var paid = price.Net <= 0 || _bank.TryBankDeposit(player, price.Net, new MarketRecord { Kind = MarketTransactionKind.ShipyardSale });
        if (!paid)
            Log.Error($"Drydock: {shipId} ({soldName}) was sold by {owner} for {price.Net} but the deposit to {ToPrettyString(player)} failed; the timeline row carries the amount.");

        _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Low, $"{ToPrettyString(player):actor} scrapped stored ship {soldName} ({shipId}) for {price.Net} credits via {ToPrettyString(uid)}");

        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        return (true, price.Net, paid);
    }

    /// <summary>
    /// The shipyard's own live sale, catching the drydock row up. Called by the upstream sell
    /// handler through one marked line after the credits have moved, with the identity read off the
    /// grid before the sale deleted it. Only a hull the drydock has filed carries an identity, so a
    /// fresh purchase sold the same round never reaches here. Without this the row goes on reading
    /// as checked out: the panel lists a scrapped ship as stranded, a plain restore hands it back on
    /// top of the credits, and a restore from sale finds no sale on file.
    /// </summary>
    internal void RecordDrydockLiveSale(Guid shipId, EntityUid player, int price, int appraisal)
    {
        if (!TryGetOperatorAccount(player, out var seller))
            return;

        _ = RecordDrydockLiveSaleAsync(shipId, seller, price, appraisal);
    }

    private async Task RecordDrydockLiveSaleAsync(Guid shipId, Guid seller, int price, int appraisal)
    {
        try
        {
            var (outcome, _) = await _drydockStore.TrySellLiveShip(shipId, seller, price, appraisal, DrydockRoundId);
            if (outcome != DrydockBerthResult.Success)
                Log.Warning($"Drydock: {shipId} was sold live at the shipyard but its row would not move to sold ({outcome}); the shipyard log carries the sale.");
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: recording the live sale of {shipId} threw: {e}");
        }
    }

    /// <summary>
    /// The owner renames a stored ship. The suffix the shipyard gave the hull survives: only the
    /// name part changes, and the hull and deed take the new full name at the next retrieve.
    /// </summary>
    internal async Task<bool> TryRenameStoredShip(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid shipId, string newName, ShipyardConsoleUiKey uiKey)
    {
        const DrydockConsoleVerb verb = DrydockConsoleVerb.Rename;

        if (RefuseBarredOperator(uid, component, player, verb))
            return false;

        if (!TryGetOperatorAccount(player, out var owner))
            return false;

        newName = newName.Trim();
        if (!DrydockNameRules.IsValidStoredShipName(newName))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-rename-invalid", ("max", ShuttleDeedComponent.MaxNameLength)));
            return false;
        }

        if (await GateOwnedShip(uid, component, player, shipId, owner, "rename", verb, DrydockShipState.Stored) is not { } header)
            return false;

        var (_, suffix) = DrydockNameRules.SplitShuttleName(header.ShipName);
        var fullName = suffix == null ? newName : $"{newName} {suffix}";

        var outcome = await _drydockStore.TryRenameShip(shipId, owner, fullName, DrydockRoundId);
        return await FinishVerb(uid, component, player, uiKey, outcome == DrydockBerthResult.Success, () => BerthRefusalText(verb, outcome));
    }

    /// <summary>
    /// The owner moves a stored ship to another of their own empty berths that fits. The store's
    /// admin move does the work; the composite key on the ship row already refuses another
    /// owner's berth, and the ownership check here is what turns a forged move into a timeline row.
    /// </summary>
    internal async Task<bool> TryMoveStoredShip(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid shipId, int berthId, ShipyardConsoleUiKey uiKey)
    {
        const DrydockConsoleVerb verb = DrydockConsoleVerb.Move;

        if (RefuseBarredOperator(uid, component, player, verb))
            return false;

        if (!TryGetOperatorAccount(player, out var owner))
            return false;

        if (await GateOwnedShip(uid, component, player, shipId, owner, "move", verb, DrydockShipState.Stored) is null)
            return false;

        var outcome = await _drydockStore.TryMoveShip(shipId, berthId, owner, DrydockRoundId, "moved at the console");
        return await FinishVerb(uid, component, player, uiKey, outcome == DrydockBerthResult.Success, () => BerthRefusalText(verb, outcome));
    }

    // ---------------------------------------------------------------- The impound lot

    /// <summary>
    /// The owner pays the fee and takes an impounded ship back into one of their berths. Money
    /// first, then the row, and the money comes back on every refusal: the order a berth purchase
    /// uses. The fee charged is the one on the row when it was read, and the store refuses the move
    /// when the row's fee is any different by the time it lands, so a re-impound on new terms
    /// between the read and the press cannot be paid at the old price.
    /// </summary>
    internal async Task<bool> TryRedeemImpound(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid shipId, int berthId, ShipyardConsoleUiKey uiKey)
    {
        const DrydockConsoleVerb verb = DrydockConsoleVerb.Reclaim;

        if (RefuseBarredOperator(uid, component, player, verb))
            return false;

        if (!TryGetOperatorAccount(player, out var owner))
            return false;

        if (!HasComp<BankAccountComponent>(player))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-no-bank"));
            return false;
        }

        if (await GateOwnedShip(uid, component, player, shipId, owner, "reclaim", verb, DrydockShipState.Impounded) is not { } header)
            return false;

        if (!header.ImpoundRedeemable)
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-impound-locked"));
            return false;
        }

        // The fee is owed to the Triad Frontier Administration, which has no account to receive it
        // until the economy update; RouteDrydockFeeToTfa is the seam that work fills in.
        var fee = header.ImpoundFee;
        if (fee > 0 && !_bank.TryBankWithdraw(player, fee, new MarketRecord { Kind = MarketTransactionKind.DrydockImpound }))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-reclaim-funds", ("fee", BankSystemExtensions.ToSpesoString(fee))));
            return false;
        }

        if (fee > 0)
            RouteDrydockFeeToTfa(fee, MarketTransactionKind.DrydockImpound);

        var outcome = await _drydockStore.TryRedeemImpound(shipId, owner, berthId, fee, DrydockRoundId);

        if (outcome != DrydockBerthResult.Success)
        {
            // Nothing moved, so the money goes back to whoever is still standing there.
            if (fee > 0 && !TerminatingOrDeleted(player))
            {
                RouteDrydockFeeToTfa(-fee, MarketTransactionKind.DrydockImpound);
                if (!_bank.TryBankDeposit(player, fee, new MarketRecord { Kind = MarketTransactionKind.DrydockImpound }))
                    Log.Error($"Drydock: reclaim of {shipId} by {owner} was refused ({outcome}) and the {fee} taken could not be returned to {ToPrettyString(player)}.");
            }

            if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
                return false;

            DenyWithReason(player, uid, component, BerthRefusalText(verb, outcome));
            await RefreshAfterRefusal(uid, component, player, uiKey);
            return false;
        }

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return true;

        _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Low, $"{ToPrettyString(player):actor} reclaimed impounded ship {header.ShipName} ({shipId}) into berth {berthId} for {fee} credits via {ToPrettyString(uid)}");

        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        return true;
    }

    /// <summary>
    /// The owner gives up an impounded ship rather than pay for it. The typed name is the safety,
    /// compared here exactly as a sale compares it; no money moves in either direction, which is
    /// the whole difference from a sale; and a locked impound refuses, so an owner cannot end an
    /// adjudication from their side.
    /// </summary>
    internal async Task<bool> TryAbandonShip(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid shipId, string typedName, ShipyardConsoleUiKey uiKey)
    {
        const DrydockConsoleVerb verb = DrydockConsoleVerb.Abandon;

        if (RefuseBarredOperator(uid, component, player, verb))
            return false;

        if (!TryGetOperatorAccount(player, out var owner))
            return false;

        if (await GateOwnedShip(uid, component, player, shipId, owner, "abandon", verb, DrydockShipState.Impounded) is not { } header)
            return false;

        if (!header.ImpoundRedeemable)
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-impound-locked"));
            return false;
        }

        if (!TypedNameMatches(typedName, header.ShipName))
        {
            DenyWithReason(player, uid, component, DrydockError(verb, "drydock-error-name-mismatch"));
            return false;
        }

        var (outcome, abandonedName) = await _drydockStore.TryAbandonShip(shipId, owner, DrydockRoundId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return outcome == DrydockBerthResult.Success;

        if (outcome != DrydockBerthResult.Success || abandonedName == null)
        {
            DenyWithReason(player, uid, component, BerthRefusalText(verb, outcome));
            await RefreshAfterRefusal(uid, component, player, uiKey);
            return false;
        }

        _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Low, $"{ToPrettyString(player):actor} abandoned impounded ship {abandonedName} ({shipId}) via {ToPrettyString(uid)}");

        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        return true;
    }

    // ---------------------------------------------------------------- Console errors

    /// <summary>
    /// The drydock presses whose refusals a player reads in chat. Each names the label its failure
    /// line opens with ("Store failed: ..."), through <see cref="DrydockVerbKey"/>.
    /// </summary>
    internal enum DrydockConsoleVerb : byte
    {
        Store,
        Retrieve,
        Purchase,
        BerthGrant,
        BuyBerth,
        SellBerth,
        UpgradeBerth,
        OfferTransfer,
        CancelTransfer,
        DeclineTransfer,
        AcceptTransfer,
        Sell,
        Rename,
        Move,
        Reclaim,
        Abandon,
        ReissueDeed,
        DeedAction,
    }

    /// <summary>The locale key of the label a verb's failure line opens with.</summary>
    internal static string DrydockVerbKey(DrydockConsoleVerb verb)
    {
        return verb switch
        {
            DrydockConsoleVerb.Store => "drydock-error-verb-store",
            DrydockConsoleVerb.Retrieve => "drydock-error-verb-retrieve",
            DrydockConsoleVerb.Purchase => "drydock-error-verb-purchase",
            DrydockConsoleVerb.BerthGrant => "drydock-error-verb-berth-grant",
            DrydockConsoleVerb.BuyBerth => "drydock-error-verb-buy-berth",
            DrydockConsoleVerb.SellBerth => "drydock-error-verb-sell-berth",
            DrydockConsoleVerb.UpgradeBerth => "drydock-error-verb-upgrade-berth",
            DrydockConsoleVerb.OfferTransfer => "drydock-error-verb-offer",
            DrydockConsoleVerb.CancelTransfer => "drydock-error-verb-cancel-offer",
            DrydockConsoleVerb.DeclineTransfer => "drydock-error-verb-decline-offer",
            DrydockConsoleVerb.AcceptTransfer => "drydock-error-verb-accept-offer",
            DrydockConsoleVerb.Sell => "drydock-error-verb-sell",
            DrydockConsoleVerb.Rename => "drydock-error-verb-rename",
            DrydockConsoleVerb.Move => "drydock-error-verb-move",
            DrydockConsoleVerb.Reclaim => "drydock-error-verb-reclaim",
            DrydockConsoleVerb.Abandon => "drydock-error-verb-abandon",
            DrydockConsoleVerb.ReissueDeed => "drydock-error-verb-reissue-deed",
            DrydockConsoleVerb.DeedAction => "drydock-error-verb-deed-action",
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, null),
        };
    }

    /// <summary>
    /// The locale key naming why a store refused. Every refusal value has its own line; asking for
    /// <see cref="DrydockStoreResult.Success"/> is a caller bug and throws.
    /// </summary>
    internal static string DrydockStoreErrorKey(DrydockStoreResult result)
    {
        return result switch
        {
            DrydockStoreResult.SerializeFailed => "drydock-error-store-serialize-failed",
            DrydockStoreResult.OrganicsAboard => "drydock-error-store-organics-aboard",
            DrydockStoreResult.HazardAboard => "drydock-error-store-hazard-aboard",
            DrydockStoreResult.ValidationFailed => "drydock-error-store-validation-failed",
            DrydockStoreResult.Disabled => "drydock-error-store-disabled",
            DrydockStoreResult.NoBerth => "drydock-error-store-no-berth",
            DrydockStoreResult.BerthTooSmall => "drydock-error-store-berth-too-small",
            DrydockStoreResult.InProgress => "drydock-error-store-in-progress",
            DrydockStoreResult.BerthOccupied => "drydock-error-berth-occupied",
            DrydockStoreResult.Cancelled => "drydock-error-interrupted",
            DrydockStoreResult.Success => throw new ArgumentOutOfRangeException(nameof(result), result, "A successful store has no error line."),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, null),
        };
    }

    /// <summary>
    /// The locale key naming why a retrieve refused. Every refusal value has its own line, including
    /// <see cref="DrydockRetrieveResult.NoStagingMap"/>, which nothing produces any more; asking for
    /// <see cref="DrydockRetrieveResult.Success"/> is a caller bug and throws.
    /// </summary>
    internal static string DrydockRetrieveErrorKey(DrydockRetrieveResult result)
    {
        return result switch
        {
            DrydockRetrieveResult.Disabled => "drydock-error-retrieve-disabled",
            DrydockRetrieveResult.NoStation => "drydock-error-retrieve-no-dock-grid",
            DrydockRetrieveResult.NoStagingMap => "drydock-error-retrieve-no-staging-map",
            DrydockRetrieveResult.NotFound => "drydock-error-retrieve-not-found",
            DrydockRetrieveResult.NotOwned => "drydock-error-not-owner",
            DrydockRetrieveResult.AlreadyOut => "drydock-error-retrieve-already-out",
            DrydockRetrieveResult.Impounded => "drydock-error-retrieve-impounded",
            DrydockRetrieveResult.InEscrow => "drydock-error-retrieve-in-escrow",
            DrydockRetrieveResult.Sold => "drydock-error-retrieve-sold",
            DrydockRetrieveResult.NotStored => "drydock-error-retrieve-not-stored",
            DrydockRetrieveResult.NoReadableRevision => "drydock-error-retrieve-unreadable",
            DrydockRetrieveResult.StationLost => "drydock-error-retrieve-station-lost",
            DrydockRetrieveResult.Cancelled => "drydock-error-interrupted",
            DrydockRetrieveResult.Destroyed => "drydock-error-retrieve-destroyed",
            DrydockRetrieveResult.Abandoned => "drydock-error-retrieve-abandoned",
            DrydockRetrieveResult.ContentDrift => "drydock-error-retrieve-content-drift",
            DrydockRetrieveResult.Success => throw new ArgumentOutOfRangeException(nameof(result), result, "A successful retrieve has no error line."),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, null),
        };
    }

    /// <summary>
    /// The locale key naming why a berth or row write refused, for the verb that asked. The store
    /// methods behind each verb produce only some of the values, and those pairings read as what
    /// that store method means by them (for an accept, a wrong state is an offer that expired or was
    /// withdrawn). Every other pairing falls to the value's own meaning on
    /// <see cref="DrydockBerthResult"/>, so no refusal is ever silent; asking for
    /// <see cref="DrydockBerthResult.Success"/> is a caller bug and throws.
    /// </summary>
    internal static string DrydockBerthErrorKey(DrydockConsoleVerb verb, DrydockBerthResult result)
    {
        return (verb, result) switch
        {
            (_, DrydockBerthResult.Success) => throw new ArgumentOutOfRangeException(nameof(result), result, "A successful write has no error line."),

            // TryRemoveBerth.
            (DrydockConsoleVerb.SellBerth, DrydockBerthResult.NotFound) => "drydock-error-berth-not-yours",
            (DrydockConsoleVerb.SellBerth, DrydockBerthResult.BerthOccupied) => "drydock-error-sell-berth-occupied",

            // TryUpgradeBerth. A wrong state past the console's own class check is another upgrade
            // that landed first.
            (DrydockConsoleVerb.UpgradeBerth, DrydockBerthResult.NotFound) => "drydock-error-berth-not-yours",
            (DrydockConsoleVerb.UpgradeBerth, DrydockBerthResult.WrongState) => "drydock-error-upgrade-berth-already",

            // TryOfferTransfer. The berth outcomes are the recipient's garage, not the owner's.
            (DrydockConsoleVerb.OfferTransfer, DrydockBerthResult.NoBerth) => "drydock-error-offer-recipient-no-berth",
            (DrydockConsoleVerb.OfferTransfer, DrydockBerthResult.BerthTooSmall) => "drydock-error-offer-recipient-too-small",
            (DrydockConsoleVerb.OfferTransfer, DrydockBerthResult.Conflict) => "drydock-error-offer-conflict",

            // TryAcceptTransfer.
            (DrydockConsoleVerb.AcceptTransfer, DrydockBerthResult.NotFound) => "drydock-error-offer-not-open",
            (DrydockConsoleVerb.AcceptTransfer, DrydockBerthResult.WrongState) => "drydock-error-accept-expired",
            (DrydockConsoleVerb.AcceptTransfer, DrydockBerthResult.NoBerth) => "drydock-error-accept-no-berth",
            (DrydockConsoleVerb.AcceptTransfer, DrydockBerthResult.BerthTooSmall) => "drydock-error-accept-berth-too-small",
            (DrydockConsoleVerb.AcceptTransfer, DrydockBerthResult.Conflict) => "drydock-error-accept-conflict",

            // The row is re-read inside each write, so these are the ship leaving the account
            // (offer, sale, rename, abandon) or leaving the stored state (offer, sale, rename, move)
            // after the console's own gate passed.
            (DrydockConsoleVerb.OfferTransfer or DrydockConsoleVerb.Sell or DrydockConsoleVerb.Rename or DrydockConsoleVerb.Abandon, DrydockBerthResult.NotFound) => "drydock-error-ship-not-found",
            (DrydockConsoleVerb.OfferTransfer or DrydockConsoleVerb.Sell or DrydockConsoleVerb.Rename or DrydockConsoleVerb.Move, DrydockBerthResult.WrongState) => "drydock-error-ship-not-stored",

            // TryMoveShip and TryRedeemImpound name a berth, so its checks are the named berth's.
            (DrydockConsoleVerb.Move or DrydockConsoleVerb.Reclaim, DrydockBerthResult.NotFound) => "drydock-error-berth-or-ship-not-yours",
            (DrydockConsoleVerb.Move or DrydockConsoleVerb.Reclaim, DrydockBerthResult.BerthTooSmall) => "drydock-error-named-berth-too-small",

            // TryRedeemImpound and TryAbandonShip.
            (DrydockConsoleVerb.Reclaim or DrydockConsoleVerb.Abandon, DrydockBerthResult.WrongState) => "drydock-error-impound-changed",
            (DrydockConsoleVerb.Reclaim, DrydockBerthResult.Conflict) => "drydock-error-reclaim-fee-changed",

            // Pairings the store does not produce for the verb, read as the value itself.
            (_, DrydockBerthResult.NoBerth) => "drydock-error-berth-no-berth",
            (_, DrydockBerthResult.BerthTooSmall) => "drydock-error-berth-too-small",
            (_, DrydockBerthResult.BerthOccupied) => "drydock-error-berth-occupied",
            (_, DrydockBerthResult.NotFound) => "drydock-error-berth-or-ship-not-yours",
            (_, DrydockBerthResult.WrongState) => "drydock-error-berth-wrong-state",
            (_, DrydockBerthResult.Conflict) => "drydock-error-berth-conflict",
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, null),
        };
    }

    /// <summary>
    /// A drydock failure line: <paramref name="key"/> with the verb's label as <c>$verb</c> and any
    /// further arguments beside it.
    /// </summary>
    private string DrydockError(DrydockConsoleVerb verb, string key, params (string, object)[] args)
    {
        var all = new (string, object)[args.Length + 1];
        all[0] = ("verb", Loc.GetString(DrydockVerbKey(verb)));
        args.CopyTo(all, 1);
        return Loc.GetString(key, all);
    }

    /// <summary>
    /// The failure line for a berth or row write that did not succeed. A store call that reported
    /// success but handed back nothing to act on is not a refusal it names, so it reads as the
    /// generic error line instead of a mapped reason.
    /// </summary>
    private string BerthRefusalText(DrydockConsoleVerb verb, DrydockBerthResult outcome)
    {
        return outcome == DrydockBerthResult.Success
            ? DrydockError(verb, "drydock-error-exception")
            : DrydockError(verb, DrydockBerthErrorKey(verb, outcome));
    }

    // ---------------------------------------------------------------- Helpers

    /// <summary>
    /// Hands the operator who pressed the button the pipeline's own percentage, and remembers it on
    /// the console so a tab opened halfway through draws the indicator instead of a live button.
    ///
    /// <para>Aimed at the actor rather than published as state: the whole point of slicing the
    /// pipeline is to stop it spending main-thread time per tick, and republishing the interface
    /// state would re-run the shuttle listing and a grid appraisal every time the figure moved. The
    /// actor overload is the <see cref="EntityUid"/> one; the session overload is client-only and
    /// returns without sending anything when called from here.</para>
    ///
    /// <para>Called synchronously from inside the pipeline's own tick slice, so it does the least
    /// it can: one cache write and one message.</para>
    /// </summary>
    private void PushDrydockProgress(EntityUid uid, ShipyardConsoleComponent component, EntityUid player,
        ShipyardConsoleUiKey uiKey, DrydockProgressKind kind, int percent)
    {
        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return;

        component.CachedDrydock = component.CachedDrydock with { StoreProgressPercent = percent };

        _ui.ServerSendUiMessage(uid, uiKey, new ShipyardConsoleDrydockProgressMessage(kind, percent), player);
    }

    /// <summary>
    /// Forgets whatever this console was reporting. Called from the finally around each pipeline
    /// call and again from the handler catches, because a cached percentage is published by every
    /// later state this console builds and would otherwise outlive the operation by the round.
    /// </summary>
    private void ClearDrydockProgress(EntityUid uid, ShipyardConsoleComponent component)
    {
        if (TerminatingOrDeleted(uid))
            return;

        component.CachedDrydock = component.CachedDrydock with { StoreProgressPercent = null };
    }

    /// <summary>
    /// A refusal changes nothing on the server, but the client only takes its store or retrieve
    /// indicator down when a state arrives, so a refusal has to send one or the button sits on
    /// "Retrieving" until its timeout. Errors here are logged and swallowed: the deny sound and the
    /// chat reason have already gone out.
    /// </summary>
    private async Task RefreshAfterRefusal(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey)
    {
        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return;

        try
        {
            await RefreshDrydockState(uid, component, player, uiKey);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: state refresh after a refusal at {ToPrettyString(uid)} threw: {e.Message}");
        }
    }

    /// <summary>Whether a typed confirmation matches the row's name exactly, the safety a sell or abandon modal enforces server-side.</summary>
    private static bool TypedNameMatches(string typedName, string shipName)
    {
        return string.Equals(typedName.Trim(), shipName.Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The gate a stored or impounded-ship verb opens with once it has the operator's account: the
    /// row, then (in order) whether the console or operator went during the read, whether the
    /// account behind the click owns the row (audited and denied when it does not), and whether the
    /// row exists and is in the state the verb requires (denied). Every denial names its reason in
    /// chat under <paramref name="chatVerb"/>; <paramref name="verb"/> is the audit row's wording.
    /// Returns null once the gate itself has denied or the console or operator went; the caller's
    /// own early return matches every branch this leaves unhandled, so it need only test for null.
    /// </summary>
    private async Task<DrydockShip?> GateOwnedShip(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid shipId, Guid owner, string verb, DrydockConsoleVerb chatVerb, DrydockShipState requiredState)
    {
        var header = await _drydockStore.GetShipHeader(shipId);

        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return null;

        if (header != null && header.OwnerUserId != owner)
        {
            RefuseAccess(uid, component, player, owner, shipId, header.ShipName, header.OwnerUserId, header.BerthId, verb,
                DrydockError(chatVerb, "drydock-error-not-owner"));
            return null;
        }

        if (header == null)
        {
            DenyWithReason(player, uid, component, DrydockError(chatVerb, "drydock-error-ship-not-found"));
            return null;
        }

        if (header.State != requiredState)
        {
            var key = requiredState == DrydockShipState.Impounded ? "drydock-error-ship-not-impounded" : "drydock-error-ship-not-stored";
            DenyWithReason(player, uid, component, DrydockError(chatVerb, key));
            return null;
        }

        return header;
    }

    /// <summary>
    /// The deny-or-confirm-and-refresh tail a drydock write shares once its store call has an
    /// outcome: the console or operator may have gone during the write, checked first and returned
    /// with neither sound nor message; a refusal runs <paramref name="onDeny"/> first, then plays
    /// the deny sound and writes <paramref name="denyReason"/> (read only on refusal) to the
    /// player's chat, matching every verb that refunds a charge on refusal; a success plays the
    /// confirm sound, republishes state, then runs <paramref name="onSuccess"/>, and writes nothing.
    /// </summary>
    private async Task<bool> FinishVerb(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey, bool success, Func<string> denyReason, Action? onDeny = null, Action? onSuccess = null)
    {
        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return success;

        if (!success)
        {
            onDeny?.Invoke();
            DenyWithReason(player, uid, component, denyReason());
            return false;
        }

        PlayConfirmSound(player, uid, component);
        await RefreshDrydockState(uid, component, player, uiKey);
        onSuccess?.Invoke();
        return true;
    }

    /// <summary>
    /// Mints the deeds a retrieved ship needs, mirroring the deed-assign block of the purchase
    /// path, which deeds the card and the grid both. The card the ship was stored with is generally
    /// gone by now - it was stripped at store, and rounds end - so retrieve always mints rather than
    /// looking for the old one.
    ///
    /// <para>The grid-side deed normally rides the document. A legacy import brings none, because
    /// the ship-save exporter strips <c>ShuttleDeed</c> from everything it writes, and a hull with
    /// no grid deed files no shuttle record and takes no name stamp. So the grid half is minted when
    /// the document did not bring one, and left alone when it did: an existing deed carries the
    /// owner the hull was bought under, which is not this retrieve's to rewrite.</para>
    /// </summary>
    internal void MintDeeds(EntityUid targetId, EntityUid shuttleUid, EntityUid player)
    {
        TryComp<ShuttleDeedComponent>(shuttleUid, out var gridDeed);
        var name = gridDeed != null ? GetFullName(gridDeed) : Name(shuttleUid);
        var owner = Name(player).Trim();

        var deed = EnsureComp<ShuttleDeedComponent>(targetId);
        AssignShuttleDeedProperties(deed, shuttleUid, name, owner, purchasedWithVoucher: false);
        deed.DeedHolder = targetId;

        if (gridDeed == null)
        {
            // The name is the row's, already stamped onto the grid by the retrieve. Not
            // loadedFromSave, deliberately: that flag bars a sale at any shipyard console, and a
            // retrieved hull is meant to sell like a purchased one whichever way it got here.
            gridDeed = EnsureComp<ShuttleDeedComponent>(shuttleUid);
            AssignShuttleDeedProperties(gridDeed, shuttleUid, name, owner, purchasedWithVoucher: false);
        }

        // The grid-side deed tracks which card currently holds it; retrieve's rebind left that
        // blank because the card it pointed at did not survive the store.
        gridDeed.DeedHolder = targetId;
        Dirty(shuttleUid, gridDeed);
    }

    /// <summary>
    /// Applies what the vessel prototype grants a hull, through the same call the purchase makes
    /// (<c>EntityManager.AddComponents(shuttleUid, vessel.AddComponents)</c> in
    /// ShipyardSystem.Consoles.cs, which until now was the only site in the server that applied it).
    /// The grant belongs to the shipyard, not to the document: a ship file never carried it, because
    /// the ship-save exporter strips <c>IFF</c> the same way it strips the deed and the station
    /// membership, and a retrieve is a purchase's worth of paperwork done again from a row.
    ///
    /// <para>Additive, so a crew's own work survives a round trip. <paramref name="vesselProto"/>
    /// is the caller's resolution where it has one - the drydock prefers the row - and falls back
    /// to the id the purchase wrote onto the grid, which rides both kinds of document.</para>
    ///
    /// <para>One exception to additive, and it is the whole reason this exists. Every vessel base
    /// grants <c>IsPlayerShuttle</c>, so an <c>IFF</c> without that flag was minted by something
    /// that is not the shipyard: the import path used to <c>EnsureComp</c> a bare one, which is the
    /// component's factory gold. Such a hull draws amber on every mass scanner where its purchased
    /// sister draws BaseVessel's white, and the radar's shuttle filter does not count it as a
    /// shuttle at all (ShuttleNavControl.xaml.cs:689). It is wrong on its own terms, so it is
    /// dropped to let the grant land. A customised IFF still carries the flag and is left alone.</para>
    /// </summary>
    internal void GrantVesselComponents(EntityUid grid, string? vesselProto)
    {
        if (string.IsNullOrEmpty(vesselProto) && TryComp<VesselComponent>(grid, out var vessel))
            vesselProto = vessel.VesselId.Id;

        if (!string.IsNullOrEmpty(vesselProto)
            && _prototypeManager.TryIndex<VesselPrototype>(vesselProto, out var proto))
        {
            if (TryComp<IFFComponent>(grid, out var iff) && (iff.Flags & IFFFlags.IsPlayerShuttle) == 0x0)
                RemComp<IFFComponent>(grid);

            EntityManager.AddComponents(grid, proto.AddComponents, removeExisting: false);
        }

        // Deliberately no IFF floor here for a hull that resolves to no vessel. The import sets
        // its own, and a retrieve has to hand back what the document holds: a floor in this shared
        // path mints a gold IFF on every stored hull that never had one, which the round-trip
        // oracle reads as state appearing out of nowhere.
    }

    /// <summary>
    /// Files the sector shuttle record the purchase and ship-load paths file, so a retrieved ship
    /// shows on the shuttle records console. Records are round-scoped and keyed by the live grid, so
    /// every retrieve files a fresh one. Same gate as those paths: a console that cannot transfer
    /// deeds keeps no records.
    /// </summary>
    private void AddRetrievedShuttleRecord(ShipyardConsoleComponent component, EntityUid grid, EntityUid player)
    {
        if (!component.CanTransferDeed || !TryComp<ShuttleDeedComponent>(grid, out var gridDeed))
            return;

        uint price = 0;
        if (TryComp<VesselComponent>(grid, out var vesselComp)
            && _prototypeManager.TryIndex<VesselPrototype>(vesselComp.VesselId, out var vessel))
        {
            price = (uint)vessel.Price;
        }

        _shuttleRecordsSystem.AddRecord(
            new ShuttleRecord(
                name: gridDeed.ShuttleName ?? "",
                suffix: gridDeed.ShuttleNameSuffix ?? "",
                ownerName: Name(player).Trim(),
                entityUid: GetNetEntity(grid),
                purchasedWithVoucher: false,
                loadedFromSave: false,
                purchasePrice: price));
    }

    /// <summary>
    /// Puts the captain on the ship's own station records: their existing record copied over from
    /// any station that has one, else a fresh general record from their profile. Lifted verbatim
    /// from the ship-load path, which purchase mirrors, so that retrieve does what both do.
    /// </summary>
    internal void EnsureCaptainStationRecord(EntityUid shuttleStation, EntityUid targetId, EntityUid player)
    {
        if (!TryComp<StationRecordKeyStorageComponent>(targetId, out var keyStorage) || keyStorage.Key == null)
            return;

        var recSuccess = false;
        var stationList = EntityQueryEnumerator<StationRecordsComponent>();
        while (stationList.MoveNext(out _, out _))
        {
            if (!_records.TryGetRecord<GeneralStationRecord>(keyStorage.Key.Value, out var record))
                continue;

            _records.AddRecordEntry(shuttleStation, record);
            recSuccess = true;
            break;
        }

        if (!recSuccess
            && _mind.TryGetMind(player, out _, out var mindComp)
            && mindComp.UserId != null
            && _prefManager.GetPreferences(mindComp.UserId.Value).SelectedCharacter is HumanoidCharacterProfile playerProfile
            && TryComp<StationRecordsComponent>(shuttleStation, out var stationRec))
        {
            TryComp<FingerprintComponent>(player, out var fingerprintComponent);
            TryComp<DnaComponent>(player, out var dnaComponent);

            _records.CreateGeneralRecord(
                shuttleStation,
                targetId,
                playerProfile.Name,
                playerProfile.Age,
                playerProfile.Species,
                playerProfile.Gender,
                "Captain",
                fingerprintComponent?.Fingerprint ?? string.Empty,
                dnaComponent?.DNA ?? string.Empty,
                playerProfile,
                stationRec);
        }

        // Not every station keeps records: the POI outposts a shipyard console can sit on have
        // none, and Synchronize logs an error when the component is missing.
        if (HasComp<StationRecordsComponent>(shuttleStation))
            _records.Synchronize(shuttleStation);
    }

    /// <summary>
    /// Blanks the grid-side deed's holder for the length of a store and hands back what it was.
    /// The holder is the card in the console, which is not in the ship's document, so serialized
    /// it is an invalid reference that the deserializer logs as an error on every scratch load and
    /// every retrieve. Retrieve mints a card deed and sets the holder afresh, so nothing is lost by
    /// writing it blank; the abort path puts the live value back.
    /// </summary>
    internal EntityUid? DetachGridDeedHolder(EntityUid grid)
    {
        if (!TryComp<ShuttleDeedComponent>(grid, out var deed))
            return null;

        var holder = deed.DeedHolder;
        deed.DeedHolder = null;
        Dirty(grid, deed);
        return holder;
    }

    /// <summary>The abort half of <see cref="DetachGridDeedHolder"/>.</summary>
    internal void ReattachGridDeedHolder(EntityUid grid, EntityUid? holder)
    {
        if (holder == null || !TryComp<ShuttleDeedComponent>(grid, out var deed))
            return;

        deed.DeedHolder = holder;
        Dirty(grid, deed);
    }
}
