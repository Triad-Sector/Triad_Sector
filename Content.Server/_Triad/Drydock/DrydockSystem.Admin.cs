using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Content.Server.Database;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The admin half: the decisions the store cannot make because they need the live world. A row
/// says a ship is checked out; only the entity manager knows whether a grid still carries its id.
/// </summary>
public sealed partial class DrydockSystem
{
    /// <summary>
    /// Every hull id that has a live grid in the current round. A retrieved ship, a ship that
    /// failed to store, and a ship mid-store all count; a row that says checked out for any of
    /// them is telling the truth, and restoring it would mint a second copy.
    /// </summary>
    public HashSet<Guid> LiveShipIds()
    {
        var live = new HashSet<Guid>();
        var query = AllEntityQuery<DrydockIdentityComponent>();
        while (query.MoveNext(out var uid, out var identity))
        {
            if (identity.ShipId != Guid.Empty && !TerminatingOrDeleted(uid))
                live.Add(identity.ShipId);
        }

        return live;
    }

    public bool IsShipLive(Guid shipId)
    {
        return TryGetLiveShipGrid(shipId, out _);
    }

    /// <summary>
    /// Same walk as <see cref="TryGetLiveShipGrid"/>, taken once for a caller judging many rows. A
    /// snapshot: a caller that awaits between rows re-checks each grid before trusting it.
    /// </summary>
    public Dictionary<Guid, EntityUid> LiveShipGridMap()
    {
        var live = new Dictionary<Guid, EntityUid>();
        var query = AllEntityQuery<DrydockIdentityComponent>();
        while (query.MoveNext(out var uid, out var identity))
        {
            if (identity.ShipId != Guid.Empty && !TerminatingOrDeleted(uid))
                live[identity.ShipId] = uid;
        }

        return live;
    }

    /// <summary>
    /// The grid carrying a hull this round, for the callers that need to act on it rather than
    /// merely refuse because of it. Same walk as <see cref="IsShipLive"/>, which now asks this.
    /// </summary>
    public bool TryGetLiveShipGrid(Guid shipId, out EntityUid gridUid)
    {
        var query = AllEntityQuery<DrydockIdentityComponent>();
        while (query.MoveNext(out var uid, out var identity))
        {
            if (identity.ShipId != shipId || TerminatingOrDeleted(uid))
                continue;

            gridUid = uid;
            return true;
        }

        gridUid = EntityUid.Invalid;
        return false;
    }

    /// <summary>
    /// Admin restore, guarded: a ship whose grid is still in the world is not lost, and putting
    /// its row back to stored would let it be retrieved into a duplicate while the original
    /// flies. Everything else is the store's decision.
    /// </summary>
    /// <param name="fromSale">Set by the sale reversal, the one path allowed to restore a sold hull.</param>
    public Task<DrydockBerthResult> TryAdminRestore(Guid shipId, int berthId, Guid? actorUserId, int? roundId, string reason, bool fromSale = false)
    {
        if (IsShipLive(shipId))
        {
            Log.Warning($"Drydock: admin restore of {shipId} refused, a live grid still carries it.");
            return Task.FromResult(DrydockBerthResult.WrongState);
        }

        return _store.TryRestoreShip(shipId, berthId, actorUserId, roundId, reason, fromSale);
    }
}
