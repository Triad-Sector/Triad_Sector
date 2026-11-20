using System.Threading.Tasks;
using Content.Server._NF.Shipyard.Systems;
using Content.Shared._NF.Shipyard.BUI;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.Drydock;
using Robust.Shared.Configuration;

namespace Content.Server._Triad.Drydock;

public sealed partial class LobbyDrydockSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _configManager = default!;
    [Dependency] private ShipyardSystem _shipyard = default!;
    [Dependency] private DrydockStore _drydockStore = default!;

    [SubscribeNetworkEvent]
    private async void OnGhostWarpsRequest(LobbyDrydockBerthInfoRequest _, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { Valid: true })
            return;

        var session = args.SenderSession;

        var owner = session.UserId.UserId;

        var slotsTask = _drydockStore.GetBerths(owner);
        var appraisalsTask = _drydockStore.GetCurrentAppraisals(owner);
        await Task.WhenAll(slotsTask, appraisalsTask);
        var slots = await slotsTask;
        var appraisals = await appraisalsTask;

        var berthInfos = new List<DrydockBerthInfo>();

        var refund = _configManager.GetCVar(TriadCCVars.DrydockBerthRefund);
        foreach (var slot in slots)
        {
            int? sellPrice = null;
            if (slot.Occupant != null)
            {
                if (appraisals.TryGetValue(slot.Occupant.ShipGuid, out var appraisal) && appraisal is { } value)
                    sellPrice = value;
            }

            berthInfos.Add(new DrydockBerthInfo(
                slot.Berth.BerthId,
                slot.Berth.MaxSizeClass,
                (int)(slot.Berth.PricePaid * refund),
                null,
                null,
                slot.Occupant?.ShipGuid,
                slot.Occupant?.ShipName,
                slot.Occupant?.SizeClass,
                slot.Occupant?.State.ToString(),
                sellPrice,
                null,
                null,
                null,
                null));
        }

        var response = new LobbyDrydockBerthInfoResponse(berthInfos);
        RaiseNetworkEvent(response, args.SenderSession.Channel);
    }
}
