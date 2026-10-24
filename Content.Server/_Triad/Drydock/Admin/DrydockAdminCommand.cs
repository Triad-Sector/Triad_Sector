using Content.Server.Administration;
using Content.Server.EUI;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._Triad.Drydock.Admin;

/// <summary>
/// Opens the drydock admin panel for the admin who ran it. The Admin menu's Drydock button runs this
/// by name, and must stay a classic console command: the button's visibility check reads the command
/// list <c>AdminManager.UpdateAdminStatus</c> sends the client, which carries classic commands only,
/// so a Toolshed command hides the button from every admin but a host.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed class DrydockAdminCommand : IConsoleCommand
{
    public string Command => "drydockadmin";
    public string Description => "Opens the drydock admin panel: stored ships, berths, history, restore.";
    public string Help => $"Usage: {Command}";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } player)
        {
            shell.WriteError(Loc.GetString("shell-cannot-run-command-from-server"));
            return;
        }

        var eui = IoCManager.Resolve<EuiManager>();
        eui.OpenEui(new DrydockAdminEui(), player);
    }
}
