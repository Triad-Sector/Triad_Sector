using Content.Client.Lobby.UI;
using Content.Shared._Triad.Drydock;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;

namespace Content.Client._Triad.Drydock.UI;

public sealed partial class LobbySavedShipsUIController : UIController, IOnSystemChanged<LobbyDrydockSystem>
{
    [Dependency] private IClipboardManager _clipboard = default!;

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

    public void ToggleWindow()
    {
        if (_window == null)
        {
            _window = new LobbySavedShipsWindow();
            _window.OnClose += () => _window = null;
            RequestBerthInfo();

            //.CopyButton.OnPressed += _ =>
            //{
            //    _clipboard.SetText(_code.ToString());
            //    _window.CopyButton.Text = Loc.GetString("rmc-ui-link-discord-account-copied");
            //    _window.CopyButton.Disabled = true;
            //    _disableUntil = _timing.RealTime.Add(TimeSpan.FromSeconds(3));
            //};

            _window.OpenCentered();

            //if (_code == default)
            //    _window.CopyButton.Disabled = true;

            //_net.ClientSendMessage(new LinkAccountRequestMsg());
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
