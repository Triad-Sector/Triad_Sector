using System.Threading.Tasks;
using Content.Server._NF.Shipyard.Systems;
using Content.Server.Administration.Logs;
using Content.Server.Chat.Managers;
using Content.Server.Chat.Systems;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Shared._NF.Shipyard.BUI;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Drydock;
using Content.Shared.Database;
using Robust.Shared.Configuration;
using Robust.Shared.Player;

namespace Content.Server._Triad.Drydock;

public sealed partial class LobbyDrydockSystem : EntitySystem
{
    [Dependency] private IAdminLogManager _adminLogger = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private IConfigurationManager _configManager = default!;
    [Dependency] private GameTicker _gameTicker = default!;
    [Dependency] private ShipyardSystem _shipyard = default!;
    [Dependency] private DrydockStore _drydockStore = default!;

    private const string DeleteReason = "Deleted by player";

    [SubscribeNetworkEvent]
    private async void OnLobbyDrydockBerthInfoRequest(LobbyDrydockBerthInfoRequest ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        await ProcessBerthInfoRequest(session);
    }

    [SubscribeNetworkEvent]
    private async void OnLobbyDrydockDeleteShipMessage(LobbyDrydockDeleteShipMessage ev, EntitySessionEventArgs args)
    {
        var (success, shipName) = await TryDeleteStoredShip(args.SenderSession, ev.ShipId, ev.ConfirmText);

        if (!success)
            _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Medium, $"{args.SenderSession} could not delete their ship {shipName} (ID: {ev.ShipId}) from the lobby interface");
        else
            _adminLogger.Add(LogType.ShipYardUsage, LogImpact.High, $"{args.SenderSession} deleted their ship {shipName} (ID: {ev.ShipId}) permanently by using the lobby interface");
    }

    internal async Task<(bool success, string shipName)> TryDeleteStoredShip(ICommonSession player, Guid shipId, string typedName)
    {
        var header = await _drydockStore.GetShipHeader(shipId);
        var owner = player.UserId.UserId;

        var shipName = header != null ? header.ShipName : "Unknown";

        if (header != null && header.OwnerUserId != owner)
        {
            _adminLogger.Add(LogType.ShipYardUsage, LogImpact.Extreme, $"{player} ATTEMPTED TO DELETE SHIP {shipId} THAT THEY DID NOT OWN!");
            _chat.SendAdminAlert($"{player} ATTEMPTED TO DELETE SHIP {shipName} (ID: {shipId}) THAT THEY DID NOT OWN!");
            return (false, shipName);
        }

        if (header == null)
            return (false, shipName);

        if (header.State != DrydockShipState.Stored)
            return (false, shipName);

        if (!ShipyardSystem.TypedNameMatches(typedName, shipName))
            return (false, shipName);

        var result = await _drydockStore.TryDeleteShip(shipId, owner, _gameTicker.RoundIdOrNull, DeleteReason);

        if (result == DrydockBerthResult.Success)
        {
            await ProcessBerthInfoRequest(player); // Re-send berth info data to the player
            return (true, shipName);
        }
        else
        {
            return (false, shipName);
        }
    }

    private async Task<bool> ProcessBerthInfoRequest(ICommonSession requester)
    {
        var owner = requester.UserId.UserId;

        var slotsTask = _drydockStore.GetBerths(owner);
        var appraisalsTask = _drydockStore.GetCurrentAppraisals(owner);
        await Task.WhenAll(slotsTask, appraisalsTask);
        var slots = await slotsTask;
        var appraisals = await appraisalsTask;

        var berthInfos = new List<DrydockBerthInfo>();

        var refund = _configManager.GetCVar(TriadCCVars.DrydockBerthRefund);
        foreach (var slot in slots)
        {
            if (slot.Occupant is not { } occupant || occupant.State != DrydockShipState.Stored)
                continue;

            appraisals.TryGetValue(occupant.ShipGuid, out var appraisal);

            berthInfos.Add(new DrydockBerthInfo(
                slot.Berth.BerthId,
                slot.Berth.MaxSizeClass,
                (int)(slot.Berth.PricePaid * refund),
                null,
                null,
                occupant.ShipGuid,
                occupant.ShipName,
                occupant.SizeClass,
                occupant.State.ToString(),
                null,
                null,
                null,
                null,
                appraisal,
                occupant.CharacterIndex));
        }

        var response = new LobbyDrydockBerthInfoResponse(berthInfos);
        RaiseNetworkEvent(response, requester.Channel);
        return berthInfos.Count > 0;
    }
}
