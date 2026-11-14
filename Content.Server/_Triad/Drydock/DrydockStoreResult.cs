namespace Content.Server._Triad.Drydock;

/// <summary>
/// The outcome of a store attempt. A refusal names its reason, so the console can say what to fix.
/// Every value other than <see cref="Success"/> leaves the live grid as usable as it was.
///
/// <para>That promise now has a middle. Between the freeze and the return leg the ship is not where
/// its owner left it: it is intact, restored and frozen on a private map, and only the unwind's dock
/// back to the station makes "as usable as it was" true again. Any refusal path that does not reach
/// that dock is a ship stranded, which is why the unwind runs from a finally, synchronously, and
/// never observes cancellation.</para>
/// </summary>
public enum DrydockStoreResult : byte
{
    /// <summary>Serialized, filed, and the grid despawned.</summary>
    Success,

    /// <summary>The engine map serializer could not write the grid.</summary>
    SerializeFailed,

    /// <summary>A living, sapient occupant is aboard, by player session or live mind.</summary>
    OrganicsAboard,

    /// <summary>An armed nuke, an active countdown, or a singularity is aboard.</summary>
    HazardAboard,

    /// <summary>
    /// The written document disagrees with the live grid. Aborts before any revision is filed, so a
    /// serializer regression cannot half-commit a ship.
    /// </summary>
    ValidationFailed,

    /// <summary>
    /// Off, or read-only. Read-only stops a suspect build writing more revisions without grounding
    /// the fleet.
    /// </summary>
    Disabled,

    /// <summary>
    /// The owner has no free berth. Checked before the first mutation and again inside the filing
    /// transaction, where the unique index on the berth column settles the race.
    /// </summary>
    NoBerth,

    /// <summary>Free berths exist, none fits this hull class. The player can upgrade or buy one here.</summary>
    BerthTooSmall,

    /// <summary>
    /// A store of this grid is already in flight, or the grid is inside another drydock pipeline: a
    /// retrieve still loading it onto its private map, or an unwind that could not put it back. The
    /// second request does nothing.
    ///
    /// <para>The ordinary answer to a second press, not a rare race. A store used to be one blocked
    /// tick, so hitting this needed two clicks inside the same database round trip; sliced, it lasts
    /// as long as the hull is big, and any second press in that window lands here. The console
    /// message and the progress indicator have to read as the same story - one says the ship is being
    /// stored, the other says how far along it is. Only the admin ripcord can reach the other two
    /// cases, since no console can see a grid on a private map.</para>
    /// </summary>
    InProgress,

    /// <summary>The berth the player named already has a ship in it.</summary>
    BerthOccupied,

    /// <summary>
    /// The pipeline was cancelled before it could finish: a round restart, a shutdown, or the slice
    /// watchdog. The unwind ran, so the ship is back where it was, and nothing was filed.
    /// </summary>
    Cancelled,
}
