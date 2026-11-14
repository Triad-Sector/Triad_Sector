// Triad: console failure feedback. The shipyard console's popups were removed (its buttons carry
// every state), which left a refused press with nothing but the deny sound. A refusal now also
// writes its reason to the chat of the player who pressed, and nobody else. Successes write nothing:
// the console already shows them.

using Content.Server.Administration.Logs;
using Content.Server.Chat.Managers;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Chat;
using Content.Shared.Database;
using Robust.Shared.Utility;

namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem
{
    [Dependency] private IChatManager _chatManager = default!;

    private static readonly Color ConsoleErrorColor = Color.FromHex("#ff6b6b");

    /// <summary>
    /// Refuses a press at a shipyard console: the deny sound, and <paramref name="reason"/> in the
    /// pressing player's chat. Use it at every refusal a player can cause or needs to act on.
    /// </summary>
    internal void DenyWithReason(EntityUid player, EntityUid console, ShipyardConsoleComponent component, string reason)
    {
        PlayDenySound(player, console, component);
        ReportConsoleError(player, reason);
    }

    /// <summary>
    /// Writes a console failure to the chat of <paramref name="player"/> only, and to the admin log,
    /// without the deny sound (for failures that arrive after an await, once the press is long over).
    /// </summary>
    internal void ReportConsoleError(EntityUid player, string reason)
    {
        if (!_player.TryGetSessionByEntity(player, out var session))
            return;

        var wrapped = Loc.GetString("chat-manager-server-wrap-message", ("message", FormattedMessage.EscapeText(reason)));
        _chatManager.ChatMessageToOne(ChatChannel.Server, reason, wrapped, default, false, session.Channel, ConsoleErrorColor);
        _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Low, $"{ToPrettyString(player):actor} was refused at a shipyard console: {reason}");
    }
}
