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
    public event Action<bool?>? ExportResponseFinal;

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

        var controller = _ui.GetUIController<LobbySavedShipsUIController>();
        if (!controller.Exporting)
            return;

        controller.ExportReplyArrived();

        if (msg.Yaml == null)
        {
            EndExport(false);
            return;
        }

        var file = await _dialogManager.SaveFile(new FileDialogFilters(new FileDialogFilters.Group("yml")));

        if (file == null)
        {
            EndExport(null);
            return;
        }

        var success = false;
        try
        {
            await using var writer = new StreamWriter(file.Value.fileStream);
            await writer.WriteAsync(msg.Yaml);
            success = true;
        }
        catch (Exception exc)
        {
            _sawmill.Error($"Error when exporting ship\n{exc.StackTrace}");
        }
        finally
        {
            EndExport(success);
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

    private void EndExport(bool? success)
    {
        ExportResponseFinal?.Invoke(success);
    }
}
