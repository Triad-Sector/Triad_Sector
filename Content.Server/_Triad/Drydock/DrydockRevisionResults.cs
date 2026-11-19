namespace Content.Server._Triad.Drydock;

/// <summary>The outcome of <see cref="DrydockStore.TryPinRevision"/> or <see cref="DrydockStore.TryUnpinRevision"/>.</summary>
public enum DrydockPinResult : byte
{
    /// <summary>The flag moved and the timeline row was written.</summary>
    Success,

    /// <summary>
    /// The ship or the revision is unknown. For a pin, also a revision whose document pruning has
    /// already taken: there is nothing left to protect.
    /// </summary>
    NotFound,

    /// <summary>The revision was already pinned, or already unpinned. Nothing was written, including no timeline row.</summary>
    AlreadyInState,
}

/// <summary>The outcome of <see cref="DrydockStore.FileRebakeRevision"/>. Every value but <see cref="Success"/> files nothing.</summary>
public enum DrydockRebakeResult : byte
{
    Success,

    /// <summary>The ship is unknown, or the source revision does not exist on it.</summary>
    NotFound,

    /// <summary>
    /// The ship is not <see cref="Content.Server.Database.DrydockShipState.Stored"/>. A hull that is
    /// out, impounded, in escrow or terminal is not re-baked: the next store of a live hull would
    /// supersede the re-bake anyway, and a verdict state is an admin's to change.
    /// </summary>
    WrongState,

    /// <summary>
    /// The ship's current revision is no longer the source the re-bake was derived from: a store,
    /// promote or other re-bake landed in between. Re-read and derive again from the new current.
    /// </summary>
    StaleSource,
}

/// <summary>What filing a re-bake produced: the outcome, and on success the revision number filed.</summary>
public sealed record DrydockRebakeFileResult(DrydockRebakeResult Outcome, int Revision);
