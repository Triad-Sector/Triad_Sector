using Content.Client._NF.Shipyard.UI;
using Content.Shared.Containers.ItemSlots;
using Content.Shared._NF.Shipyard.BUI;
using Content.Shared._NF.Shipyard.Events;
using static Robust.Client.UserInterface.Controls.BaseButton;
using Robust.Client.UserInterface;
using Content.Client._Triad.Shipyard.Save; // Triad
using Content.Shared._NF.Shipyard.Components; // Triad
using Robust.Client.UserInterface.Controls;
using Robust.Client.Player; // Triad

namespace Content.Client._NF.Shipyard.BUI;

public sealed partial class ShipyardConsoleBoundUserInterface : BoundUserInterface
{
    [Dependency] private IPlayerManager _player = default!; // Triad
    [Dependency] private ShipFileManagementSystem _shipFileManagementSystem = default!;

    private ISawmill _sawmill = default!;

    private ShipyardConsoleMenu? _menu;
    private ShipyardRulesPopup? _rulesWindow;

    public int Balance { get; private set; }

    public int? ShipSellValue { get; private set; }

    public ShipyardConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
        _sawmill = Logger.GetSawmill("shipyard_console_bui"); // Triad
    }

    protected override void Open()
    {
        base.Open();
        _menu = this.CreateWindow<ShipyardConsoleMenu>();
        // Disable the NFSD popup for now.
        // var rules = new FormattedMessage();
        // _rulesWindow = new ShipyardRulesPopup(this);
        _menu.OpenCentered();
        // if (ShipyardConsoleUiKey.Security == (ShipyardConsoleUiKey) UiKey)
        // {
        //     rules.AddText(Loc.GetString($"shipyard-rules-default1"));
        //     rules.PushNewline();
        //     rules.AddText(Loc.GetString($"shipyard-rules-default2"));
        //     _rulesWindow.ShipRules.SetMessage(rules);
        //     _rulesWindow.OpenCentered();
        // }
        _menu.OnClose += Close;
        _menu.OnOrderApproved += ApproveOrder;
        _menu.OnSellShip += SellShip;
        // _menu.OnUnassignDeed += UnassignDeed; // Triad: removed with the Unassign button
        // _menu.OnRenameShip += RenameShip; // Triad: removed with the footer Rename
        // Triad: drydock tab
        _menu.LocalUserId = _player.LocalSession?.UserId.UserId;
        _menu.OnStore += berthId => SendMessage(new ShipyardConsoleStoreMessage(berthId));
        _menu.OnRetrieve += shipId => SendMessage(new ShipyardConsoleRetrieveMessage(shipId));
        _menu.OnImport += ImportLegacyShip; // Triad: legacy import

        // Triad: legacy import. Offer what this machine holds as soon as the console opens; the
        // server answers by putting the acceptable ones in the tab's state. Only the manifest goes
        // now - names, appraisals and the keys the files claim - never the ships themselves.
        SendMessage(new ShipyardConsoleImportManifestMessage(_shipFileManagementSystem.BuildImportManifest()));
        _menu.OnBuyBerth += sizeClass => SendMessage(new ShipyardConsoleBuyBerthMessage(sizeClass));
        _menu.OnSellBerth += berthId => SendMessage(new ShipyardConsoleSellBerthMessage(berthId));
        _menu.OnUpgradeBerth += berthId => SendMessage(new ShipyardConsoleUpgradeBerthMessage(berthId));
        _menu.OnOfferTransfer += (shipId, recipient) => SendMessage(new ShipyardConsoleOfferTransferMessage(shipId, recipient));
        _menu.OnCancelTransfer += transferId => SendMessage(new ShipyardConsoleCancelTransferMessage(transferId));
        _menu.OnAcceptTransfer += transferId => SendMessage(new ShipyardConsoleAcceptTransferMessage(transferId));
        _menu.OnDeclineTransfer += transferId => SendMessage(new ShipyardConsoleDeclineTransferMessage(transferId));
        _menu.OnSellStoredShip += (shipId, typed) => SendMessage(new ShipyardConsoleSellStoredShipMessage(shipId, typed));
        _menu.OnRenameStoredShip += (shipId, name) => SendMessage(new ShipyardConsoleRenameStoredShipMessage(shipId, name));
        _menu.OnMoveStoredShip += (shipId, berthId) => SendMessage(new ShipyardConsoleMoveStoredShipMessage(shipId, berthId));
        _menu.OnRedeemImpound += (shipId, berthId) => SendMessage(new ShipyardConsoleRedeemImpoundMessage(shipId, berthId));
        _menu.OnAbandonShip += (shipId, typed) => SendMessage(new ShipyardConsoleAbandonShipMessage(shipId, typed));
        _menu.OnReissueDeed += ship => SendMessage(new ShipyardConsoleReissueDeedMessage(ship)); // Triad: deed reissue
        var targetIdButton = _menu.FindControl<Button>("TargetIdButton");
        if (targetIdButton != null)
            targetIdButton.OnPressed += _ => SendMessage(new ItemSlotButtonPressedEvent("ShipyardConsole-targetId"));

    }

    /// <summary>
    /// Triad: legacy import. Confirms, then sends the one file's payload. The prompt is the last
    /// point at which this is reversible on an enforcing server, so it asks before the bytes move
    /// rather than after.
    /// </summary>
    private void ImportLegacyShip(string fileId, string shipName)
    {
        var prompt = new DrydockConfirmPrompt(
            Loc.GetString("shipyard-console-import-prompt-title", ("ship", shipName)),
            Loc.GetString("shipyard-console-import-prompt-body", ("ship", shipName)),
            Loc.GetString("shipyard-console-import-button"),
            () =>
            {
                var yaml = _shipFileManagementSystem.ReadShipFile(fileId);
                if (yaml == null)
                {
                    _sawmill.Error($"Could not read '{fileId}' to import it.");
                    return;
                }

                // The server only retires a file the client vouched for, the same gate the old load
                // path used.
                ShipFileManagementSystem.MarkShipPathAsDeletable(fileId);

                // Lock the rows here rather than on the press: up to this point the prompt could
                // still be cancelled, and nothing had been sent to lock the console for.
                _menu?.BeginImportFeedback(fileId);
                SendMessage(new ShipyardConsoleImportMessage(fileId, yaml));
            });

        prompt.OpenCentered();
    }

    private void Populate(List<string> availablePrototypes, List<string> unavailablePrototypes, bool freeListings, bool validId)
    {
        if (_menu == null)
            return;

        _menu.PopulateProducts(availablePrototypes, unavailablePrototypes, freeListings, validId);
        _menu.PopulateCategories(availablePrototypes, unavailablePrototypes);
        _menu.PopulateClasses(availablePrototypes, unavailablePrototypes);
        _menu.PopulateEngines(availablePrototypes, unavailablePrototypes);
    }

    /// <summary>
    /// Triad: drydock tab. The one incremental push on this interface. A store or a retrieve now
    /// runs across many ticks on a budget, and the server reports how far along it is to the
    /// operator who pressed rather than republishing the whole console state per tick.
    /// </summary>
    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        if (message is not ShipyardConsoleDrydockProgressMessage progress)
            return;

        _menu?.SetDrydockProgress(progress.Kind, progress.Percent);
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is not ShipyardConsoleInterfaceState cState)
            return;

        Balance = cState.Balance;
        ShipSellValue = cState.ShipSellValue;
        var castState = (ShipyardConsoleInterfaceState)state;
        Populate(castState.ShipyardPrototypes.available, castState.ShipyardPrototypes.unavailable, castState.FreeListings, castState.IsTargetIdPresent);
        _menu?.UpdateState(castState);
    }

    private void ApproveOrder(ButtonEventArgs args)
    {
        if (args.Button.Parent?.Parent is not VesselRow row || row.Vessel == null)
        {
            return;
        }

        var vesselId = row.Vessel.ID;
        SendMessage(new ShipyardConsolePurchaseMessage(vesselId));
    }

    private void SellShip(ButtonEventArgs args)
    {
        //reserved for a sanity check, but im not sure what since we check all the important stuffs on server already
        SendMessage(new ShipyardConsoleSellMessage());
    }

    // Triad: removed with the Unassign button
    // private void UnassignDeed(ButtonEventArgs args)
    // {
    //     SendMessage(new ShipyardConsoleUnassignDeedMessage());
    // }

    // Triad: removed with the footer Rename
    // private void RenameShip(string newName)
    // {
    //     SendMessage(new ShipyardConsoleRenameMessage(newName));
    // }

}
