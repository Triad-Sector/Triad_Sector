using Content.Shared._Triad.ContrabandPermit;
using Content.Shared.Containers.ItemSlots;
using Robust.Client.Player;

namespace Content.Client._Triad.ContrabandPermit;

public sealed partial class ContrabandPermitConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    [Dependency] private IPlayerManager _playerManager = default!;

    [ViewVariables]
    private ContrabandPermitConsoleWindow? _menu;

    protected override void Open()
    {
        base.Open();

        _menu = new(_playerManager);
        _menu.SetOwner(Owner);
        _menu.OpenCentered();

        _menu.OnClose += Close;

        if (!EntMan.TryGetComponent<ContrabandPermitConsoleComponent>(Owner, out var consoleComp))
            return;

        _menu.EjectButton.OnPressed += _ => SendPredictedMessage(new ItemSlotButtonPressedEvent(consoleComp.ChipSlotContainerId));

        _menu.OnReasonChanged += OnReasonChanged;
        _menu.OnGrantButtonPressed += OnGrantButtonPressed;
        _menu.OnRevokeButtonPressed += OnRevokeButtonPressed;
        _menu.OnPrintButtonPressed += OnPrintButtonPressed;
        _menu.SendFocusChangeMessage += OnSendFocusChangeMessage;
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        var castState = (ContrabandPermitConsoleBuiState) state;
        _menu?.UpdateState(castState);
    }

    private void OnReasonChanged(string reason)
    {
        SendPredictedMessage(new ContrabandPermitConsoleReasonUpdatedMessage(reason));
    }

    private void OnGrantButtonPressed()
    {
        SendPredictedMessage(new ContrabandPermitConsoleGrantButtonPressedMessage());
    }

    private void OnRevokeButtonPressed(string reason)
    {
        SendPredictedMessage(new ContrabandPermitConsoleRevokeButtonPressedMessage(reason));
    }

    private void OnPrintButtonPressed()
    {
        SendPredictedMessage(new ContrabandPermitConsolePrintButtonPressedMessage());
    }

    private void OnSendFocusChangeMessage(ContrabandPermitConsoleOwner? owner, ContrabandPermitConsoleItem? item)
    {
        SendPredictedMessage(new ContrabandPermitConsoleFocusChangeMessage(owner, item));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _menu?.Close();
    }
}
