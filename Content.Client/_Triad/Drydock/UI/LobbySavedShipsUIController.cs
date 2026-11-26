using Content.Shared._Triad.Drydock;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;

namespace Content.Client._Triad.Drydock.UI;

public sealed partial class LobbySavedShipsUIController : UIController, IOnSystemChanged<LobbyDrydockSystem>
{

    [UISystemDependency] private LobbyDrydockSystem? _system = default;

    public bool ActiveWindow = false;

    private LobbySavedShipsWindow? _window;

    public override void Initialize()
    {
    }

    private void OnBerthInfoResponse(LobbyDrydockBerthInfoResponse msg)
    {
        _window?.UpdateUi(msg.Berths);
    }

    private void OnExportFinish()
    {
        _window?.Exporting = false;
    }

    private void RequestBerthInfo()
    {
        _system?.RequestBerthInfo();
    }

    private void SendDeleteShipMessage(Guid shipId, string typed)
    {
        _system?.SendDeleteShipMessage(shipId, typed);
    }

    private void SendExportShipMessage(Guid shipId)
    {
        _system?.SendExportShipMessage(shipId);
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
            _window.OnClose += () =>
            {
                _window = null;
                ActiveWindow = false;
            };

            _window.OnTransferShip += SendTransferShipOwnerMessage;
            _window.OnExportShip += SendExportShipMessage;
            _window.OnDeleteShip += SendDeleteShipMessage;
            RequestBerthInfo();

            _window.OpenCentered();
            ActiveWindow = true;

            return;
        }

        _window.Close();
        ActiveWindow = false;
        _window = null;
    }

    public void OnSystemLoaded(LobbyDrydockSystem system)
    {
        system.BerthInfoResponse += OnBerthInfoResponse;
        system.ExportResponseFinal += OnExportFinish;
    }

    public void OnSystemUnloaded(LobbyDrydockSystem system)
    {
        system.BerthInfoResponse -= OnBerthInfoResponse;
        system.ExportResponseFinal -= OnExportFinish;
    }
}
