using Content.Shared._Triad.Drydock;
using Robust.Client.State;
using Content.Client.Lobby;

namespace Content.Client._Triad.Drydock;

public sealed partial class LobbyDrydockSystem : EntitySystem
{
    [Dependency] private IStateManager _state = default!;

    public event Action<LobbyDrydockBerthInfoResponse>? BerthInfoResponse;

    [SubscribeNetworkEvent]
    private void OnLobbyBerthInfoResponse(LobbyDrydockBerthInfoResponse msg)
    {
        if (_state.CurrentState is not LobbyState)
            return;

        BerthInfoResponse?.Invoke(msg);
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

    public void SendTransferShipOwnerMessage(Guid shipId, int character)
    {
        if (_state.CurrentState is not LobbyState)
            return;

        var ev = new LobbyDrydockTransferShipOwnerMessage(shipId, character);
        RaiseNetworkEvent(ev);
    }
}
