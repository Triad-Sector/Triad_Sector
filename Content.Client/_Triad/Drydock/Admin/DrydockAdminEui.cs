using Content.Client.Eui;
using Content.Shared._Triad.Drydock.Admin;
using Content.Shared.Eui;
using JetBrains.Annotations;

namespace Content.Client._Triad.Drydock.Admin;

[UsedImplicitly]
public sealed class DrydockAdminEui : BaseEui
{
    private DrydockAdminWindow? _window;

    public override void Opened()
    {
        _window = new DrydockAdminWindow(this);
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
        _window.OpenCentered();
    }

    public override void Closed()
    {
        _window?.Close();
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is not DrydockAdminEuiState s)
            return;

        _window?.UpdateState(s);
    }

    public void Send(EuiMessageBase msg) => SendMessage(msg);
}
