using Content.Client.Eui;
using Content.Shared._Triad.Shipyard.Admin;
using Content.Shared.Eui;
using JetBrains.Annotations;

namespace Content.Client._Triad.Shipyard.Admin;

[UsedImplicitly]
public sealed class TriadTamperAdminEui : BaseEui
{
    private TriadTamperAdminWindow? _window;

    public override void Opened()
    {
        _window = new TriadTamperAdminWindow(this);
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
        _window.OpenCentered();
    }

    public override void Closed()
    {
        _window?.Close();
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is not TriadTamperAdminEuiState s)
            return;
        _window?.UpdateState(s);
    }

    public void Send(EuiMessageBase msg) => SendMessage(msg);
}
