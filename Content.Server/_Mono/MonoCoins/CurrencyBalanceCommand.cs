using System.Linq;
using Content.Server.Administration;
using Content.Server.Database;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Player;

namespace Content.Server._Mono.MonoCoins;

/// <summary>
/// Admin command for checking any player's MonoCoins balance.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed partial class CurrencyBalanceCommand : LocalizedCommands
{
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private IServerDbManager _db = default!;

    public override string Command => "currency:balance";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1)
        {
            shell.WriteError("Usage: currency:balance <player>");
            return;
        }

        var playerName = args[0];

        // Find the player
        ICommonSession? targetSession = null;
        foreach (var session in _playerManager.Sessions)
        {
            if (session.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase))
            {
                targetSession = session;
                break;
            }
        }

        if (targetSession == null)
        {
            shell.WriteError($"Player '{playerName}' not found.");
            return;
        }

        var userId = targetSession.UserId;

        try
        {
            var balance = await _db.GetMonoCoinsAsync(userId);
            shell.WriteLine($"{playerName} has {balance} MonoCoins");
        }
        catch (Exception ex)
        {
            shell.WriteError($"Database error: {ex.Message}");
        }
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        switch (args.Length)
        {
            case 1:
                var playerNames = _playerManager.Sessions.Select(s => s.Name).ToArray();
                return CompletionResult.FromOptions(playerNames);
            default:
                return CompletionResult.Empty;
        }
    }
}
