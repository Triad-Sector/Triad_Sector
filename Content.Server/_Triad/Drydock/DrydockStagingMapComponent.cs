using System;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// What a drydock staging map is for, which decides what the orphan sweep is allowed to do with it.
/// </summary>
public enum DrydockStagingKind : byte
{
    /// <summary>A store's freeze map. Carries the ship between the freeze and the despawn.</summary>
    Store,

    /// <summary>A retrieve's load map, created by the map loader and tagged afterwards.</summary>
    Retrieve,

    /// <summary>The round-trip validation scratch map. Pre-init, so it never ticks.</summary>
    Validation,

    /// <summary>
    /// An unwind that could not put the ship back. The sweep must not scrap this: it still carries a
    /// live, deeded, fully restored player ship, and deleting it is the one thing the unwind exists
    /// to prevent. Logged loudly and left for an administrator.
    /// </summary>
    Stranded,
}

/// <summary>
/// Marks a private map the drydock made. Its whole job is making a cross-tick intermediate state
/// recoverable: a store that used to run inside one tick now spans hundreds of them, and a pipeline
/// that dies in the middle leaves a map behind that nothing else would ever look at.
/// </summary>
/// <remarks>
/// Process-local only. <see cref="OwnerJobId"/> is a handle into a dictionary that dies with the
/// process, and maps are not persisted anyway, so the sweep is a same-process leak catcher and never
/// a restart recovery. Deliberately not data fields: an unsaved component's data fields are inert,
/// and writing a process-local id into a document would be a lie about what could be read back.
/// </remarks>
[RegisterComponent, UnsavedComponent]
public sealed partial class DrydockStagingMapComponent : Component
{
    public DrydockStagingKind Kind = DrydockStagingKind.Store;

    public Guid ShipId;

    public int OwnerJobId;
}
