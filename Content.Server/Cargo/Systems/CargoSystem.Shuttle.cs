using Content.Server.Cargo.Components;
using Content.Shared.Stacks;
using Content.Shared.Cargo;
using Content.Shared.Cargo.BUI;
using Content.Shared.Cargo.Components;
using Content.Shared.Cargo.Events;
using Content.Shared.GameTicking;
using Robust.Shared.Map;
using Robust.Shared.Audio;
using Content.Shared.Whitelist; // Frontier
using Content.Server._NF.Cargo.Components; // Frontier
using Content.Shared._NF.Bank.Components; // Frontier
using Content.Shared.Mobs;
using Robust.Shared.Containers; // Frontier
using Content.Shared._Mono.ItemTax.Components; // Mono
using Content.Server._NF.Bank;
using Content.Server._NF.Trade; // Mono
using Content.Shared._NF.Bank.BUI;
using Content.Shared._NF.Trade;
using Content.Shared.Mech.Components;
using Robust.Shared.Toolshed.Commands.Math; // Mono


using Content.Server._Triad.Market; // Triad: market data
using Content.Shared.Materials; // Triad: market data
using Content.Shared.Chemistry.Components.SolutionManager; // Triad: market data
using Content.Server.Database; // Triad: market data

namespace Content.Server.Cargo.Systems;

public sealed partial class CargoSystem
{
    /*
     * Handles cargo shuttle / trade mechanics.
     */

    // Frontier addition:
    // The maximum distance from the console to look for pallets.
    private const int DefaultPalletDistance = 8;

    private static readonly SoundPathSpecifier ApproveSound = new("/Audio/Effects/Cargo/ping.ogg");

    private void InitializeShuttle()
    {
        SubscribeLocalEvent<TradeStationComponent, GridSplitEvent>(OnTradeSplit);

        SubscribeLocalEvent<CargoShuttleConsoleComponent, ComponentStartup>(OnCargoShuttleConsoleStartup);

        SubscribeLocalEvent<CargoPalletConsoleComponent, CargoPalletSellMessage>(OnPalletSale);
        SubscribeLocalEvent<CargoPalletConsoleComponent, CargoPalletAppraiseMessage>(OnPalletAppraise);
        SubscribeLocalEvent<CargoPalletConsoleComponent, BoundUIOpenedEvent>(OnPalletUIOpen);

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    #region Console

    private void UpdateCargoShuttleConsoles(EntityUid shuttleUid, CargoShuttleComponent _)
    {
        // Update pilot consoles that are already open.
        _console.RefreshDroneConsoles();

        // Update order consoles.
        var shuttleConsoleQuery = AllEntityQuery<CargoShuttleConsoleComponent>();

        while (shuttleConsoleQuery.MoveNext(out var uid, out var _))
        {
            var stationUid = _station.GetOwningStation(uid);
            if (stationUid != shuttleUid)
                continue;

            UpdateShuttleState(uid, stationUid);
        }
    }

    private void UpdatePalletConsoleInterface(Entity<CargoPalletConsoleComponent> uid) // Frontier: EntityUid<Entity
    {
        if (Transform(uid).GridUid is not { Valid: true } gridUid)
        {
            _uiSystem.SetUiState(uid.Owner,
                CargoPalletConsoleUiKey.Sale, // Frontier: uid<uid.Owner
                new CargoPalletConsoleInterfaceState(0, 0, false));
            return;
        }

        // Frontier: per-object market modification
        GetPalletGoods(uid, gridUid, out var toSell, out var amount, out var noModAmount, out var blackMarketTaxAmount, out var frontierTaxAmount, out var nfsdTaxAmount, out var medicalTaxAmount);

        amount += noModAmount;
        // End Frontier

        // Monolith: display multiplier
        var station = _station.GetOwningStation(uid);
        var tradeCrateMultiplier = 1D;
        var otherMultiplier = 1D;

        if (TryComp<TradeCrateWildcardDestinationComponent>(station, out var wildcard))
            tradeCrateMultiplier = wildcard.ValueMultiplier;

        if (TryComp<MarketModifierComponent>(uid, out var marketModifier) && !marketModifier.Buy)
            otherMultiplier = marketModifier.Mod;

        _uiSystem.SetUiState(uid.Owner,
            CargoPalletConsoleUiKey.Sale, // Frontier: uid<uid.Owner
            new CargoPalletConsoleInterfaceState((int)amount, toSell.Count, true, tradeCrateMultiplier, otherMultiplier));
        // End Monolith
    }

    private void OnPalletUIOpen(EntityUid uid, CargoPalletConsoleComponent component, BoundUIOpenedEvent args)
    {
        UpdatePalletConsoleInterface((uid, component)); // Frontier: EntityUid<Entity
    }

    /// <summary>
    /// Ok so this is just the same thing as opening the UI, its a refresh button.
    /// I know this would probably feel better if it were like predicted and dynamic as pallet contents change
    /// However.
    /// I dont want it to explode if cargo uses a conveyor to move 8000 pineapple slices or whatever, they are
    /// known for their entity spam i wouldnt put it past them
    /// </summary>

    private void OnPalletAppraise(EntityUid uid, CargoPalletConsoleComponent component, CargoPalletAppraiseMessage args)
    {
        UpdatePalletConsoleInterface((uid, component)); // Frontier: EntityUid<Entity
    }

    private void OnCargoShuttleConsoleStartup(EntityUid uid, CargoShuttleConsoleComponent component, ComponentStartup args)
    {
        var station = _station.GetOwningStation(uid);
        UpdateShuttleState(uid, station);
    }

    private void UpdateShuttleState(EntityUid uid, EntityUid? station = null)
    {
        TryComp<StationCargoOrderDatabaseComponent>(station, out var orderDatabase);
        TryComp<CargoShuttleComponent>(orderDatabase?.Shuttle, out var shuttle);

        var orders = GetProjectedOrders(uid, station ?? EntityUid.Invalid, orderDatabase, shuttle);
        var shuttleName = orderDatabase?.Shuttle != null ? MetaData(orderDatabase.Shuttle.Value).EntityName : string.Empty;

        if (_uiSystem.HasUi(uid, CargoConsoleUiKey.Shuttle))
            _uiSystem.SetUiState(uid, CargoConsoleUiKey.Shuttle, new CargoShuttleConsoleBoundUserInterfaceState(
                station != null ? MetaData(station.Value).EntityName : Loc.GetString("cargo-shuttle-console-station-unknown"),
                string.IsNullOrEmpty(shuttleName) ? Loc.GetString("cargo-shuttle-console-shuttle-not-found") : shuttleName,
                orders
            ));
    }

    #endregion

    private void OnTradeSplit(EntityUid uid, TradeStationComponent component, ref GridSplitEvent args)
    {
        // If the trade station gets bombed it's still a trade station.
        foreach (var gridUid in args.NewGrids)
        {
            EnsureComp<TradeStationComponent>(gridUid);
        }
    }

    #region Shuttle

    /// <summary>
    /// Returns the orders that can fit on the cargo shuttle.
    /// </summary>
    private List<CargoOrderData> GetProjectedOrders(
        EntityUid consoleUid,
        EntityUid shuttleUid,
        StationCargoOrderDatabaseComponent? component = null,
        CargoShuttleComponent? shuttle = null)
    {
        var orders = new List<CargoOrderData>();

        if (component == null || shuttle == null || component.Orders.Count == 0)
            return orders;

        var spaceRemaining = GetCargoSpace(consoleUid, shuttleUid);
        for (var i = 0; i < component.Orders.Count && spaceRemaining > 0; i++)
        {
            var order = component.Orders[i];
            if (order.Approved)
            {
                var numToShip = order.OrderQuantity - order.NumDispatched;
                if (numToShip > spaceRemaining)
                {
                    // We won't be able to fit the whole order on, so make one
                    // which represents the space we do have left:
                    var reducedOrder = new CargoOrderData(order.OrderId,
                            order.ProductId, order.ProductName, order.Price, spaceRemaining, order.Requester, order.Reason, null);
                    orders.Add(reducedOrder);
                }
                else
                {
                    orders.Add(order);
                }
                spaceRemaining -= numToShip;
            }
        }

        return orders;
    }

    /// <summary>
    /// Get the amount of space the cargo shuttle can fit for orders.
    /// </summary>
    private int GetCargoSpace(EntityUid consoleUid, EntityUid gridUid)
    {
        var space = GetCargoPallets(consoleUid, gridUid, BuySellType.Buy).Count;
        return space;
    }

    /// <summary>
    /// Frontier addition - calculates distance between two EntityCoordinates
    /// Used to check for cargo pallets around the console instead of on the grid.
    /// </summary>
    /// <param name="point1">first point to get distance between</param>
    /// <param name="point2">second point to get distance between</param>
    /// <returns></returns>
    public static double CalculateDistance(EntityCoordinates point1, EntityCoordinates point2)
    {
        var xDifference = point2.X - point1.X;
        var yDifference = point2.Y - point1.Y;

        return Math.Sqrt(xDifference * xDifference + yDifference * yDifference);
    }

    /// GetCargoPallets(gridUid, BuySellType.Sell) to return only Sell pads
    /// GetCargoPallets(gridUid, BuySellType.Buy) to return only Buy pads
    private List<(EntityUid Entity, CargoPalletComponent Component, TransformComponent PalletXform)> GetCargoPallets(EntityUid consoleUid, EntityUid gridUid, BuySellType requestType = BuySellType.All)
    {
        _pads.Clear();

        var query = AllEntityQuery<CargoPalletComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var comp, out var compXform))
        {
            // Frontier addition - To support multiple cargo selling stations we add a distance check for the pallets.
            var distance = CalculateDistance(compXform.Coordinates, Transform(consoleUid).Coordinates);
            var maxPalletDistance = DefaultPalletDistance;

            // Get the mapped checking distance from the console
            if (TryComp<CargoPalletConsoleComponent>(consoleUid, out var cargoShuttleComponent))
            {
                maxPalletDistance = cargoShuttleComponent.PalletDistance;
            }

            var isTooFarAway = distance > maxPalletDistance;
            // End of Frontier addition

            if (compXform.ParentUid != gridUid ||
                !compXform.Anchored || isTooFarAway)
            {
                continue;
            }

            if ((requestType & comp.PalletType) == 0)
            {
                continue;
            }

            _pads.Add((uid, comp, compXform));

        }

        return _pads;
    }

    private List<(EntityUid Entity, CargoPalletComponent Component, TransformComponent Transform)>
        GetFreeCargoPallets(EntityUid gridUid,
            List<(EntityUid Entity, CargoPalletComponent Component, TransformComponent Transform)> pallets)
    {
        _setEnts.Clear();

        List<(EntityUid Entity, CargoPalletComponent Component, TransformComponent Transform)> outList = new();

        foreach (var pallet in pallets)
        {
            var aabb = _lookup.GetAABBNoContainer(pallet.Entity, pallet.Transform.LocalPosition, pallet.Transform.LocalRotation);

            if (_lookup.AnyLocalEntitiesIntersecting(gridUid, aabb, LookupFlags.Dynamic))
                continue;

            outList.Add(pallet);
        }

        return outList;
    }

    #endregion

    #region Station

    private bool SellPallets(Entity<CargoPalletConsoleComponent> consoleUid, EntityUid gridUid, out double amount, out double noMultiplierAmount, out double blackMarketTaxAmount, out double frontierTaxAmount, out double nfsdTaxAmount, out double medicalTaxAmount, MarketRecord? capture = null) // Frontier: first arg to Entity, add noMultiplierAmount. Triad: add capture
    {
        GetPalletGoods(consoleUid, gridUid, out var toSell, out amount, out noMultiplierAmount, out blackMarketTaxAmount, out frontierTaxAmount, out nfsdTaxAmount, out medicalTaxAmount, capture); // Frontier: add noMultiplierAmount. Triad: add capture

        Log.Debug($"Cargo sold {toSell.Count} entities for {amount} (plus {noMultiplierAmount} without mods). (Taxes: Black Market: {blackMarketTaxAmount}, CO: {frontierTaxAmount}, TSFMC: {nfsdTaxAmount}, MD: {medicalTaxAmount})"); // Frontier: add section in parentheses

        if (toSell.Count == 0)
            return false;

        var ev = new EntitySoldEvent(toSell, gridUid); // Frontier: add gridUid
        RaiseLocalEvent(ref ev);

        // Collect all container entities and their contained entities recursively
        var allEntsToDelete = new HashSet<EntityUid>(toSell);

        // Make sure we delete all contained entities as well
        foreach (var ent in toSell)
        {
            if (TryComp<ContainerManagerComponent>(ent, out var containerManager))
            {
                // Recursively gather all entities inside containers
                var containedEntities = new HashSet<EntityUid>();
                GatherContainedEntities(ent, containerManager, containedEntities);
                allEntsToDelete.UnionWith(containedEntities);
            }
        }

        foreach (var ent in allEntsToDelete)
        {
            Del(ent);
        }

        return true;
    }

    /// <summary>
    /// Recursively gathers all entities inside containers
    /// </summary>
    private void GatherContainedEntities(EntityUid uid, ContainerManagerComponent containerManager, HashSet<EntityUid> containedEntities)
    {
        foreach (var container in containerManager.Containers.Values)
        {
            foreach (var entity in container.ContainedEntities)
            {
                containedEntities.Add(entity);

                // Recursively check containers inside this entity
                if (TryComp<ContainerManagerComponent>(entity, out var nestedContainers))
                {
                    GatherContainedEntities(entity, nestedContainers, containedEntities);
                }
            }
        }
    }

    private void GetPalletGoods(Entity<CargoPalletConsoleComponent> consoleUid, EntityUid gridUid, out HashSet<EntityUid> toSell, out double amount, out double noMultiplierAmount, out double blackMarketTaxAmount, out double frontierTaxAmount, out double nfsdTaxAmount, out double medicalTaxAmount, MarketRecord? capture = null) // Frontier: first arg to Entity, add noMultiplierAmount. Triad: add capture
    {
        // Triad: reused across entities so a full pallet does not allocate a list per item.
        var pricedNodes = capture != null ? new List<PricedNode>() : null;
        amount = 0;
        noMultiplierAmount = 0;
        blackMarketTaxAmount = 0;
        frontierTaxAmount = 0;
        nfsdTaxAmount = 0;
        medicalTaxAmount = 0;
        toSell = new HashSet<EntityUid>();

        foreach (var (palletUid, _, _) in GetCargoPallets(consoleUid, gridUid, BuySellType.Sell))
        {
            // Containers should already get the sell price of their children so can skip those.
            _setEnts.Clear();

            _lookup.GetEntitiesIntersecting(palletUid, _setEnts,
                LookupFlags.Dynamic | LookupFlags.Sundries);

            foreach (var ent in _setEnts)
            {
                // Dont sell:
                // - anything already being sold
                // - anything anchored (e.g. light fixtures)
                // - anything blacklisted (e.g. players).
                if (toSell.Contains(ent) ||
                    _xformQuery.TryGetComponent(ent, out var xform) &&
                    (xform.Anchored || !CanSell(ent, xform)))
                {
                    continue;
                }

                // Frontier: whitelisted consoles
                if (_whitelist.IsWhitelistFail(consoleUid.Comp.Whitelist, ent))
                    continue;
                // End Frontier

                if (_blacklistQuery.HasComponent(ent))
                    continue;

                // Mono: Use vending machine discount pricing for cargo sales
                // Triad: the collecting variant returns the same total and additionally reports what
                // each contained entity contributed, so a crate breaks down to its contents instead
                // of recording as one aggregate price.
                double price;
                if (pricedNodes != null)
                {
                    pricedNodes.Clear();
                    price = _pricing.GetPriceWithVendingDiscountCollecting(ent, gridUid, pricedNodes);
                }
                else
                {
                    price = _pricing.GetPriceWithVendingDiscount(ent, gridUid);
                }

                if (price == 0)
                    continue;
                toSell.Add(ent);

                var station = _station.GetOwningStation(ent);
                double multiplier = 1;

                if (station != null
                    && !HasComp<TradeCrateWildcardDestinationComponent>(station)
                    && TryComp<MarketModifierComponent>(consoleUid, out var marketModifier)
                    && !HasComp<IgnoreMarketModifierComponent>(ent)
                    && !marketModifier.Buy
                    && !HasComp<TradeCrateComponent>(ent))
                {
                    multiplier = marketModifier.Mod;
                }

                if (station != null
                    && TryComp<TradeCrateWildcardDestinationComponent>(station, out var wildcard)
                    && HasComp<TradeCrateComponent>(ent))
                {
                    multiplier = wildcard.ValueMultiplier;
                }

                // Frontier: check for items that are immune to market modifiers
                if (HasComp<IgnoreMarketModifierComponent>(ent))
                    noMultiplierAmount += price;
                else
                    amount += price * multiplier;


                // End Frontier: check for items that are immune to market modifiers
                // Triad: record what this entity and its contents were each worth. The effective
                // multiplier is known here and nowhere later, so the lines are emitted here.
                if (capture != null && pricedNodes != null)
                {
                    var effectiveMultiplier = HasComp<IgnoreMarketModifierComponent>(ent) ? 1.0 : multiplier;
                    CaptureSaleLines(capture, pricedNodes, effectiveMultiplier);
                }
                // Mono: ItemTaxs to budgets.
                if (TryComp<ItemTaxComponent>(ent, out var itemTax))
                {
                    foreach (var (account, taxCoeff) in itemTax.TaxAccounts)
                    {
                        switch (account)
                        {
                            case SectorBankAccount.BlackMarket:
                                blackMarketTaxAmount += price * taxCoeff;
                                break;
                            case SectorBankAccount.Frontier:
                                frontierTaxAmount += price * taxCoeff;
                                break;
                            case SectorBankAccount.TDF:
                                nfsdTaxAmount += price * taxCoeff;
                                break;
                            case SectorBankAccount.Medical:
                                medicalTaxAmount += price * taxCoeff;
                                break;
                            default:
                                break;
                        }
                    }
                }
                // End Mono
            }
        }
    }

    // Triad: begin, market data capture for pallet sales.
    /// <summary>
    /// Files the completed sale: the header, the four sector taxes as splits, and the payout trace.
    /// Lines were already attached during pricing, where the multipliers were known.
    /// </summary>
    private void CapturePalletSale(EntityUid consoleUid, CargoPalletConsoleComponent component,
        MarketRecord? capture, EntityUid actor, double exactPayout, int paidPayout,
        double blackMarketTax, double frontierTax, double nfsdTax, double medicalTax)
    {
        if (capture == null)
            return;

        var taxTotal = blackMarketTax + frontierTax + nfsdTax + medicalTax;

        capture.Kind = MarketTransactionKind.PalletSale;
        capture.LedgerEntryType = null;
        // The seller is paid in physical cash, not into a bank. Without this the row cannot be
        // reconciled against any balance, because no balance moved.
        capture.Rail = MarketRail.Cash;
        capture.Gross = (long) Math.Round(exactPayout * 100);
        capture.Tax = (long) Math.Round(taxTotal * 100);
        capture.Net = paidPayout * 100L;
        capture.Succeeded = true;
        capture.ConsoleProto = MetaData(consoleUid).EntityPrototype?.ID;
        capture.LocationName = GetCaptureLocationName(consoleUid);

        if (TryComp<MarketModifierComponent>(consoleUid, out var mod))
            capture.MarketMod = mod.Mod;

        if (_playerManager.TryGetSessionByEntity(actor, out var session))
            capture.ActorUserId = session.UserId;

        AddTaxSplit(capture, SectorBankAccount.BlackMarket, LedgerEntryType.BlackMarketSales, LedgerEntryType.BlackMarketPenalties, blackMarketTax);
        AddTaxSplit(capture, SectorBankAccount.Frontier, LedgerEntryType.ColonialOutpostSales, LedgerEntryType.ColonialOutpostPenalties, frontierTax);
        AddTaxSplit(capture, SectorBankAccount.TDF, LedgerEntryType.TSFMCSales, LedgerEntryType.TSFMCPenalties, nfsdTax);
        AddTaxSplit(capture, SectorBankAccount.Medical, LedgerEntryType.MedicalSales, LedgerEntryType.MedicalPenalties, medicalTax);

        // The seller's own share, so splits over one transaction sum to its gross.
        capture.AddSplit("Player", "PalletSale", capture.Net);

        // The payout trace. Only the parts that are not already columns: what was rounded away, and
        // whether the lines actually reconcile against what was paid.
        var lineTotal = capture.LineTotal();
        capture.Calc =
            $"{{\"exactPayout\":{exactPayout:0.####},\"paidPayout\":{paidPayout}," +
            $"\"roundingLoss\":{exactPayout - paidPayout:0.####}," +
            $"\"lineTotalMinor\":{lineTotal},\"lineCount\":{capture.Lines.Count}}}";

        _market.Record(capture);
    }

    private void AddTaxSplit(MarketRecord capture, SectorBankAccount account,
        LedgerEntryType income, LedgerEntryType penalty, double amount)
    {
        if (amount == 0)
            return;

        // Negative tax is a penalty withdrawn from the account, which the sale path handles as a
        // separate ledger type. Same split, opposite sign, so summing an account still nets out.
        var entryType = amount > 0 ? income : penalty;
        capture.AddSplit(account.ToString(), entryType.ToString(), (long) Math.Round(amount * 100));
    }

    /// <summary>
    /// The station name for a console, which is copied from the point of interest prototype at
    /// spawn and is therefore stable across rounds. Null off-station.
    /// </summary>
    private string? GetCaptureLocationName(EntityUid uid)
    {
        var station = _station.GetOwningStation(uid);
        return station == null ? null : MetaData(station.Value).EntityName;
    }

    /// <summary>
    /// Turns one entity's priced tree into line rows. The node the traversal started from becomes a
    /// root line and everything it contained hangs off it, each line carrying only what that entity
    /// was worth on its own, so every line of the sale sums to the payout.
    /// </summary>
    private void CaptureSaleLines(MarketRecord capture, List<PricedNode> nodes, double multiplier)
    {
        // Node index to line index, or null where a node was skipped. The record's line list spans
        // every entity on the pallet, so a node's own index is not its line index.
        var lineIndices = new int?[nodes.Count];

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];

            // Minor units, and the multiplier is applied because that is what the seller was
            // actually paid for this item.
            var lineTotal = (long) Math.Round(node.OwnPrice * multiplier * 100);

            // Skip worthless nodes, except the one the traversal started from, which anchors the
            // tree. The pricing recursion descends into every container an entity has, and solutions
            // are entities in containers, so a steel sheet alone yields a node for its own steel
            // solution. Those are not traded goods and a corpus full of them teaches nothing. The
            // sale itself already refuses zero-priced entities at the top level, so this matches.
            if (lineTotal == 0 && node.ParentIndex != null)
            {
                lineIndices[i] = null;
                continue;
            }

            var proto = MetaData(node.Uid).EntityPrototype?.ID ?? "unknown";
            var quantity = TryComp<StackComponent>(node.Uid, out var stack) ? stack.Count : 1;
            var unitPrice = quantity > 0 ? lineTotal / quantity : lineTotal;
            var source = InferPriceSource(node.Uid);

            // A skipped parent re-parents its children onto the nearest ancestor that was kept, so
            // dropping a solution entity never orphans anything hanging off it.
            int? parentLine = null;
            var walk = node.ParentIndex;
            while (walk is { } w)
            {
                if (lineIndices[w] is { } kept)
                {
                    parentLine = kept;
                    break;
                }
                walk = nodes[w].ParentIndex;
            }

            lineIndices[i] = parentLine is { } p
                ? capture.AddChildLine(p, proto, MarketDirection.Sale, quantity, unitPrice, lineTotal, source, (float) multiplier)
                : capture.AddLine(proto, MarketDirection.Sale, quantity, unitPrice, lineTotal, source, (float) multiplier);
        }
    }

    /// <summary>
    /// Which price provider most likely produced an entity's price.
    ///
    /// <para>This is an inference from which components the entity carries, not a fact reported by
    /// the pricing system, because <c>PriceCalculationEvent</c> does not say who answered it. It is
    /// right for the ordinary cases and exists so a model can exclude the ones that are not real
    /// valuations. Treat a disagreement between this and the price as the inference being wrong.</para>
    /// </summary>
    private MarketPriceSource InferPriceSource(EntityUid uid)
    {
        if (HasComp<MobPriceComponent>(uid))
            return MarketPriceSource.Mob;
        if (HasComp<StackPriceComponent>(uid))
            return MarketPriceSource.Stack;
        if (HasComp<StaticPriceComponent>(uid))
            return MarketPriceSource.Static;
        if (HasComp<MaterialComponent>(uid))
            return MarketPriceSource.Material;
        if (HasComp<SolutionContainerManagerComponent>(uid))
            return MarketPriceSource.Solution;

        return MarketPriceSource.Unknown;
    }
    // Triad: end

    private bool CanSell(EntityUid uid, TransformComponent xform)
    {
        // Frontier: Look for blacklisted items and stop the selling of the container.
        if (_blacklistQuery.HasComponent(uid))
            return false;

        // Frontier: allow selling dead mobs, Mono: and mecha
        if (_mobQuery.TryComp(uid, out var mob) && mob.CurrentState != MobState.Dead && !TryComp<MechComponent>(uid, out _))
            return false;
        // End Frontier

        var complete = IsBountyComplete(uid, out var bountyEntities);

        // Recursively check for mobs at any point.
        var children = xform.ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (complete && bountyEntities.Contains(child))
                continue;

            if (!CanSell(child, _xformQuery.GetComponent(child)))
                return false;
        }

        return true;
    }

    private void OnPalletSale(EntityUid uid, CargoPalletConsoleComponent component, CargoPalletSellMessage args)
    {
        var xform = Transform(uid);

        if (xform.GridUid is not { Valid: true } gridUid)
        {
            _uiSystem.SetUiState(uid, CargoPalletConsoleUiKey.Sale,
            new CargoPalletConsoleInterfaceState(0, 0, false));
            return;
        }

        // Triad: build the record before the sale so lines can be collected during pricing. Null
        // when capture is off, which keeps the collecting price path and its allocations out of the
        // loop entirely rather than building a record nobody reads.
        var capture = _market.LinesEnabled ? new MarketRecord() : null;

        if (!SellPallets((uid, component), gridUid, out var price, out var noMultiplierPrice, out var blackMarketTaxAmount, out var frontierTaxAmount, out var nfsdTaxAmount, out var medicalTaxAmount, capture)) // Frontier: convert first arg to Entity, add noMultiplierPrice. Triad: add capture
            return;

        price += noMultiplierPrice;

        // End Frontier: market modifiers & immune objects
        // Mono Begin
        if (blackMarketTaxAmount > 0)
            _bank.TrySectorDeposit(SectorBankAccount.BlackMarket, (int)blackMarketTaxAmount, LedgerEntryType.BlackMarketSales, captureStandalone: false); // Triad: attached as a split of the sale
        if (frontierTaxAmount > 0)
            _bank.TrySectorDeposit(SectorBankAccount.Frontier, (int)frontierTaxAmount, LedgerEntryType.ColonialOutpostSales, captureStandalone: false); // Triad: attached as a split of the sale
        if (nfsdTaxAmount > 0)
            _bank.TrySectorDeposit(SectorBankAccount.TDF, (int)nfsdTaxAmount, LedgerEntryType.TSFMCSales, captureStandalone: false); // Triad: attached as a split of the sale
        if (medicalTaxAmount > 0)
            _bank.TrySectorDeposit(SectorBankAccount.Medical, (int)medicalTaxAmount, LedgerEntryType.MedicalSales, captureStandalone: false); // Triad: attached as a split of the sale
        if (blackMarketTaxAmount < 0)
        {
            blackMarketTaxAmount = -blackMarketTaxAmount;
            _bank.TrySectorWithdraw(SectorBankAccount.BlackMarket, (int)blackMarketTaxAmount, LedgerEntryType.BlackMarketPenalties, captureStandalone: false); // Triad: attached as a split of the sale
        }
        if (frontierTaxAmount < 0)
        {
            frontierTaxAmount = -frontierTaxAmount;
            _bank.TrySectorWithdraw(SectorBankAccount.Frontier, (int)frontierTaxAmount, LedgerEntryType.ColonialOutpostPenalties, captureStandalone: false); // Triad: attached as a split of the sale
        }
        if (nfsdTaxAmount < 0)
        {
            nfsdTaxAmount = -nfsdTaxAmount;
            _bank.TrySectorWithdraw(SectorBankAccount.TDF, (int)nfsdTaxAmount, LedgerEntryType.TSFMCPenalties, captureStandalone: false); // Triad: attached as a split of the sale
        }
        if (medicalTaxAmount < 0)
        {
            medicalTaxAmount = -medicalTaxAmount;
            _bank.TrySectorWithdraw(SectorBankAccount.Medical, (int)medicalTaxAmount, LedgerEntryType.MedicalPenalties, captureStandalone: false); // Triad: attached as a split of the sale
        }
        // Mono End
        var stackPrototype = _protoMan.Index<StackPrototype>(component.CashType);
        // Triad: capture before the cast, so the rounding loss is visible rather than inferred.
        CapturePalletSale(uid, component, capture, args.Actor, price, (int) price,
            blackMarketTaxAmount, frontierTaxAmount, nfsdTaxAmount, medicalTaxAmount);
        _stack.Spawn((int)price, stackPrototype, xform.Coordinates);
        _audio.PlayPvs(ApproveSound, uid);
        UpdatePalletConsoleInterface((uid, component)); // Frontier: EntityUid<Entity
    }

    #endregion

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        Reset();
        CleanupTradeCrateDestinations(); // Frontier
    }
}

/// <summary>
/// Event broadcast raised by-ref before it is sold and
/// deleted but after the price has been calculated.
/// </summary>
[ByRefEvent]
public readonly record struct EntitySoldEvent(HashSet<EntityUid> Sold, EntityUid Grid);
