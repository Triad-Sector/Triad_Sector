// Triad: the drydock registry, the shuttle records console's read-only view of the drydock.
using System.Linq;
using Content.Server.Database;
using Content.Shared._NF.ShuttleRecords;
using Content.Shared._NF.ShuttleRecords.Components;
using Content.Shared._Triad.Drydock;
using Robust.Server.GameObjects;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// Answers the shuttle records console, which now shows the drydock registry instead of the round's
/// purchase records. Read only: there is no message here that changes anything.
///
/// <para>Pages travel by message to the viewer who asked, not as console state. The upstream
/// records system still publishes its own state on this interface key whenever a shuttle is
/// bought, and a page carried in state would be overwritten by it.</para>
/// </summary>
public sealed partial class DrydockRegistrySystem : EntitySystem
{
    [Dependency] private DrydockStore _store = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    private const int PageSize = 50;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ShuttleRecordsConsoleComponent, DrydockRegistryRequestPageMessage>(OnRequestPage);
    }

    private async void OnRequestPage(EntityUid uid, ShuttleRecordsConsoleComponent component, DrydockRegistryRequestPageMessage args)
    {
        if (args.Actor is not { Valid: true } actor)
            return;

        try
        {
            var (rows, total) = await _store.QueryRegistry(args.Search, States(args.Status), args.Page, PageSize);

            if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(actor))
                return;

            var ships = rows.Select(ToInfo).ToList();
            _ui.ServerSendUiMessage(uid, ShuttleRecordsUiKey.Default, new DrydockRegistryPageMessage(ships, total, args.Page, PageSize), actor);
        }
        catch (Exception e)
        {
            Log.Error($"Drydock: registry page for {ToPrettyString(actor)} at {ToPrettyString(uid)} threw: {e}");
        }
    }

    /// <summary>The row states each registry status covers. Null is every hull.</summary>
    private static DrydockShipState[]? States(DrydockRegistryStatus? status)
    {
        return status switch
        {
            DrydockRegistryStatus.Stored => new[] { DrydockShipState.Stored, DrydockShipState.InEscrow },
            DrydockRegistryStatus.Underway => new[] { DrydockShipState.CheckedOut },
            DrydockRegistryStatus.Impounded => new[] { DrydockShipState.Impounded },
            DrydockRegistryStatus.Deregistered => new[] { DrydockShipState.Sold, DrydockShipState.Destroyed, DrydockShipState.Abandoned },
            _ => null,
        };
    }

    private static DrydockRegistryShipInfo ToInfo(DrydockShip row)
    {
        var status = row.State switch
        {
            DrydockShipState.Stored or DrydockShipState.InEscrow => DrydockRegistryStatus.Stored,
            DrydockShipState.CheckedOut => DrydockRegistryStatus.Underway,
            DrydockShipState.Impounded => DrydockRegistryStatus.Impounded,
            _ => DrydockRegistryStatus.Deregistered,
        };

        var impounded = status == DrydockRegistryStatus.Impounded;
        return new DrydockRegistryShipInfo(
            row.ShipGuid,
            row.ShipName,
            row.SizeClass,
            row.CaptainName,
            status,
            status == DrydockRegistryStatus.Stored ? row.BerthId : null,
            row.State == DrydockShipState.InEscrow,
            impounded ? row.ImpoundFee : 0,
            impounded ? row.ImpoundReason : null);
    }
}
