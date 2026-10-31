using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Toolshed;

namespace Content.Server._Triad.Drydock.Admin;

/// <summary>
/// Starts the tier 1 re-bake sweep by hand, for after a switch was flipped on mid-run or a mapping
/// was hot-fixed. A sibling of <c>drydockadmin</c> rather than a subcommand of it: Toolshed does not
/// let one command mix a bare implementation with subcommands, and the Admin menu runs that one bare.
/// The Toolshed loc test reads <c>command-description-drydockrebake</c>, which has to say the same thing.
/// </summary>
[ToolshedCommand(Name = "drydockrebake"), AdminCommand(AdminFlags.Admin)]
public sealed class DrydockRebakeCommand : ToolshedCommand
{
    [CommandImplementation]
    [CommandDescription("Starts the drydock re-bake sweep over every stored ship, unless one is already running.")]
    [CommandHelp("Usage: drydockrebake")]
    public void Rebake([CommandInvocationContext] IInvocationContext ctx)
    {
        var who = ctx.Session?.Name ?? "server console";
        var key = Sys<DrydockSystem>().StartRebakeSweep($"requested by {who}") switch
        {
            DrydockRebakeStart.Started => "drydock-rebake-command-started",
            DrydockRebakeStart.AlreadyRunning => "drydock-rebake-command-running",
            DrydockRebakeStart.Disabled => "drydock-rebake-command-disabled",
            var other => throw new ArgumentOutOfRangeException(nameof(other), other, null),
        };

        ctx.WriteLine(Loc.GetString(key));
    }
}
