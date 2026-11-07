using Content.Server.Database;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The outcome of anything that touches a berth: assign, buy, sell, move, transfer. A refusal names
/// what the player or admin can do about it, which is why it is an enum and not a bool.
/// </summary>
public enum DrydockBerthResult : byte
{
    Success,

    /// <summary>The owner has no free berth at all. Buying one is the fix.</summary>
    NoBerth,

    /// <summary>Free berths exist, none fits this hull class. Upgrade or buy larger.</summary>
    BerthTooSmall,

    /// <summary>The berth already holds a hull. Move it first.</summary>
    BerthOccupied,

    /// <summary>The berth or ship is unknown, or does not belong to whoever is asking.</summary>
    NotFound,

    /// <summary>The ship is not in a state this operation accepts.</summary>
    WrongState,

    /// <summary>Two writers wanted the same berth and the unique index picked one. Nothing was written; retry is safe.</summary>
    Conflict,
}

/// <summary>What filing a revision produced: the outcome, and on success the revision number and the berth the ship now sits in.</summary>
public sealed record DrydockFileResult(DrydockBerthResult Outcome, int Revision, int? BerthId);

/// <summary>One berth and whatever hull is sitting in it, for the terminal and the admin panel.</summary>
public sealed record DrydockBerthSlot(DrydockBerth Berth, DrydockShip? Occupant);
