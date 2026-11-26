using Content.Shared._Triad.Drydock;
using Robust.Client.State;
using Content.Client.Lobby;
using Robust.Client.UserInterface;
using Content.Client._Triad.Drydock.UI;
using System.IO;

namespace Content.Client._Triad.Drydock;

public sealed partial class LobbyDrydockSystem : EntitySystem
{
    [Dependency] private IFileDialogManager _dialogManager = default!;
    [Dependency] private IUserInterfaceManager _ui = default!;
    [Dependency] private IStateManager _state = default!;

    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = LogManager.GetSawmill("client.lobby.drydock");
    }

    public event Action<LobbyDrydockBerthInfoResponse>? BerthInfoResponse;
    public event Action? ExportResponseFinal;

    [SubscribeNetworkEvent]
    private void OnLobbyBerthInfoResponse(LobbyDrydockBerthInfoResponse msg)
    {
        if (_state.CurrentState is not LobbyState)
            return;

        BerthInfoResponse?.Invoke(msg);
    }

    [SubscribeNetworkEvent]
    private async void OnLobbyDrydockExportShipResponse(LobbyDrydockExportShipResponse msg)
    {
        if (_state.CurrentState is not LobbyState)
            return;

        // Responses only work if they have a window open
        if (!_ui.GetUIController<LobbySavedShipsUIController>().ActiveWindow)
            return;

        var file = await _dialogManager.SaveFile(new FileDialogFilters(new FileDialogFilters.Group("yml")));

        if (file == null)
        {
            EndExport();
            return;
        }

        try
        {
            await using var writer = new StreamWriter(file.Value.fileStream);
            writer.Write(msg.Yaml);
        }
        catch (Exception exc)
        {
            _sawmill.Error($"Error when exporting profile\n{exc.StackTrace}");
        }
        finally
        {
            EndExport();
            await file.Value.fileStream.DisposeAsync();
        }
    }

    public void RequestBerthInfo()
    {
        if (_state.CurrentState is not LobbyState)
            return;

        var ev = new LobbyDrydockBerthInfoRequest();
        RaiseNetworkEvent(ev);
    }

    public void SendDeleteShipMessage(Guid shipId, string typed)
    {
        if (_state.CurrentState is not LobbyState)
            return;

        var ev = new LobbyDrydockDeleteShipMessage(shipId, typed);
        RaiseNetworkEvent(ev);
    }

    public void SendExportShipMessage(Guid shipId)
    {
        if (_state.CurrentState is not LobbyState)
            return;

        var ev = new LobbyDrydockExportShipMessage(shipId);
        RaiseNetworkEvent(ev);
    }

    public void SendTransferShipOwnerMessage(Guid shipId, int character)
    {
        if (_state.CurrentState is not LobbyState)
            return;

        var ev = new LobbyDrydockTransferShipOwnerMessage(shipId, character);
        RaiseNetworkEvent(ev);
    }

    private void EndExport()
    {
        ExportResponseFinal?.Invoke();
    }
}
