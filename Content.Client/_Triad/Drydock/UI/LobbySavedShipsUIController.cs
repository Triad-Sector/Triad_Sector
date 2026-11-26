using Content.Shared._Triad.Drydock;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;

namespace Content.Client._Triad.Drydock.UI;

public sealed partial class LobbySavedShipsUIController : UIController, IOnSystemChanged<LobbyDrydockSystem>
{

    [UISystemDependency] private LobbyDrydockSystem? _system = default;

    private LobbySavedShipsWindow? _window;

    public override void Initialize()
    {
    }

    private void OnBerthInfoResponse(LobbyDrydockBerthInfoResponse msg)
    {
        _window?.UpdateUi(msg.Berths);
    }

    private void RequestBerthInfo()
    {
        _system?.RequestBerthInfo();
    }

    private void SendDeleteShipMessage(Guid shipId, string typed)
    {
        _system?.SendDeleteShipMessage(shipId, typed);
    }

    private void SendTransferShipOwnerMessage(Guid shipId, int character)
    {
        _system?.SendTransferShipOwnerMessage(shipId, character);
    }

    public void ToggleWindow()
    {
        if (_window == null)
        {
            _window = new LobbySavedShipsWindow();
            _window.OnClose += () => _window = null;
            _window.OnTransferShip += SendTransferShipOwnerMessage;
            _window.OnDeleteShip += SendDeleteShipMessage;
            RequestBerthInfo();

            _window.OpenCentered();

            return;
        }

        _window.Close();
        _window = null;
    }

    public void OnSystemLoaded(LobbyDrydockSystem system)
    {
        system.BerthInfoResponse += OnBerthInfoResponse;
    }

    public void OnSystemUnloaded(LobbyDrydockSystem system)
    {
        system.BerthInfoResponse -= OnBerthInfoResponse;
    }
}
