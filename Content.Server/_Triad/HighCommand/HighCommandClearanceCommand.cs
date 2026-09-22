using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared._Triad.HighCommand;
using Robust.Shared.Console;

namespace Content.Server._Triad.HighCommand;

/// <summary>
/// Grants or revokes a hull's clearance to FTL to the TFA High Command outpost.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed class HighCommandClearanceCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;

    public string Command => "hcclearance";
    public string Description => "Toggles TFA High Command FTL clearance on the grid you are standing on.";
    public string Help => "hcclearance\nStand on the ship you want cleared and run it. Run it again to revoke.";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } player)
        {
            shell.WriteError("This command must be run by a player standing on a grid.");
            return;
        }

        if (player.AttachedEntity is not { } attached)
        {
            shell.WriteError("You are not attached to an entity.");
            return;
        }

        if (_entities.GetComponentOrNull<TransformComponent>(attached)?.GridUid is not { } grid)
        {
            shell.WriteError("You are not standing on a grid.");
            return;
        }

        if (_entities.RemoveComponent<TfaHighCommandClearanceComponent>(grid))
        {
            shell.WriteLine($"Revoked High Command clearance from {_entities.ToPrettyString(grid)}.");
            return;
        }

        _entities.AddComponent<TfaHighCommandClearanceComponent>(grid);
        shell.WriteLine($"Granted High Command clearance to {_entities.ToPrettyString(grid)}. " +
                        "The outpost will now show in that ship's FTL list.");
    }
}
