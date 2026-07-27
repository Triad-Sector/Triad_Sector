using Content.Shared._Triad.ContrabandPermit;
using Robust.Client.UserInterface;

namespace Content.Client._Triad.ContrabandPermit;

public sealed class ContrabandPermitConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    [ViewVariables]
    private ContrabandPermitConsoleWindow? _menu;

    protected override void Open()
    {
        base.Open();
        _menu = this.CreateWindow<ContrabandPermitConsoleWindow>();
        _menu.SetOwner(Owner);
        _menu.OpenCentered();
        _menu.UpdateUI();
    }
}
