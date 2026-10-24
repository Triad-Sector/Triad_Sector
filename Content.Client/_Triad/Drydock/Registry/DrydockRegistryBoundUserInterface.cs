// Triad: the drydock registry.
using Content.Shared._Triad.Drydock;
using Robust.Client.UserInterface;

namespace Content.Client._Triad.Drydock.Registry;

/// <summary>
/// The shuttle records console's interface, which now opens the drydock registry. Asks for the first
/// page on open and draws whatever page comes back; the server answers the viewer who asked.
/// </summary>
public sealed class DrydockRegistryBoundUserInterface : BoundUserInterface
{
    private DrydockRegistryWindow? _window;

    public DrydockRegistryBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<DrydockRegistryWindow>();
        _window.OnRequestPage += (search, status, page) => SendMessage(new DrydockRegistryRequestPageMessage(search, status, page));
        _window.RequestPage(0);
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        if (message is DrydockRegistryPageMessage page)
            _window?.ShowPage(page);
    }
}
